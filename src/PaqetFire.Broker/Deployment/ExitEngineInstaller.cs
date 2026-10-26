using System.Security.Cryptography;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Deployment;

/// <summary>
/// Installs the exit engines that are not part of the installer, and reports which are
/// available. Psiphon is downloaded on request from a commit-pinned URL, or copied from a
/// file the user downloaded, and accepted only with the exact pinned size and SHA-256; its
/// adapter checks the digest again before every start. OpenVPN is the user's own
/// installation, which this class only reports on.
/// </summary>
public sealed class ExitEngineInstaller
{
    private const int CopyBufferSize = 128 * 1024;
    private const string PartialSuffix = ".partial";
    private static readonly TimeSpan TransitionWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TransitionPollInterval = TimeSpan.FromSeconds(1);

    private readonly DownloadableEngine psiphon;
    private readonly ProtectedDirectory directory;
    private readonly IRoutingDataSource source;
    private readonly Func<CancellationToken, ValueTask<RoutingDataRouteState>> routeState;
    private readonly Func<OpenVpnLookup> openVpn;
    private readonly Func<bool> psiphonInUse;
    private readonly BrokerSignal? signal;
    private readonly ILogger logger;
    private readonly Lock sync = new();
    // Downloads, imports, and removals never overlap.
    private readonly SemaphoreSlim installGate = new(1, 1);
    private Task? download;
    private string? lastError;
    private VerifiedCopy? verified;

    /// <param name="directory">The broker-only directory the engines are installed in.</param>
    /// <param name="source">Downloads GitHub assets directly or through Paqet.</param>
    /// <param name="psiphonInUse">Whether the running route uses Psiphon, which then cannot be removed.</param>
    public ExitEngineInstaller(
        DownloadableEngine psiphon,
        ProtectedDirectory directory,
        IRoutingDataSource source,
        Func<CancellationToken, ValueTask<RoutingDataRouteState>> routeState,
        Func<OpenVpnLookup> openVpn,
        Func<bool> psiphonInUse,
        ILogger logger,
        BrokerSignal? signal = null)
    {
        this.psiphon = psiphon ?? throw new ArgumentNullException(nameof(psiphon));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.routeState = routeState ?? throw new ArgumentNullException(nameof(routeState));
        this.openVpn = openVpn ?? throw new ArgumentNullException(nameof(openVpn));
        this.psiphonInUse = psiphonInUse ?? throw new ArgumentNullException(nameof(psiphonInUse));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.signal = signal;
    }

    public string PsiphonPath => Path.Combine(directory.Path, psiphon.FileName);

    /// <summary>The download running now, if any; tests await it.</summary>
    internal Task? PendingDownload
    {
        get
        {
            lock (sync)
            {
                return download;
            }
        }
    }

    public IReadOnlyList<ExitEngineAvailability> GetAvailability()
    {
        bool downloading;
        string? error;
        lock (sync)
        {
            downloading = download is { IsCompleted: false };
            error = lastError;
        }

        var psiphonInstalled = IsPsiphonInstalled();
        var openVpnLookup = openVpn();
        return
        [
            new ExitEngineAvailability(
                ExitProxyKind.Psiphon,
                psiphonInstalled,
                psiphonInstalled ? psiphon.Version : null,
                CanDownload: true,
                IsDownloading: downloading,
                Detail: error ?? (psiphonInstalled ? null : $"Psiphon is not downloaded yet ({psiphon.Size / (1024 * 1024)} MB)."),
                DownloadUrl: psiphon.Uri.ToString()),
            new ExitEngineAvailability(
                ExitProxyKind.OpenVpn,
                openVpnLookup.Installation is not null,
                openVpnLookup.Installation?.Version.ToString(3),
                Detail: openVpnLookup.Problem,
                DownloadUrl: OpenVpnInstallation.DownloadPage.ToString()),
        ];
    }

    /// <summary>Applies a request from the desktop. A download returns once it has started.</summary>
    /// <param name="openFileAsUser">Opens the file an import names with the requesting user's own access.</param>
    public async Task ApplyAsync(
        ExitEngineAction action,
        Func<string, FileStream> openFileAsUser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Kind == ExitProxyKind.OpenVpn)
        {
            throw new InvalidOperationException(
                $"PaqetFire uses your own OpenVPN. Install OpenVPN Community {OpenVpnInstallation.MinimumVersion.ToString(2)} or newer from openvpn.net.");
        }

        if (action.Kind != ExitProxyKind.Psiphon || !Enum.IsDefined(action.Operation))
        {
            throw new InvalidOperationException("The exit engine request is not supported.");
        }

