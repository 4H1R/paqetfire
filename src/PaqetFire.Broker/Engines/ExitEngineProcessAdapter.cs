using System.Diagnostics;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// The process lifecycle shared by the exit engines: a process that reaches its network
/// only through Paqet and counts as running once its output reports a tunnel. Subclasses
/// prepare and launch the process and interpret its output.
/// </summary>
public abstract class ExitEngineProcessAdapter : IExitEngineAdapter, IAsyncDisposable
{
    private const int MaximumLogEntries = 80;
    private const int MaximumLogLength = 2048;
    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly TimeSpan readinessTimeout;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly Queue<ExitEngineLogEntry> logs = new();
    private Process? process;
    private TaskCompletionSource? tunnelEstablished;
    private EngineState state = EngineState.Stopped;
    private string? detail;
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private string? lastProblem;
    private bool expectedExit;
    private volatile bool enabled;
    private bool disposed;

    protected ExitEngineProcessAdapter(TimeSpan readinessTimeout)
    {
        this.readinessTimeout = readinessTimeout;
        detail = $"{DisplayName} is stopped.";
    }

    public abstract EngineKind Kind { get; }

    /// <summary>The engine's name in status details, errors, and the activity log.</summary>
    public abstract string DisplayName { get; }

    public bool IsEnabled => enabled;

    /// <summary>Raised when the running process exits without being stopped, so recovery need not wait for a poll.</summary>
    public event EventHandler? UnexpectedExit;

    /// <summary>Guards the engine's state; subclasses use it for the session they parse from the output.</summary>
    protected object StateLock { get; } = new();

    protected abstract string? Version { get; }

    protected abstract string? Endpoint { get; }

    /// <summary>Records whether the configuration written for the next start selects this exit.</summary>
    public void UseExit(bool selected) => enabled = selected;

