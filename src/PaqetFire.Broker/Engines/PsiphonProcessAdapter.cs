using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Runs psiphon-tunnel-core as the exit after Paqet. Psiphon dials its servers only through
/// Paqet's loopback SOCKS5 listener and serves Xray's exit outbound on its own loopback
/// listener. It counts as running once it reports an established tunnel.
/// </summary>
public sealed partial class PsiphonProcessAdapter : IExitEngineAdapter, IAsyncDisposable
{
    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(5);
    private readonly PsiphonProcessOptions options;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Queue<PsiphonProcessLogEntry> logs = new();
    private Process? process;
    private TaskCompletionSource? tunnelEstablished;
    private EngineState state = EngineState.Stopped;
    private string? detail = "Psiphon is stopped.";
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private PsiphonSession session = PsiphonSession.None;
    private string? lastProblem;
    private bool expectedExit;
    private volatile bool enabled;
    private bool disposed;

    public PsiphonProcessAdapter(PsiphonProcessOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public EngineKind Kind => EngineKind.Psiphon;

    public bool IsEnabled => enabled;

    /// <summary>Raised when the running process exits without being stopped, so recovery need not wait for a poll.</summary>
    public event EventHandler? UnexpectedExit;

    /// <summary>What the running Psiphon process last reported about its tunnel.</summary>
    public PsiphonSession Session
    {
        get
        {
            lock (stateLock)
            {
                return session;
            }
        }
    }

    /// <summary>Records whether the configuration written for the next start selects this exit.</summary>
    public void UseExit(bool selected) => enabled = selected;

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
                    Detail: "The bundled Psiphon executable was not found.",
                    ChangedAt: changedAt));
            }

            return ValueTask.FromResult(new EngineStatus(
                Kind,
                state,
                options.Version,
                $"{options.SocksEndpoint.Address}:{options.SocksEndpoint.Port}",
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

            if (!File.Exists(options.ConfigurationPath))
            {
                throw new FileNotFoundException("The generated Psiphon configuration is missing.", options.ConfigurationPath);
            }

            await using var executableLock = await VerifiedExecutable.OpenAsync(
                    options.ExecutablePath,
                    options.ExpectedExecutableSha256,
                    () => new CryptographicException("The bundled Psiphon executable failed its SHA-256 integrity check."),
                    cancellationToken)
                .ConfigureAwait(false);
            if (LoopbackListeners.IsListening(options.SocksEndpoint))
            {
                throw new InvalidOperationException(
                    $"Psiphon's local SOCKS5 endpoint {options.SocksEndpoint} is already in use.");
            }

            PrepareDataDirectory();
            var established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (stateLock)
            {
                tunnelEstablished = established;
                session = PsiphonSession.None;
                lastProblem = null;
            }

            SetState(EngineState.Starting, "Waiting for Psiphon to establish a tunnel through Paqet.");
            var started = CreateProcess();
            if (!started.Start())
            {
                throw new InvalidOperationException("Windows did not start the bundled Psiphon process.");
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
                await WaitUntilReadyAsync(started, established.Task, cancellationToken).ConfigureAwait(false);
                SetState(EngineState.Running, DescribeTunnel());
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
            SetState(EngineState.Stopped, "Psiphon startup was cancelled.");
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
            lock (stateLock)
            {
                RefreshState();
                owned = process;
                expectedExit = true;
                session = PsiphonSession.None;
                if (owned is null)
                {
                    SetStateUnsafe(EngineState.Stopped, "Psiphon is stopped.");
                    return;
                }

                SetStateUnsafe(EngineState.Stopping, "Stopping Psiphon.");
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
                SetStateUnsafe(EngineState.Stopped, "Psiphon is stopped.");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public IReadOnlyList<PsiphonProcessLogEntry> GetRecentLogs()
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

    /// <summary>
    /// Applies one console-client notice, a JSON object such as
    /// <c>{"noticeType":"Tunnels","data":{"count":1},"timestamp":"..."}</c>.
    /// Anything else is kept as a plain log line.
    /// </summary>
    internal void HandleOutput(string line)
    {
        if (!TryParseNotice(line, out var type, out var data))
        {
            AppendLog(line);
            return;
        }

        TaskCompletionSource? established = null;
        lock (stateLock)
        {
            switch (type)
            {
                case "Tunnels" when TryGetInt(data, "count", out var count):
                    session = session with { TunnelActive = count > 0 };
                    if (count > 0)
                    {
                        established = tunnelEstablished;
                    }

                    if (state == EngineState.Running)
                    {
                        // Only the description changes; the engine has been running since it started.
                        detail = DescribeTunnelUnsafe();
                    }

                    break;
                case "ConnectedServerRegion" when TryGetString(data, "serverRegion", out var region):
                    session = session with { ConnectedRegion = region };
                    break;
                case "AvailableEgressRegions" when data.TryGetProperty("regions", out var regions) &&
                                                   regions.ValueKind == JsonValueKind.Array:
                    session = session with
                    {
                        AvailableRegions = regions.EnumerateArray()
                            .Where(region => region.ValueKind == JsonValueKind.String)
                            .Select(region => region.GetString()!)
                            .Where(region => region.Length == 2)
                            .Order(StringComparer.Ordinal)
                            .ToArray(),
                    };
                    break;
                case "UpstreamProxyError" or "Error" or "Alert" when TryGetString(data, "message", out var message):
                    lastProblem = RedactProxyUrls(message);
                    break;
                case "SocksProxyPortInUse":
                    lastProblem = $"Its local SOCKS5 port {options.SocksEndpoint.Port} is in use.";
                    break;
            }
        }

        established?.TrySetResult();
        if (type is not ("ClientRegion" or "Homepage" or "Info"))
        {
            AppendLog($"{type} {data.GetRawText()}");
        }
    }

    internal static bool TryParseNotice(string line, out string type, out JsonElement data)
    {
        type = string.Empty;
        data = default;
        if (!line.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetString(document.RootElement, "noticeType", out type))
            {
                return false;
            }

            data = document.RootElement.TryGetProperty("data", out var payload) && payload.ValueKind == JsonValueKind.Object
                ? payload.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Removes credentials from any proxy URL, such as the upstream Paqet hop in an error.</summary>
    internal static string RedactProxyUrls(string text) => ProxyCredentials().Replace(text, "$1***@");

    [GeneratedRegex(@"(socks5?://|socks4a://|https?://)[^@\s/""]+@", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyCredentials();

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private void PrepareDataDirectory()
    {
        if (options.DataDirectoryPolicy is { } policy)
        {
            new ProtectedDirectory(options.DataDirectory, policy).Ensure();
        }
        else
        {
            Directory.CreateDirectory(options.DataDirectory);
        }
    }

    private Process CreateProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(options.ConfigurationPath);
        startInfo.ArgumentList.Add("-dataRootDirectory");
        startInfo.ArgumentList.Add(options.DataDirectory);
        return new Process { StartInfo = startInfo };
    }

    private async Task WaitUntilReadyAsync(Process started, Task established, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ReadinessTimeout);
        try
        {
            while (true)
            {
                if (started.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Psiphon exited during startup with code {started.ExitCode}.{ProblemSuffix()}");
                }

                if (established.IsCompleted && LoopbackListeners.IsListening(options.SocksEndpoint))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Psiphon did not establish a tunnel through Paqet within {options.ReadinessTimeout.TotalSeconds:0} seconds.{ProblemSuffix()}");
        }
    }

    private string ProblemSuffix()
    {
        lock (stateLock)
        {
            return lastProblem is null ? string.Empty : $" Last Psiphon error: {lastProblem}";
        }
    }

    private string DescribeTunnel()
    {
        lock (stateLock)
        {
            return DescribeTunnelUnsafe();
        }
    }

    private string DescribeTunnelUnsafe() => session switch
    {
        { TunnelActive: false } => "Psiphon lost its tunnel and is reconnecting through Paqet.",
        { ConnectedRegion: { Length: > 0 } region } => $"Psiphon is connected through a server in {region}.",
        _ => "Psiphon is connected.",
    };

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

    private void AppendLog(string message)
    {
        message = RedactProxyUrls(message);
        lock (stateLock)
        {
            logs.Enqueue(new PsiphonProcessLogEntry(
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
                expectedExit ? "Psiphon is stopped." : $"Psiphon exited unexpectedly with code {exited.ExitCode}.");
            session = PsiphonSession.None;
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
                expectedExit ? "Psiphon is stopped." : $"Psiphon exited unexpectedly with code {exited.ExitCode}.");
            session = PsiphonSession.None;
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
            throw new TimeoutException("The Psiphon process tree did not terminate in time.", error);
        }
        finally
        {
            process.Dispose();
        }
    }
}

public sealed record PsiphonProcessLogEntry(DateTimeOffset OccurredAt, string Message);

/// <param name="ConnectedRegion">The country of the Psiphon server in use, as Psiphon reports it.</param>
/// <param name="AvailableRegions">The exit countries Psiphon can currently serve.</param>
public sealed record PsiphonSession(bool TunnelActive, string? ConnectedRegion, IReadOnlyList<string> AvailableRegions)
{
    public static PsiphonSession None { get; } = new(false, null, []);
}
