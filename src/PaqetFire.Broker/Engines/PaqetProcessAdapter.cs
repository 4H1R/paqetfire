using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class PaqetProcessAdapter : IEngineAdapter, IAsyncDisposable
{
    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SocksGreetingTimeout = TimeSpan.FromMilliseconds(500);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Queue<PaqetProcessLogEntry> _logs = new();
    private readonly ValidatedOptions _options;
    private readonly LocalPortPlan? _ports;
    private IPEndPoint? _startedEndpoint;
    private Process? _process;
    private Task _standardOutputPump = Task.CompletedTask;
    private Task _standardErrorPump = Task.CompletedTask;
    private EngineState _state = EngineState.Stopped;
    private string? _detail;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;
    private bool _expectedExit;
    private bool _disposed;

    /// <param name="ports">The broker's port plan; each start listens on its Paqet port. Without it, the options' endpoint.</param>
    public PaqetProcessAdapter(PaqetProcessOptions options, LocalPortPlan? ports = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = ValidateOptions(options);
        _ports = ports;
    }

    /// <summary>The endpoint the next start listens on; a running process keeps the one it started with.</summary>
    private IPEndPoint PlannedEndpoint => _ports?.PaqetEndpoint ?? _options.SocksEndpoint;

    public EngineKind Kind => EngineKind.Paqet;

    /// <summary>Raised when the running process exits without being stopped, so recovery need not wait for a poll.</summary>
    public event EventHandler? UnexpectedExit;

    public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            RefreshExitedProcessState();
            return ValueTask.FromResult(new EngineStatus(
                Kind,
                _state,
                _options.Version,
                FormatEndpoint(_process is not null && _startedEndpoint is not null ? _startedEndpoint : PlannedEndpoint),
                _detail,
                _changedAt));
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_stateLock)
            {
                RefreshExitedProcessState();
                if (_state == EngineState.Running && _process is not null)
                {
                    return;
                }
            }

            FileStream? executableLock;
            var endpoint = PlannedEndpoint;
            try
            {
                executableLock = await ValidateFilesAtStartAsync(cancellationToken).ConfigureAwait(false);
                if (LoopbackListeners.IsListening(endpoint))
                {
                    executableLock?.Dispose();
                    throw new InvalidOperationException(
                        $"Paqet's local SOCKS5 port {endpoint.Port} was taken by another program just before " +
                        "Paqet started. Connect again to move it to a free port.");
                }
            }
            catch (OperationCanceledException)
            {
                SetState(EngineState.Stopped, "Paqet startup was cancelled before launch.");
                throw;
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or InvalidOperationException or
                    CryptographicException or InvalidDataException)
            {
                SetState(EngineState.Faulted, error.Message);
                throw;
            }

            SetState(EngineState.Starting, "Waiting for the local SOCKS5 listener.");

            var launchedAt = DateTimeOffset.UtcNow;
            var process = CreateProcess();
            try
            {
                bool started;
                using (executableLock)
                {
                    started = process.Start();
                }

                if (!started)
                {
                    throw new InvalidOperationException("Windows did not start the bundled Paqet process.");
                }

                lock (_stateLock)
                {
                    _process = process;
                    _startedEndpoint = endpoint;
                    _expectedExit = false;
                }

                EngineProcessJob.Assign(process);

                process.EnableRaisingEvents = true;
                process.Exited += HandleProcessExited;
                _standardOutputPump = PumpLogAsync(process.StandardOutput, isError: false);
                _standardErrorPump = PumpLogAsync(process.StandardError, isError: true);

                await WaitForSocksReadinessAsync(process, endpoint, launchedAt, cancellationToken).ConfigureAwait(false);
                SetState(EngineState.Running, "Paqet is accepting SOCKS5 connections.");
            }
            catch (Exception error)
            {
                // This kill is ours: it must not be reported as an unexpected exit.
                lock (_stateLock)
                {
                    _expectedExit = true;
                }

                await TerminateOwnedProcessAsync(process).ConfigureAwait(false);
                ClearOwnedProcess(process);

                if (error is OperationCanceledException)
                {
                    SetState(EngineState.Stopped, "Paqet startup was cancelled and cleaned up.");
                    throw;
                }

                SetState(EngineState.Faulted, error.Message);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Process? process;
            lock (_stateLock)
            {
                RefreshExitedProcessState();
                process = _process;
                if (process is null)
                {
                    // A rollback stops an engine whose start just failed; keep why it failed.
                    SetStateUnsafe(
                        EngineState.Stopped,
                        _state == EngineState.Faulted && _detail is { Length: > 0 } failure
                            ? $"Paqet is stopped. Its last start failed: {failure}"
                            : "Paqet is stopped.");
                    return;
                }

                _expectedExit = true;
                SetStateUnsafe(EngineState.Stopping, "Stopping the bundled Paqet process.");
            }

            try
            {
                await StopOwnedProcessAsync(process, cancellationToken).ConfigureAwait(false);
                ClearOwnedProcess(process);
                SetState(EngineState.Stopped, "Paqet is stopped.");
            }
            catch (OperationCanceledException)
            {
                await TerminateOwnedProcessAsync(process).ConfigureAwait(false);
                ClearOwnedProcess(process);
                SetState(EngineState.Stopped, "Paqet shutdown was cancelled; its process tree was terminated.");
                throw;
            }
            catch (Exception error)
            {
                await TerminateOwnedProcessAsync(process).ConfigureAwait(false);
                ClearOwnedProcess(process);
                SetState(EngineState.Faulted, error.Message);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public IReadOnlyList<PaqetProcessLogEntry> GetRecentLogs()
    {
        lock (_stateLock)
        {
            return [.. _logs];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? process;
            lock (_stateLock)
            {
                process = _process;
                _expectedExit = true;
            }

            if (process is not null)
            {
                await TerminateOwnedProcessAsync(process).ConfigureAwait(false);
                ClearOwnedProcess(process);
            }

            _disposed = true;
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private Process CreateProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(_options.ConfigurationPath);

        return new Process { StartInfo = startInfo };
    }

    private async Task PumpLogAsync(StreamReader reader, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > _options.MaximumLogLineLength)
                {
                    line = string.Concat(
                        line.AsSpan(0, _options.MaximumLogLineLength),
                        "…");
                }

                lock (_stateLock)
                {
                    _logs.Enqueue(new PaqetProcessLogEntry(DateTimeOffset.UtcNow, isError, line));
                    while (_logs.Count > _options.MaximumLogEntries)
                    {
                        _logs.Dequeue();
                    }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Process teardown can close redirected streams while a read is pending.
        }
        catch (IOException error)
        {
            lock (_stateLock)
            {
                _logs.Enqueue(new PaqetProcessLogEntry(
                    DateTimeOffset.UtcNow,
                    true,
                    $"Log stream ended unexpectedly: {error.Message}"));
                while (_logs.Count > _options.MaximumLogEntries)
                {
                    _logs.Dequeue();
                }
            }
        }
    }

    private async Task WaitForSocksReadinessAsync(
        Process process,
        IPEndPoint endpoint,
        DateTimeOffset launchedAt,
        CancellationToken cancellationToken)
    {
        using var readinessTimeout = new CancellationTokenSource(_options.ReadinessTimeout);
        using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            readinessTimeout.Token);

        string? probeFailure = null;
        while (true)
        {
            if (process.HasExited)
            {
                var exited = new System.Text.StringBuilder(
                    $"Paqet exited with code {process.ExitCode} before its SOCKS5 listener became ready. ");
                ReadinessReport.AppendOutput(exited, "Paqet", RecentOutput(launchedAt), SilentHint);
                throw new InvalidOperationException(exited.ToString());
            }

            // Only greet once the listener exists; until then the cheap table poll runs.
            var listening = LoopbackListeners.IsListening(endpoint);
            try
            {
                if (listening)
                {
                    if (await ProbeSocks5Async(endpoint, readinessCancellation.Token).ConfigureAwait(false))
                    {
                        return;
                    }

                    probeFailure = "it answered with something other than a SOCKS5 greeting";
                }
            }
            catch (OperationCanceledException) when (
                readinessTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw ReadinessTimeout(process, endpoint, launchedAt, probeFailure);
            }
            catch (OperationCanceledException error) when (!readinessCancellation.IsCancellationRequested)
            {
                // One greeting timed out; the overall readiness budget continues.
                probeFailure = ReadinessReport.DescribeConnectFailure(error);
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                // The listener may refuse connections or drop the greeting during startup.
                probeFailure = ReadinessReport.DescribeConnectFailure(error);
            }

            try
            {
                await Task.Delay(
                        listening ? _options.ProbeInterval : LoopbackListeners.PollInterval,
                        readinessCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                readinessTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw ReadinessTimeout(process, endpoint, launchedAt, probeFailure);
            }
        }
    }

    private const string SilentHint =
        "If Log level under Advanced transport is warn or error, set it to info and connect again to see how far Paqet gets.";

    private TimeoutException ReadinessTimeout(
        Process process,
        IPEndPoint endpoint,
        DateTimeOffset launchedAt,
        string? probeFailure) => new(
        ReadinessReport.Describe(
            "Paqet",
            process,
            endpoint,
            _options.ReadinessTimeout,
            probeFailure,
            RecentOutput(launchedAt),
            "Paqet opens it only after it has set up packet capture on the network adapter and its connections " +
            "to the server, so that setup had not finished. A stuck Npcap or network adapter driver can cause this.",
            SilentHint));

    /// <summary>What this start printed; the log also keeps earlier runs.</summary>
    private IReadOnlyList<string> RecentOutput(DateTimeOffset since)
    {
        lock (_stateLock)
        {
            return [.. _logs.Where(entry => entry.Timestamp >= since).Select(entry => entry.Message)];
        }
    }

    private static async ValueTask<bool> ProbeSocks5Async(
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        // A listener that accepts but never answers must not consume the whole readiness budget.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(SocksGreetingTimeout);
        using var client = new TcpClient(endpoint.AddressFamily);
        await client.ConnectAsync(endpoint.Address, endpoint.Port, attempt.Token).ConfigureAwait(false);

        await using var stream = client.GetStream();
        // Readiness only needs a SOCKS5 method selection, so offer both no-auth and
        // username/password instead of handling the engine credentials here.
        var greeting = new byte[] { 0x05, 0x02, 0x00, 0x02 };
        await stream.WriteAsync(greeting, attempt.Token).ConfigureAwait(false);

        var response = new byte[2];
        await stream.ReadExactlyAsync(response, attempt.Token).ConfigureAwait(false);
        return response[0] == 0x05 && response[1] is 0x00 or 0x02;
    }

    private async Task StopOwnedProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            await AwaitLogPumpsAsync().ConfigureAwait(false);
            return;
        }

        if (!process.CloseMainWindow())
        {
            await TerminateOwnedProcessAsync(process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        using var gracefulTimeout = new CancellationTokenSource(_options.ShutdownTimeout);
        using var gracefulCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            gracefulTimeout.Token);

        try
        {
            await process.WaitForExitAsync(gracefulCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            gracefulTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await TerminateOwnedProcessAsync(process).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await AwaitLogPumpsAsync().ConfigureAwait(false);
    }

    private async Task TerminateOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var timeout = new CancellationTokenSource(ForceKillTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await AwaitLogPumpsAsync().WaitAsync(ForceKillTimeout).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The owned process exited between the state check and termination.
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException("The owned Paqet process tree did not terminate in time.", error);
        }
    }

    private async Task AwaitLogPumpsAsync()
    {
        await Task.WhenAll(_standardOutputPump, _standardErrorPump).ConfigureAwait(false);
    }

    private void HandleProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!ReferenceEquals(_process, process) || _expectedExit)
            {
                return;
            }

            SetStateUnsafe(
                EngineState.Faulted,
                $"Paqet exited unexpectedly with code {TryGetExitCode(process)}.");
        }

        UnexpectedExit?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshExitedProcessState()
    {
        if (_process is null || !_process.HasExited || _expectedExit)
        {
            return;
        }

        SetStateUnsafe(
            EngineState.Faulted,
            $"Paqet exited unexpectedly with code {TryGetExitCode(_process)}.");
    }

    private void ClearOwnedProcess(Process process)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _expectedExit = false;
            }
        }

        process.Exited -= HandleProcessExited;
        process.Dispose();
    }

    /// <returns>A handle on the verified executable to hold until the process has started.</returns>
    private async ValueTask<FileStream?> ValidateFilesAtStartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath))
        {
            throw new FileNotFoundException(
                "The validated bundled Paqet executable is missing.",
                _options.ExecutablePath);
        }

        if (!File.Exists(_options.ConfigurationPath))
        {
            throw new FileNotFoundException(
                "The validated Paqet configuration is missing.",
                _options.ConfigurationPath);
        }

        EnsureNotReparsePoint(_options.ExecutablePath, "Paqet executable");
        EnsureNotReparsePoint(_options.ConfigurationPath, "Paqet configuration");

        return _options.ExpectedExecutableSha256 is { } expectedDigest
            ? await VerifiedExecutable.OpenAsync(
                    _options.ExecutablePath,
                    expectedDigest,
                    () => new InvalidDataException("The bundled Paqet executable failed its pre-launch integrity check."),
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    private static void EnsureNotReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"The {label} cannot be a reparse point.");
        }
    }

    private void SetState(EngineState state, string? detail)
    {
        lock (_stateLock)
        {
            SetStateUnsafe(state, detail);
        }
    }

    private void SetStateUnsafe(EngineState state, string? detail)
    {
        _state = state;
        _detail = detail;
        _changedAt = DateTimeOffset.UtcNow;
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static ValidatedOptions ValidateOptions(PaqetProcessOptions options)
    {
        var executablePath = ValidateContainedPath(
            options.ExecutablePath,
            options.TrustedExecutableRoot,
            nameof(options.ExecutablePath));
        var configurationPath = ValidateContainedPath(
            options.ConfigurationPath,
            options.TrustedConfigurationRoot,
            nameof(options.ConfigurationPath));

        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Paqet entry point must be a Windows executable.", nameof(options));
        }

        var configurationExtension = Path.GetExtension(configurationPath);
        if (!string.Equals(configurationExtension, ".yaml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configurationExtension, ".yml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Paqet configuration must be a YAML file.", nameof(options));
        }

        if (!IPAddress.IsLoopback(options.SocksEndpoint.Address) || options.SocksEndpoint.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("The Paqet SOCKS endpoint must be a valid loopback endpoint.", nameof(options));
        }

        ValidateFinitePositive(options.ReadinessTimeout, nameof(options.ReadinessTimeout));
        ValidateFinitePositive(options.ShutdownTimeout, nameof(options.ShutdownTimeout));
        ValidateFinitePositive(options.ProbeInterval, nameof(options.ProbeInterval));

        if (options.MaximumLogEntries is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumLogEntries must be between 1 and 10,000.");
        }

        if (options.MaximumLogLineLength is < 64 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumLogLineLength must be between 64 and 65,536.");
        }

        string? expectedExecutableSha256 = null;
        if (!string.IsNullOrWhiteSpace(options.ExpectedExecutableSha256))
        {
            expectedExecutableSha256 = options.ExpectedExecutableSha256.Trim();
            if (expectedExecutableSha256.Length != 64 ||
                !expectedExecutableSha256.All(Uri.IsHexDigit))
            {
                throw new ArgumentException(
                    "The expected Paqet executable SHA-256 is invalid.",
                    nameof(options));
            }
        }

        return new ValidatedOptions(
            executablePath,
            configurationPath,
            new IPEndPoint(options.SocksEndpoint.Address, options.SocksEndpoint.Port),
            options.Version,
            expectedExecutableSha256,
            options.ReadinessTimeout,
            options.ShutdownTimeout,
            options.ProbeInterval,
            options.MaximumLogEntries,
            options.MaximumLogLineLength);
    }

    private static string ValidateContainedPath(string path, string root, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("Engine paths and trusted roots must be fully qualified.", parameterName);
        }

        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var resolvedPath = Path.GetFullPath(path);
        if (!resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The engine path escapes its trusted root.", parameterName);
        }

        return resolvedPath;
    }

    private static void ValidateFinitePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan || value > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Timeouts must be positive, finite, and no longer than five minutes.");
        }
    }

    private static string FormatEndpoint(IPEndPoint endpoint) =>
        endpoint.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{endpoint.Address}]:{endpoint.Port}"
            : $"{endpoint.Address}:{endpoint.Port}";

    private sealed record ValidatedOptions(
        string ExecutablePath,
        string ConfigurationPath,
        IPEndPoint SocksEndpoint,
        string? Version,
        string? ExpectedExecutableSha256,
        TimeSpan ReadinessTimeout,
        TimeSpan ShutdownTimeout,
        TimeSpan ProbeInterval,
        int MaximumLogEntries,
        int MaximumLogLineLength);
}
