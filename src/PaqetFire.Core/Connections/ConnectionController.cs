using System.Runtime.ExceptionServices;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Core.Connections;

public sealed class ConnectionController : IConnectionController, IDisposable
{
    private readonly IEngineAdapter _paqet;
    private readonly IEngineAdapter _xray;
    private readonly IEngineAdapter _proxiFyre;
    private readonly IReadOnlyList<IExitEngineAdapter> _exits;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private ConnectionState? _transitionState;
    private bool _disposed;

    public ConnectionController(
        IEngineAdapter paqet,
        IEngineAdapter xray,
        IEngineAdapter proxiFyre,
        params IExitEngineAdapter[] exits)
    {
        ArgumentNullException.ThrowIfNull(paqet);
        ArgumentNullException.ThrowIfNull(xray);
        ArgumentNullException.ThrowIfNull(proxiFyre);

        if (paqet.Kind != EngineKind.Paqet)
        {
            throw new ArgumentException("The Paqet adapter must identify itself as EngineKind.Paqet.", nameof(paqet));
        }

        if (xray.Kind != EngineKind.Xray)
        {
            throw new ArgumentException("The Xray adapter must identify itself as EngineKind.Xray.", nameof(xray));
        }

        if (proxiFyre.Kind != EngineKind.ProxiFyre)
        {
            throw new ArgumentException(
                "The ProxiFyre adapter must identify itself as EngineKind.ProxiFyre.",
                nameof(proxiFyre));
        }

        _paqet = paqet;
        _xray = xray;
        foreach (var exit in exits)
        {
            ArgumentNullException.ThrowIfNull(exit, nameof(exits));
            if (!exit.Kind.IsExit())
            {
                throw new ArgumentException($"{exit.Kind} is not an exit engine.", nameof(exits));
            }
        }

        if (exits.DistinctBy(exit => exit.Kind).Count() != exits.Length)
        {
            throw new ArgumentException("Each exit engine may be registered once.", nameof(exits));
        }

        _proxiFyre = proxiFyre;
        _exits = exits;
    }

    // Deliberately does not take the transition gate so the desktop can observe a long
    // transition; adapters must therefore tolerate GetStatusAsync during Start/Stop.
    public async ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var transitionState = _transitionState;
        var paqetStatus = await _paqet.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var xrayStatus = await _xray.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var proxiFyreStatus = await _proxiFyre.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var exitStatus = await GetExitStatusAsync(cancellationToken).ConfigureAwait(false);
        var derivedState = DeriveState(paqetStatus.State, xrayStatus.State, proxiFyreStatus.State, exitStatus?.State);

        if (transitionState is not null && transitionState == _transitionState)
        {
            derivedState = transitionState.Value;
        }