    public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        // Looked up outside the lock: finding an installation may read the disk.
        var unavailable = FindUnavailableReason();
        var version = Version;
        lock (StateLock)
        {
            RefreshState();
            return ValueTask.FromResult(unavailable is not null && process is null
                ? new EngineStatus(Kind, EngineState.NotInstalled, version, Detail: unavailable, ChangedAt: changedAt)
                : new EngineStatus(Kind, state, version, Endpoint, detail, changedAt));
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (StateLock)
            {
                RefreshState();
                if (state == EngineState.Running)
                {
                    return;
                }
            }

            var established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (StateLock)
            {
                tunnelEstablished = established;
                lastProblem = null;
                ResetSession();
                SetStateUnsafe(EngineState.Starting, $"Waiting for {DisplayName} to connect through Paqet.");
            }

            var started = await LaunchAsync(cancellationToken).ConfigureAwait(false);

            started.EnableRaisingEvents = true;
            started.Exited += HandleExited;
            lock (StateLock)
            {
                process = started;
                expectedExit = false;
            }

            _ = PumpAsync(started.StandardOutput);
            _ = PumpAsync(started.StandardError);
            try
            {
                EngineProcessJob.Assign(started);
                await WaitUntilReadyAsync(started, established.Task, cancellationToken).ConfigureAwait(false);
                lock (StateLock)
                {
                    SetStateUnsafe(EngineState.Running, DescribeRunning());
                }
            }
            catch
            {
                lock (StateLock)
                {
                    expectedExit = true;
                }

                try
                {
                    await TerminateAsync(started).ConfigureAwait(false);
                }
                finally
                {
                    lock (StateLock)
                    {
                        process = null;
                    }
                }

                throw;
            }
        }
        catch (OperationCanceledException)
        {
            SetState(EngineState.Stopped, $"{DisplayName} startup was cancelled.");
            throw;
        }
        catch (Exception error)
        {
            AppendLog(error.Message);
            SetState(EngineState.Faulted, error.Message);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? owned;
            lock (StateLock)
            {
                RefreshState();
                owned = process;
                expectedExit = true;
                ResetSession();
                if (owned is null)
                {
                    SetStateUnsafe(EngineState.Stopped, $"{DisplayName} is stopped.");
                    return;
                }

                SetStateUnsafe(EngineState.Stopping, $"Stopping {DisplayName}.");
            }

            try
            {
                await TerminateAsync(owned).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                lock (StateLock)
                {
                    process = null;
                    SetStateUnsafe(EngineState.Faulted, error.Message);
                }

                throw;
            }

            lock (StateLock)
            {
                process = null;
                SetStateUnsafe(EngineState.Stopped, $"{DisplayName} is stopped.");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public IReadOnlyList<ExitEngineLogEntry> GetRecentLogs()
    {
        lock (StateLock)
        {
            return logs.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            disposed = true;
            lifecycleGate.Dispose();
        }
    }

    /// <summary>Applies one line of the engine's output.</summary>
    internal abstract void HandleOutput(string line);

    /// <summary>Why the engine cannot run on this computer, with directions to get it; null when it can.</summary>
    protected abstract string? FindUnavailableReason();

    /// <summary>Checks and prepares everything the process needs, returning a lock held until it has started.</summary>
    protected abstract ValueTask<IAsyncDisposable> PrepareAsync(CancellationToken cancellationToken);

    protected abstract ProcessStartInfo CreateStartInfo();

    /// <summary>Whether a reported tunnel is also usable, for example because a listener is up.</summary>
    protected virtual bool IsReady() => true;

    /// <summary>The detail shown while running; called under <see cref="StateLock"/>.</summary>
    protected virtual string DescribeRunning() => $"{DisplayName} is connected.";

    /// <summary>Forgets what the last process reported; called under <see cref="StateLock"/>.</summary>
    protected virtual void ResetSession()
    {
    }

    /// <summary>Asks the process to exit cleanly before it is killed.</summary>
    protected virtual Task RequestExitAsync(Process owned) => Task.CompletedTask;

    /// <summary>Releases what <see cref="PrepareAsync"/> created for the process once it has ended. May run twice.</summary>
    protected virtual void ReleaseProcessResources()
    {
    }

    protected virtual string Redact(string message) => message;

    protected void MarkEstablished()
    {
        TaskCompletionSource? established;
        lock (StateLock)
        {
            established = tunnelEstablished;
        }

        established?.TrySetResult();
    }

    /// <summary>
    /// Fails a start that is still waiting for the tunnel; once running, ends the process so
    /// recovery handles it like any unexpected exit.
    /// </summary>
    protected void ReportFatal(string message)
    {
        TaskCompletionSource? established;
        Process? running = null;
        lock (StateLock)
        {
            lastProblem = message;
            established = tunnelEstablished;
            if (state == EngineState.Running)
            {
                running = process;
            }
        }

        AppendLog(message);
        established?.TrySetException(new InvalidOperationException(message));
        try
        {
            running?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It has exited already.
        }
    }

    /// <summary>Remembers the latest problem, which a failed start or an unexpected exit reports.</summary>
    protected void SetProblem(string message)
    {
        lock (StateLock)
        {
            lastProblem = Redact(message);
        }
    }

    /// <summary>Refreshes the running detail after the session changed.</summary>
    protected void UpdateRunningDetail()
    {
        lock (StateLock)
        {
            if (state == EngineState.Running)
            {
                // Only the description changes; the engine has been running since it started.
                detail = DescribeRunning();
            }
        }
    }

    protected void AppendLog(string message)
    {
        message = Redact(message);
        lock (StateLock)
        {
            logs.Enqueue(new ExitEngineLogEntry(
                DateTimeOffset.Now,
                message.Length > MaximumLogLength ? message[..MaximumLogLength] : message));
            while (logs.Count > MaximumLogEntries)
            {
                logs.Dequeue();
            }
        }
    }

    private async Task<Process> LaunchAsync(CancellationToken cancellationToken)
    {
        Process? started = null;
        try
        {
            // The lock keeps the executable from being replaced until Windows has mapped it.
            await using (await PrepareAsync(cancellationToken).ConfigureAwait(false))
            {
                started = new Process { StartInfo = CreateStartInfo() };
                if (!started.Start())
                {
                    throw new InvalidOperationException($"Windows did not start {DisplayName}.");
                }
            }

            return started;
        }
        catch
        {
            started?.Dispose();
            ReleaseProcessResources();
            throw;
        }
    }

    private async Task WaitUntilReadyAsync(Process started, Task established, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(readinessTimeout);
        try
        {
            while (true)
            {
                if (started.HasExited)
                {
                    throw new InvalidOperationException(
                        $"{DisplayName} exited during startup with code {started.ExitCode}.{ProblemSuffix()}");
                }

                if (established.IsFaulted)
                {
                    await established.ConfigureAwait(false);
                }

                if (established.IsCompleted && IsReady())
                {
                    return;
                }

                await Task.Delay(ReadinessPollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{DisplayName} did not connect through Paqet within {readinessTimeout.TotalSeconds:0} seconds.{ProblemSuffix()}");
        }
    }

    private string ProblemSuffix()
    {
        lock (StateLock)
        {
            return lastProblem is null ? string.Empty : $" Last {DisplayName} error: {lastProblem}";
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                HandleOutput(line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Process teardown can close redirected streams while a read is pending.
        }
    }

    private void HandleExited(object? sender, EventArgs eventArgs)
    {
        bool unexpected;
        lock (StateLock)
        {
            if (sender is not Process exited || process != exited)
            {
                return;
            }

            unexpected = !expectedExit;
            MarkExitedUnsafe(exited);
        }

        ReleaseProcessResources();
        if (unexpected)
        {
            UnexpectedExit?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshState()
    {
        if (process is { HasExited: true } exited)
        {
            MarkExitedUnsafe(exited);
            ReleaseProcessResources();
        }
    }

    private void MarkExitedUnsafe(Process exited)
    {
        SetStateUnsafe(
            expectedExit ? EngineState.Stopped : EngineState.Faulted,
            expectedExit
                ? $"{DisplayName} is stopped."
                : $"{DisplayName} exited unexpectedly with code {exited.ExitCode}.{ProblemSuffix()}");
        ResetSession();
        process = null;
    }

    private void SetState(EngineState next, string? message)
    {
        lock (StateLock)
        {
            SetStateUnsafe(next, message);
        }
    }

    private void SetStateUnsafe(EngineState next, string? message)
    {
        state = next;
        detail = message;
        changedAt = DateTimeOffset.UtcNow;
    }

    private async Task TerminateAsync(Process owned)
    {
        try
        {
            if (!owned.HasExited)
            {
                await RequestExitAsync(owned).ConfigureAwait(false);
            }

            if (!owned.HasExited)
            {
                owned.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(ForceKillTimeout);
                await owned.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and termination.
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException($"The {DisplayName} process tree did not terminate in time.", error);
        }
        finally
        {
            owned.Dispose();
            ReleaseProcessResources();
        }
    }
}

public sealed record ExitEngineLogEntry(DateTimeOffset OccurredAt, string Message);
