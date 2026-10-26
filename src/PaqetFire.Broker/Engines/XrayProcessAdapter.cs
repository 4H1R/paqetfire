using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class XrayProcessAdapter : IEngineAdapter, IAsyncDisposable
{
    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StoppingTimeout = TimeSpan.FromSeconds(2);
    internal const string AssetLocationVariable = "XRAY_LOCATION_ASSET";
    private readonly XrayProcessOptions options;
    private readonly RoutingDataFileVerifier routingDataVerifier;
    private RoutingDataSet routingData = RoutingDataSet.Bundled;
    private byte[]? lastValidatedConfiguration;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Queue<XrayProcessLogEntry> logs = new();
    private Process? process;
    private EngineState state = EngineState.Stopped;
    private string? detail = "Xray is stopped.";
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private bool expectedExit;
    private bool disposed;

    public XrayProcessAdapter(XrayProcessOptions options, RoutingDataFileVerifier? routingDataVerifier = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.routingDataVerifier = routingDataVerifier ?? new RoutingDataFileVerifier();
    }

    public EngineKind Kind => EngineKind.Xray;

    /// <summary>Raised when the running process exits without being stopped, so recovery need not wait for a poll.</summary>
    public event EventHandler? UnexpectedExit;

    /// <summary>
    /// Runs while a running process is about to be stopped, before it is terminated, so its
    /// in-memory traffic counters can still be read. Failures and timeouts are ignored.
    /// </summary>
    public Func<CancellationToken, ValueTask>? Stopping { get; set; }

    /// <summary>The GeoIP/GeoSite set the next start loads; a running process keeps the one it started with.</summary>
    public RoutingDataSet RoutingData
    {
        get
        {
            lock (stateLock)
            {
                return routingData;
            }
        }
    }

    /// <summary>Selects the data the next start loads, matching the configuration written for it.</summary>
    public void UseRoutingData(RoutingDataSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (stateLock)
        {
            routingData = set;
        }
    }

    public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (stateLock)
        {
            RefreshState();
            if (!File.Exists(options.ExecutablePath))
            {
                return ValueTask.FromResult(new EngineStatus(
                    Kind,
                    EngineState.NotInstalled,
                    options.Version,
                    Detail: "The bundled Xray executable was not found.",
                    ChangedAt: changedAt));
            }

            return ValueTask.FromResult(new EngineStatus(
                Kind,
                state,
                options.Version,
                $"{options.InboundEndpoint.Address}:{options.InboundEndpoint.Port}",
                detail,
                changedAt));
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateLock)
            {
                RefreshState();
                if (state == EngineState.Running)
                {
                    return;
                }
            }

            await using var executableLock = await ValidatePayloadAsync(cancellationToken).ConfigureAwait(false);
            if (LoopbackListeners.IsListening(options.InboundEndpoint))
            {
                throw new InvalidOperationException(
                    $"Xray's local SOCKS5 endpoint {options.InboundEndpoint} is already in use.");
            }

            var data = RoutingData;
            // Held until Xray is ready, so the databases it loads are the bytes just verified.
            await using var dataLease = await OpenRoutingDataAsync(data, cancellationToken).ConfigureAwait(false);
            try
            {
                await ValidateConfigurationAsync(data, TimeoutFor(data, options.ValidationTimeout), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception) when (data.IsUpdated)
            {
                throw new RoutingDataUnavailableException(
                    "Xray's configuration check did not finish in time with the downloaded routing data.", exception);
            }

            SetState(EngineState.Starting, "Waiting for Xray's local policy endpoint.");
            var started = CreateProcess(options.ConfigurationPath, testOnly: false, data.AssetDirectory);
            if (!started.Start())
            {
                throw new InvalidOperationException("Windows did not start the bundled Xray process.");
            }

            await executableLock.DisposeAsync().ConfigureAwait(false);
            started.EnableRaisingEvents = true;
            started.Exited += HandleExited;
            lock (stateLock)
            {
                process = started;
                expectedExit = false;
            }

            _ = PumpAsync(started.StandardOutput);
            _ = PumpAsync(started.StandardError);
            try
            {
                EngineProcessJob.Assign(started);
                try
                {
                    await WaitUntilReadyAsync(started, TimeoutFor(data, options.ReadinessTimeout), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception) when (data.IsUpdated)
                {
                    throw new RoutingDataUnavailableException(
                        "Xray did not open its local policy endpoint in time with the downloaded routing data.", exception);
                }

                SetState(EngineState.Running, "Xray is applying the regional routing policy.");
            }
            catch
            {
                lock (stateLock)
                {
                    expectedExit = true;
                }

                try
                {
                    await TerminateAsync(started).ConfigureAwait(false);
                }
                finally
                {
                    lock (stateLock)
                    {
                        process = null;
                    }
                }

                throw;
            }
        }
        catch (OperationCanceledException)
        {
            SetState(EngineState.Stopped, "Xray startup was cancelled.");
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
            await NotifyStoppingAsync().ConfigureAwait(false);
            Process? owned;
            lock (stateLock)
            {
                RefreshState();
                owned = process;
                expectedExit = true;
                if (owned is null)
                {
                    SetStateUnsafe(EngineState.Stopped, "Xray is stopped.");
                    return;
                }

                SetStateUnsafe(EngineState.Stopping, "Stopping Xray.");
            }

            try
            {
                await TerminateAsync(owned).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                lock (stateLock)
                {
                    process = null;
                    SetStateUnsafe(EngineState.Faulted, error.Message);
                }

                throw;
            }

            lock (stateLock)
            {
                process = null;
                SetStateUnsafe(EngineState.Stopped, "Xray is stopped.");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask NotifyStoppingAsync()
    {
        bool running;
        lock (stateLock)
        {
            RefreshState();
            running = process is not null && state == EngineState.Running;
        }

        if (!running || Stopping is not { } stopping)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(StoppingTimeout);
            await stopping(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Stopping must never depend on an observer.
        }
    }

    public IReadOnlyList<XrayProcessLogEntry> GetRecentLogs()
    {
        lock (stateLock)
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

    /// <returns>A handle on the verified executable to hold until the process has started.</returns>
    private async Task<FileStream> ValidatePayloadAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[]
                 {
                     options.ExecutablePath,
                     options.ConfigurationPath,
                     options.GeoIpPath,
                     options.GeoSitePath,
                 })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("A required Xray payload file is missing.", path);
            }
        }

        return await VerifiedExecutable.OpenAsync(
                options.ExecutablePath,
                options.ExpectedExecutableSha256,
                () => new CryptographicException("The bundled Xray executable failed its SHA-256 integrity check."),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>xray run -test</c> on a probe configuration with <paramref name="assetDirectory"/>
    /// as the GeoIP/GeoSite location, using the pinned executable.
    /// </summary>
    public async Task ValidateRoutingDataAsync(
        string assetDirectory,
        string probeConfigurationPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await using var executableLock = await VerifiedExecutable.OpenAsync(
                options.ExecutablePath,
                options.ExpectedExecutableSha256,
                () => new CryptographicException("The bundled Xray executable failed its SHA-256 integrity check."),
                cancellationToken)
            .ConfigureAwait(false);
        using var validation = CreateProcess(probeConfigurationPath, testOnly: true, assetDirectory);
        await RunValidationAsync(
                validation,
                options.UpdatedDataTimeout,
                cancellationToken,
                "Xray rejected the downloaded routing data")
            .ConfigureAwait(false);
    }

    // A full downloaded data set takes longer to load than the bundled one.
    private TimeSpan TimeoutFor(RoutingDataSet data, TimeSpan bundled) => TimeoutFor(data, bundled, options.UpdatedDataTimeout);

    internal static TimeSpan TimeoutFor(RoutingDataSet data, TimeSpan bundled, TimeSpan updated) =>
        data.IsUpdated && updated > bundled ? updated : bundled;

    private async Task<RoutingDataLease> OpenRoutingDataAsync(RoutingDataSet data, CancellationToken cancellationToken)
    {
        try
        {
            return await routingDataVerifier.OpenAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The caller retries with the bundled data; the inner exception says whether the files changed.
            throw new RoutingDataUnavailableException("The downloaded routing data could not be verified for this Xray start.", exception);
        }
    }

    private async Task ValidateConfigurationAsync(RoutingDataSet data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // `xray run -test` loads the GeoIP/GeoSite databases and costs a few hundred
        // milliseconds. An identical configuration already passed it with the same data,
        // and the executable and databases are verified before every start.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(await File.ReadAllBytesAsync(options.ConfigurationPath, cancellationToken)
            .ConfigureAwait(false));
        hash.AppendData(Encoding.UTF8.GetBytes("\n" + data.Fingerprint));
        var digest = hash.GetHashAndReset();
        lock (stateLock)
        {
            if (lastValidatedConfiguration is not null &&
                CryptographicOperations.FixedTimeEquals(lastValidatedConfiguration, digest))
            {
                return;
            }
        }

        using var validation = CreateProcess(options.ConfigurationPath, testOnly: true, data.AssetDirectory);
        await RunValidationAsync(validation, timeout, cancellationToken).ConfigureAwait(false);
        lock (stateLock)
        {
            lastValidatedConfiguration = digest;
        }
    }

    internal static async Task RunValidationAsync(
        Process validation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string rejection = "Xray rejected the generated regional policy")
    {
        if (!validation.Start())
        {
            throw new InvalidOperationException("Windows could not start Xray's configuration check.");
        }

        try
        {
            // A check left behind by a crashed broker must not outlive it.
            EngineProcessJob.Assign(validation);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The deadline below still terminates it.
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var outputTask = validation.StandardOutput.ReadToEndAsync(deadline.Token);
        var errorTask = validation.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await validation.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false) + " " + await errorTask.ConfigureAwait(false)).Trim();
            if (validation.ExitCode != 0)
            {
                throw new InvalidDataException($"{rejection}: {output}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Xray's configuration check exceeded its deadline.");
        }
        finally
        {
            try
            {
                if (!validation.HasExited)
                {
                    validation.Kill(entireProcessTree: true);
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await validation.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception cleanup) when (
                cleanup is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or NotSupportedException)
            {
                // Cleanup must not replace the error that ended the check.
            }

            await deadline.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Both stream reads are observed after cancellation or timeout.
            }
        }
    }

    private Process CreateProcess(string configurationPath, bool testOnly, string? assetDirectory)
    {
        var arguments = $"run {(testOnly ? "-test " : string.Empty)}-config \"{configurationPath}\"";
        var executableDirectory = Path.GetDirectoryName(options.ExecutablePath)!;
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            Arguments = arguments,
            WorkingDirectory = executableDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        ApplyEnvironment(startInfo.Environment, assetDirectory ?? executableDirectory);
        return new Process { StartInfo = startInfo };
    }

    /// <summary>
    /// Removes every inherited Xray setting (for example a machine-wide xray.location.asset or
    /// XRAY_LOCATION_CONFDIR, which could redirect the databases or merge other configuration)
    /// and sets the asset directory explicitly.
    /// </summary>
    internal static void ApplyEnvironment(IDictionary<string, string?> environment, string assetDirectory)
    {
        foreach (var key in environment.Keys
                     .Where(key => key.StartsWith("xray.", StringComparison.OrdinalIgnoreCase) ||
                                   key.StartsWith("XRAY_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            environment.Remove(key);
        }

        environment[AssetLocationVariable] = assetDirectory;
    }

    private async Task WaitUntilReadyAsync(Process started, TimeSpan readinessTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(readinessTimeout);
        try
        {
            while (true)
            {
                if (started.HasExited)
                {
                    throw new InvalidOperationException($"Xray exited during startup with code {started.ExitCode}.");
                }

                // The listener table shows the socket as soon as Xray binds it; one connect
                // then confirms that it accepts.
                if (LoopbackListeners.IsListening(options.InboundEndpoint) &&
                    await CanConnectAsync(timeout.Token).ConfigureAwait(false))
                {
                    return;
                }

                await Task.Delay(LoopbackListeners.PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Xray did not open its local policy endpoint in time.");
        }
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(
                options.InboundEndpoint.Address,
                options.InboundEndpoint.Port,
                timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                AppendLog(line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Process teardown can close redirected streams while a read is pending.
        }
    }

    private void AppendLog(string message)
    {
        lock (stateLock)
        {
            logs.Enqueue(new XrayProcessLogEntry(
                DateTimeOffset.Now,
                message.Length > 2048 ? message[..2048] : message));
            while (logs.Count > 80)
            {
                logs.Dequeue();
            }
        }
    }

    private void HandleExited(object? sender, EventArgs eventArgs)
    {
        bool unexpected;
        lock (stateLock)
        {
            if (sender is not Process exited || process != exited)
            {
                return;
            }

            unexpected = !expectedExit;
            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "Xray is stopped." : $"Xray exited unexpectedly with code {exited.ExitCode}.");
            process = null;
        }

        if (unexpected)
        {
            UnexpectedExit?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshState()
    {
        if (process is { HasExited: true } exited)
        {
            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "Xray is stopped." : $"Xray exited unexpectedly with code {exited.ExitCode}.");
            process = null;
        }
    }

    private void SetState(EngineState next, string? message)
    {
        lock (stateLock)
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

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Bounded like Paqet's stop: callers hold lifecycle gates, so a process that
                // ignores termination must surface as a fault rather than a hung broker.
                using var timeout = new CancellationTokenSource(ForceKillTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and termination.
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException("The Xray process tree did not terminate in time.", error);
        }
        finally
        {
            process.Dispose();
        }
    }
}

public sealed record XrayProcessLogEntry(DateTimeOffset OccurredAt, string Message);
