using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class ProxiFyreProcessAdapter : IEngineAdapter, IAsyncDisposable
{
    public const string WindowsPacketFilterServiceName = "NDISRD";

    private readonly ProxiFyreProcessOptions options;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Queue<string> logs = new();
    private Process? process;
    private EngineState state = EngineState.Stopped;
    private string? detail = "ProxiFyre is stopped.";
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private bool expectedExit;
    private bool disposed;

    public ProxiFyreProcessAdapter(ProxiFyreProcessOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public EngineKind Kind => EngineKind.ProxiFyre;

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
                    Detail: "The bundled ProxiFyre executable was not found.",
                    ChangedAt: changedAt));
            }

            return ValueTask.FromResult(new EngineStatus(
                Kind,
                state,
                options.Version,
                Detail: detail,
                ChangedAt: changedAt));
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

            HardenInstallationDirectory();
            await ValidatePayloadAsync(cancellationToken).ConfigureAwait(false);
            EnsureWindowsPacketFilterInstalled();
            SetState(EngineState.Starting, "Waiting for ProxiFyre to initialize application routing.");

            var started = CreateProcess();
            if (!started.Start())
            {
                throw new InvalidOperationException("Windows did not start the bundled ProxiFyre process.");
            }

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
                await WaitUntilReadyAsync(started, cancellationToken).ConfigureAwait(false);
                SetState(EngineState.Running, "ProxiFyre is routing selected application traffic.");
            }
            catch
            {
                await TerminateAsync(started).ConfigureAwait(false);
                lock (stateLock)
                {
                    process = null;
                }

                throw;
            }
        }
        catch (OperationCanceledException)
        {
            SetState(EngineState.Stopped, "ProxiFyre startup was cancelled.");
            throw;
        }
        catch (Exception error)
        {
            AppendLog(error.Message);
            SetState(EngineState.Faulted, BuildFailureDetail(error));
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
                if (owned is null)
                {
                    SetStateUnsafe(EngineState.Stopped, "ProxiFyre is stopped.");
                    return;
                }

                SetStateUnsafe(EngineState.Stopping, "Stopping ProxiFyre.");
            }

            await TerminateAsync(owned).ConfigureAwait(false);
            lock (stateLock)
            {
                process = null;
                SetStateUnsafe(EngineState.Stopped, "ProxiFyre is stopped.");
            }
        }
        finally
        {
            lifecycleGate.Release();
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

    private Process CreateProcess() => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            Arguments = "run",
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        },
    };

    internal async Task ValidatePayloadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException("The bundled ProxiFyre executable is missing.", options.ExecutablePath);
        }

        if (!File.Exists(options.ConfigurationPath))
        {
            throw new FileNotFoundException("The generated ProxiFyre configuration is missing.", options.ConfigurationPath);
        }

        if (string.IsNullOrWhiteSpace(options.ExpectedExecutableSha256))
        {
            return;
        }

        await using var stream = File.OpenRead(options.ExecutablePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(options.ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The bundled ProxiFyre executable failed its SHA-256 integrity check.");
        }
    }

    private void HardenInstallationDirectory()
    {
        var installationDirectory = Path.GetDirectoryName(options.ExecutablePath)
            ?? throw new InvalidOperationException("The ProxiFyre executable has no installation directory.");
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateDirectoryRule(
            WellKnownSidType.LocalSystemSid,
            FileSystemRights.FullControl,
            inheritance));
        security.AddAccessRule(CreateDirectoryRule(
            WellKnownSidType.BuiltinAdministratorsSid,
            FileSystemRights.FullControl,
            inheritance));
        security.AddAccessRule(CreateDirectoryRule(
            WellKnownSidType.BuiltinUsersSid,
            FileSystemRights.ReadAndExecute,
            inheritance));
        new DirectoryInfo(installationDirectory).SetAccessControl(security);
    }

    private static FileSystemAccessRule CreateDirectoryRule(
        WellKnownSidType sidType,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        new(
            new SecurityIdentifier(sidType, domainSid: null),
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static void EnsureWindowsPacketFilterInstalled()
    {
        try
        {
            using var driver = new ServiceController(WindowsPacketFilterServiceName);
            driver.Refresh();
            _ = driver.Status;
        }
        catch (InvalidOperationException exception) when (FindWin32Exception(exception)?.NativeErrorCode == 1060)
        {
            throw new InvalidOperationException(
                "Windows Packet Filter is not installed. Install the bundled NDISRD driver before starting routing.",
                exception);
        }
    }

    private async Task WaitUntilReadyAsync(Process started, CancellationToken cancellationToken)
    {
        var stableFor = TimeSpan.Zero;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ReadinessTimeout);
        while (!timeout.Token.IsCancellationRequested)
        {
            if (started.HasExited)
            {
                throw new InvalidOperationException($"ProxiFyre exited during startup with code {started.ExitCode}.");
            }

            if (stableFor >= TimeSpan.FromSeconds(2))
            {
                return;
            }

            await Task.Delay(options.ProbeInterval, timeout.Token).ConfigureAwait(false);
            stableFor += options.ProbeInterval;
        }

        throw new System.TimeoutException("ProxiFyre did not remain active long enough to initialize routing.");
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            AppendLog(line);
        }
    }

    private void AppendLog(string message)
    {
        lock (stateLock)
        {
            logs.Enqueue(message.Length > options.MaximumLogLineLength
                ? message[..options.MaximumLogLineLength]
                : message);
            while (logs.Count > options.MaximumLogEntries)
            {
                logs.Dequeue();
            }
        }
    }

    private string BuildFailureDetail(Exception error)
    {
        lock (stateLock)
        {
            var lastLine = logs.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return lastLine is null ? error.Message : $"{error.Message} Last output: {lastLine}";
        }
    }

    private void HandleExited(object? sender, EventArgs eventArgs)
    {
        lock (stateLock)
        {
            if (sender is not Process exited || process != exited)
            {
                return;
            }

            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "ProxiFyre is stopped." : $"ProxiFyre exited unexpectedly with code {exited.ExitCode}.");
            process = null;
        }
    }

    private void RefreshState()
    {
        if (process is { HasExited: true } exited)
        {
            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "ProxiFyre is stopped." : $"ProxiFyre exited unexpectedly with code {exited.ExitCode}.");
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

    private static Win32Exception? FindWin32Exception(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is Win32Exception win32Exception)
            {
                return win32Exception;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        process.Dispose();
    }
}