        switch (action.Operation)
        {
            case ExitEngineOperation.Download:
                StartDownload();
                break;
            case ExitEngineOperation.Import:
                await ImportAsync(action.SourcePath, openFileAsUser, cancellationToken).ConfigureAwait(false);
                break;
            case ExitEngineOperation.Remove:
                Remove();
                break;
        }
    }

    private void StartDownload()
    {
        lock (sync)
        {
            if (download is { IsCompleted: false })
            {
                return;
            }

            EnterInstallGate();
            lastError = null;
            download = Task.Run(DownloadAsync);
        }

        signal?.Notify();
    }

    private void EnterInstallGate()
    {
        if (!installGate.Wait(0))
        {
            throw new InvalidOperationException("Psiphon is being installed or removed. Try again in a moment.");
        }
    }

    private async Task DownloadAsync()
    {
        try
        {
            var path = await ChooseNetworkPathAsync(CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation("Downloading Psiphon {Version} {Path}.", psiphon.Version,
                path == RoutingDataNetworkPath.Tunnel ? "through Paqet" : "directly");
            await InstallAsync(
                    (destination, token) => source.DownloadAsync(path, psiphon.Uri, destination, psiphon.Size, token),
                    CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogInformation("Installed Psiphon {Version}.", psiphon.Version);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Psiphon could not be downloaded.");
            lock (sync)
            {
                lastError = $"Psiphon could not be downloaded: {Shorten(exception.Message)}";
            }
        }
        finally
        {
            installGate.Release();
            signal?.Notify();
        }
    }

    private async Task ImportAsync(
        string? sourcePath,
        Func<string, FileStream> openFileAsUser,
        CancellationToken cancellationToken)
    {
        if (!IsLocalDrivePath(sourcePath))
        {
            throw new InvalidDataException("Choose the Psiphon file on a local drive.");
        }

        EnterInstallGate();
        try
        {
            FileStream input;
            try
            {
                // Opened as the user, so a link on the drive cannot make the broker reach a network share.
                input = openFileAsUser(sourcePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"The Psiphon file could not be read: {exception.Message}", exception);
            }

            await using var _ = input;
            if (input.Length != psiphon.Size)
            {
                throw new InvalidDataException(
                    $"That file is not the Psiphon release PaqetFire uses ({psiphon.Version}). Download it from the link in Exit proxy settings.");
            }

            await InstallAsync((destination, token) => input.CopyToAsync(destination, CopyBufferSize, token), cancellationToken)
                .ConfigureAwait(false);
            lock (sync)
            {
                lastError = null;
            }
        }
        finally
        {
            installGate.Release();
        }

        logger.LogInformation("Installed Psiphon {Version} from a file the user chose.", psiphon.Version);
        signal?.Notify();
    }

    private void Remove()
    {
        if (psiphonInUse())
        {
            throw new InvalidOperationException("The connected profile uses Psiphon. Disconnect, or choose another exit, before removing it.");
        }

        EnterInstallGate();
        try
        {
            directory.Ensure();
            File.Delete(PsiphonPath);
            lock (sync)
            {
                verified = null;
                lastError = null;
            }
        }
        finally
        {
            installGate.Release();
        }

        logger.LogInformation("Removed Psiphon.");
        signal?.Notify();
    }

    /// <summary>
    /// Writes the engine beside its final name, checks the exact size and digest, and only
    /// then moves it into place. A file that fails is deleted.
    /// </summary>
    private async Task InstallAsync(Func<Stream, CancellationToken, Task> fill, CancellationToken cancellationToken)
    {
        directory.Ensure();
        foreach (var stale in Directory.EnumerateFiles(directory.Path, "*" + PartialSuffix))
        {
            File.Delete(stale);
        }

        var temporary = Path.Combine(directory.Path, $"{psiphon.FileName}.{Guid.NewGuid():N}{PartialSuffix}");
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous))
            {
                await fill(output, cancellationToken).ConfigureAwait(false);
                if (output.Length != psiphon.Size)
                {
                    throw new InvalidDataException($"The Psiphon file has {output.Length} bytes instead of {psiphon.Size}.");
                }

                output.Position = 0;
                var digest = await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(psiphon.Sha256)))
                {
                    throw new InvalidDataException("The Psiphon file does not match the SHA-256 PaqetFire pins, so it was discarded.");
                }

                output.Flush(flushToDisk: true);
            }

            lock (sync)
            {
                File.Move(temporary, PsiphonPath, overwrite: true);
                verified = null;
            }
        }
        catch (IOException exception) when (exception.HResult == unchecked((int)0x80070020))
        {
            throw new InvalidOperationException("Psiphon is running; disconnect before replacing it.", exception);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Hashes the installed file once per identity, so snapshots stay cheap.</summary>
    private bool IsPsiphonInstalled()
    {
        var path = PsiphonPath;
        var identity = FileIdentity.TryRead(path);
        if (identity is null)
        {
            return false;
        }

        lock (sync)
        {
            if (verified is { } cached && cached.Identity == identity)
            {
                return cached.Matches;
            }
        }

        bool matches;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize);
            matches = stream.Length == psiphon.Size &&
                      CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(psiphon.Sha256));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        lock (sync)
        {
            verified = new VerifiedCopy(identity, matches);
        }

        return matches;
    }

    /// <summary>
    /// Through Paqet while the route is up, directly while it is down, never directly while
    /// the kill switch holds traffic: the same rule routing data updates follow.
    /// </summary>
    private async Task<RoutingDataNetworkPath> ChooseNetworkPathAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TransitionWait;
        while (true)
        {
            var route = await routeState(cancellationToken).ConfigureAwait(false);
            if (!route.IsTransitioning)
            {
                if (route.IsConnected)
                {
                    return RoutingDataNetworkPath.Tunnel;
                }

                if (route.IsGuarded)
                {
                    throw new InvalidOperationException(
                        "the kill switch is holding traffic while the route is down. Connect a profile without an exit first, then download Psiphon through it.");
                }

                return RoutingDataNetworkPath.Direct;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException("a connection change was still running. Try again.");
            }

            await Task.Delay(TransitionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsLocalDrivePath([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? path) =>
        path is { Length: > 3 } &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == Path.DirectorySeparatorChar &&
        Path.IsPathFullyQualified(path) &&
        new DriveInfo(path[..1]).DriveType is DriveType.Fixed or DriveType.Removable;

    private static string Shorten(string message) => message.Length > 300 ? message[..300] : message;

    private sealed record VerifiedCopy(FileIdentity Identity, bool Matches);
}