        return new ConnectionStatus(
            derivedState,
            paqetStatus,
            xrayStatus,
            proxiFyreStatus,
            CreateStatusDetail(derivedState, paqetStatus.State, xrayStatus.State, proxiFyreStatus.State, exitStatus),
            DateTimeOffset.UtcNow)
        {
            Exit = exitStatus,
        };
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken) => ConnectAsync(null, cancellationToken);

    public async ValueTask ConnectAsync(Action<ConnectionStage>? stageStarted, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Connecting;

            var paqetInitiallyRunning = await IsRunningAsync(_paqet, cancellationToken).ConfigureAwait(false);
            var xrayInitiallyRunning = await IsRunningAsync(_xray, cancellationToken).ConfigureAwait(false);
            var proxiFyreInitiallyRunning = await IsRunningAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
            var exit = EnabledExit();
            var exitInitiallyRunning = exit is null || await IsRunningAsync(exit, cancellationToken).ConfigureAwait(false);

            if (paqetInitiallyRunning && xrayInitiallyRunning && proxiFyreInitiallyRunning && exitInitiallyRunning)
            {
                return;
            }

            var proxiFyreStartAttempted = false;
            var exitStartAttempted = false;

            // Paqet and Xray start together: Xray only dials Paqet when traffic arrives.
            // ProxiFyre starts last so application capture is only enabled once the
            // complete local SOCKS chain behind it is running.
            stageStarted?.Invoke(ConnectionStage.StartingTransport);
            var (carrierEngine, carrierError) = await StartCarriersAsync(
                    paqetInitiallyRunning ? null : _paqet,
                    xrayInitiallyRunning ? null : _xray,
                    () => stageStarted?.Invoke(ConnectionStage.StartingPolicy),
                    cancellationToken)
                .ConfigureAwait(false);
            var failedEngine = carrierEngine;
            var error = carrierError;
            if (error is null && !exitInitiallyRunning)
            {
                // The exit reaches its network through Paqet, so it starts once Paqet is ready,
                // and capture waits for it: routed traffic must never leave without the exit.
                try
                {
                    stageStarted?.Invoke(ConnectionStage.StartingExit);
                    exitStartAttempted = true;
                    await StartAndVerifyAsync(exit!, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exitError)
                {
                    failedEngine = exit!.Kind;
                    error = exitError;
                }
            }

            if (error is null)
            {
                try
                {
                    if (!proxiFyreInitiallyRunning)
                    {
                        stageStarted?.Invoke(ConnectionStage.StartingCapture);
                        proxiFyreStartAttempted = true;
                        await StartAndVerifyAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception proxiFyreError)
                {
                    failedEngine = EngineKind.ProxiFyre;
                    error = proxiFyreError;
                }
            }

            if (error is not null)
            {
                var rollbackErrors = await RollBackConnectAsync(
                        !paqetInitiallyRunning,
                        !xrayInitiallyRunning,
                        proxiFyreStartAttempted,
                        exitStartAttempted ? exit : null)
                    .ConfigureAwait(false);

                if (error is OperationCanceledException && rollbackErrors.Count == 0)
                {
                    ExceptionDispatchInfo.Throw(error);
                }

                throw new ConnectionTransitionException("connect", failedEngine, error, rollbackErrors);
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Disconnecting;

            // Each failure stops the sequence: tearing down the carrier while capture may
            // still be enabled would leave routed applications attached to nothing.
            try
            {
                await StopAndVerifyAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("disconnect", EngineKind.ProxiFyre, error);
            }

            await StopExitAsync("disconnect", cancellationToken).ConfigureAwait(false);

            try
            {
                await StopAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("disconnect", EngineKind.Xray, error);
            }

            try
            {
                await StopAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("disconnect", EngineKind.Paqet, error);
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public async ValueTask GuardAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Disconnecting;

            try
            {
                await StartAndVerifyAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("arm routing kill switch", EngineKind.ProxiFyre, error);
            }

            await StopExitAsync("arm routing kill switch", cancellationToken).ConfigureAwait(false);

            try
            {
                await StopAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("arm routing kill switch", EngineKind.Xray, error);
            }

            try
            {
                await StopAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("arm routing kill switch", EngineKind.Paqet, error);
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public ValueTask RestartTransportAsync(CancellationToken cancellationToken) =>
        RestartTransportAsync(includePolicy: false, cancellationToken);

    public async ValueTask RestartTransportAsync(bool includePolicy, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Connecting;
            var stoppingEngine = EngineKind.Xray;
            try
            {
                // ProxiFyre keeps capturing throughout, so routed connections fail closed
                // against the stopped Xray listener instead of going direct.
                if (includePolicy)
                {
                    await StopAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
                }

                // An exit's tunnel runs over Paqet and does not survive its restart.
                foreach (var stale in _exits)
                {
                    stoppingEngine = stale.Kind;
                    await StopAndVerifyAsync(stale, cancellationToken).ConfigureAwait(false);
                }

                stoppingEngine = EngineKind.Paqet;
                await StopAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("restart transport", stoppingEngine, error);
            }

            var (failedEngine, startError) = await StartCarriersAsync(
                    _paqet,
                    includePolicy ? _xray : null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (startError is OperationCanceledException)
            {
                ExceptionDispatchInfo.Throw(startError);
            }

            if (startError is not null)
            {
                throw new ConnectionTransitionException("restart transport", failedEngine, startError);
            }

            if (EnabledExit() is { } exit)
            {
                try
                {
                    await StartAndVerifyAsync(exit, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw new ConnectionTransitionException("restart transport", exit.Kind, error);
                }
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transitionGate.Dispose();
    }

    /// <summary>The exit the configuration written for the next start uses, if any.</summary>
    private IExitEngineAdapter? EnabledExit()
    {
        IExitEngineAdapter? enabled = null;
        foreach (var exit in _exits)
        {
            if (!exit.IsEnabled)
            {
                continue;
            }

            if (enabled is not null)
            {
                throw new InvalidOperationException(
                    $"Both the {enabled.Kind} and {exit.Kind} exits are selected; a route uses at most one.");
            }

            enabled = exit;
        }

        return enabled;
    }

    private async ValueTask<EngineStatus?> GetExitStatusAsync(CancellationToken cancellationToken)
    {
        EngineStatus? stray = null;
        foreach (var exit in _exits)
        {
            var status = await exit.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (exit.IsEnabled)
            {
                return status;
            }

            // A disabled exit still counts until it has stopped, so a stray process is not ignored.
            if (stray is null && status.State is not (EngineState.Stopped or EngineState.NotInstalled))
            {
                stray = status;
            }
        }

        return stray;
    }

    private async ValueTask StopExitAsync(string operation, CancellationToken cancellationToken)
    {
        foreach (var exit in _exits)
        {
            try
            {
                await StopAndVerifyAsync(exit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException(operation, exit.Kind, error);
            }
        }
    }

    private static async ValueTask<bool> IsRunningAsync(
        IEngineAdapter adapter,
        CancellationToken cancellationToken)
    {
        var status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (status.State == EngineState.NotInstalled)
        {
            // An optional exit engine explains how to install it.
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(status.Detail)
                    ? $"The {adapter.Kind} engine is not installed."
                    : status.Detail);
        }

        return status.State == EngineState.Running;
    }

    private static async ValueTask StartAndVerifyAsync(
        IEngineAdapter adapter,
        CancellationToken cancellationToken)
    {
        await adapter.StartAsync(cancellationToken).ConfigureAwait(false);
        var status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (status.State != EngineState.Running)
        {
            throw new InvalidOperationException(
                $"The {adapter.Kind} engine reported {status.State} after startup instead of Running.");
        }
    }

    /// <summary>
    /// Starts Paqet and Xray concurrently. A failure cancels the sibling so the caller can
    /// roll back promptly; the reported error is the root cause rather than that cancellation.
    /// </summary>
    private static async ValueTask<(EngineKind Engine, Exception? Error)> StartCarriersAsync(
        IEngineAdapter? paqet,
        IEngineAdapter? xray,
        Action? transportReady,
        CancellationToken cancellationToken)
    {
        using var chain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var paqetStart = paqet is null ? Task.CompletedTask : StartInChainAsync(paqet, chain);
        var xrayStart = xray is null ? Task.CompletedTask : StartInChainAsync(xray, chain);

        Exception? paqetError = null;
        Exception? xrayError = null;
        try
        {
            await paqetStart.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            paqetError = error;
        }

        if (paqetError is null)
        {
            transportReady?.Invoke();
        }

        try
        {
            await xrayStart.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            xrayError = error;
        }

        if (paqetError is not null and not OperationCanceledException || xrayError is null)
        {
            return (EngineKind.Paqet, paqetError);
        }

        return xrayError is not OperationCanceledException || paqetError is null
            ? (EngineKind.Xray, xrayError)
            : (EngineKind.Paqet, paqetError);

        static async Task StartInChainAsync(IEngineAdapter adapter, CancellationTokenSource chain)
        {
            try
            {
                await StartAndVerifyAsync(adapter, chain.Token).ConfigureAwait(false);
            }
            catch
            {
                await chain.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async ValueTask StopAndVerifyAsync(
        IEngineAdapter adapter,
        CancellationToken cancellationToken)
    {
        var status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.State is EngineState.Stopped or EngineState.NotInstalled)
        {
            return;
        }

        await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
        status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (status.State is not (EngineState.Stopped or EngineState.NotInstalled))
        {
            throw new InvalidOperationException(
                $"The {adapter.Kind} engine reported {status.State} after shutdown instead of Stopped.");
        }
    }

    private async ValueTask<IReadOnlyList<Exception>> RollBackConnectAsync(
        bool paqetStartAttempted,
        bool xrayStartAttempted,
        bool proxiFyreStartAttempted,
        IExitEngineAdapter? startedExit)
    {
        List<Exception>? errors = null;

        if (proxiFyreStartAttempted)
        {
            await TryRollbackStopAsync(_proxiFyre).ConfigureAwait(false);
        }

        if (startedExit is not null)
        {
            await TryRollbackStopAsync(startedExit).ConfigureAwait(false);
        }

        if (xrayStartAttempted)
        {
            await TryRollbackStopAsync(_xray).ConfigureAwait(false);
        }

        if (paqetStartAttempted)
        {
            await TryRollbackStopAsync(_paqet).ConfigureAwait(false);
        }

        return errors ?? [];

        async ValueTask TryRollbackStopAsync(IEngineAdapter adapter)
        {
            try
            {
                // Rollback must restore a safe state even after caller cancellation, so
                // adapters are expected to bound their own Stop/Start operations.
                await StopAndVerifyAsync(adapter, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors ??= [];
                errors.Add(new InvalidOperationException(
                    $"Failed to stop {adapter.Kind} while rolling back the connection.",
                    error));
            }
        }
    }

    private static ConnectionState DeriveState(
        EngineState paqet,
        EngineState xray,
        EngineState proxiFyre,
        EngineState? exit = null)
    {
        // The exit belongs with the carriers: it runs exactly when Paqet does.
        EngineState[] carriers = exit is { } exitState ? [paqet, xray, exitState] : [paqet, xray];
        if (carriers.Contains(EngineState.NotInstalled) || proxiFyre == EngineState.NotInstalled)
        {
            return ConnectionState.NotReady;
        }

        if (carriers.All(state => state == EngineState.Running) && proxiFyre == EngineState.Running)
        {
            return ConnectionState.Connected;
        }

        if (carriers.All(state => state == EngineState.Stopped) && proxiFyre == EngineState.Stopped)
        {
            return ConnectionState.Disconnected;
        }

        if (carriers.All(state => state == EngineState.Stopped) && proxiFyre == EngineState.Running)
        {
            return ConnectionState.Guarded;
        }

        if (carriers.Contains(EngineState.Faulted) || proxiFyre == EngineState.Faulted)
        {
            return ConnectionState.Faulted;
        }

        if (carriers.Contains(EngineState.Starting) || proxiFyre == EngineState.Starting)
        {
            return ConnectionState.Connecting;
        }

        if (carriers.Contains(EngineState.Stopping) || proxiFyre == EngineState.Stopping)
        {
            return ConnectionState.Disconnecting;
        }

        return ConnectionState.Degraded;
    }

    private static string? CreateStatusDetail(
        ConnectionState connection,
        EngineState paqet,
        EngineState xray,
        EngineState proxiFyre,
        EngineStatus? exit)
    {
        var engines = exit is not null
            ? $"Paqet: {paqet}, Xray: {xray}, {exit.Engine}: {exit.State}, ProxiFyre: {proxiFyre}"
            : $"Paqet: {paqet}, Xray: {xray}, ProxiFyre: {proxiFyre}";
        return connection switch
        {
            ConnectionState.NotReady => "One or more bundled engines are not installed.",
            ConnectionState.Guarded => "The routing kill switch is active; routed applications cannot connect directly.",
            ConnectionState.Degraded => $"Engine states do not form a safe connection ({engines}).",
            ConnectionState.Faulted => $"An engine is faulted ({engines}).",
            _ => null,
        };
    }
}
