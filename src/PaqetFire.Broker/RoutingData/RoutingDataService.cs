using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.RoutingData;

namespace PaqetFire.Broker.RoutingData;

/// <param name="IsConnected">The route is up, so a download uses the Paqet hop.</param>
/// <param name="IsTransitioning">A connect, reconnect, or transport recovery is running.</param>
/// <param name="IsGuarded">The kill switch holds routed traffic while the route is down.</param>
public sealed record RoutingDataRouteState(bool IsConnected, bool IsTransitioning, bool IsGuarded = false);

public enum RoutingDataUpdateOutcome
{
    Updated,
    UpToDate,
    Failed,
    Cancelled,
}

public sealed record RoutingDataServiceOptions
{
    public required string BundledXrayVersion { get; init; }

    /// <summary>Lets a route restored at broker startup come up first, so the download can use it.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a failed check waits before the automatic schedule tries again.</summary>
    public TimeSpan FailedCheckRetryInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan TransitionWait { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan TransitionPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The most one update may take, across every request it makes.</summary>
    public TimeSpan AttemptDeadline { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The least time between two updates a user asks for, so IPC cannot drive downloads in a loop.</summary>
    public TimeSpan ManualUpdateInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>A routing data request the broker declines for now; the message says why.</summary>
public sealed class RoutingDataBusyException(string message) : InvalidOperationException(message);

/// <summary>
/// Downloads the Iran-v2ray-rules GeoIP/GeoSite release, verifies it against its
/// published checksums and with the pinned Xray, and installs it for the next Xray start.
/// Updates run in the background, one at a time, and never hold the runtime gate.
/// </summary>
public sealed class RoutingDataService : IDisposable
{
    private const int MaximumChecksumBytes = 4096;
    private const int MaximumErrorLength = 300;

    private readonly RoutingDataServiceOptions options;
    private readonly RoutingDataStore store;
    private readonly IRoutingDataSource source;
    private readonly IRoutingDataValidator validator;
    private readonly RoutingDataFileVerifier verifier;
    private readonly ILogger<RoutingDataService> logger;
    private readonly Func<CancellationToken, ValueTask<RoutingDataRouteState>> routeState;
    private readonly Func<IEnumerable<string?>> inUse;
    private readonly BrokerSignal? signal;
    private readonly TimeProvider clock;
    private readonly Lock sync = new();
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource shutdown = new();
    private RoutingDataState state;
    private Task<RoutingDataUpdateOutcome>? running;
    private CancellationTokenSource? runningCancellation;
    private bool isUpdating;
    private bool removingDownload;
    private DateTimeOffset? retryAfter;
    private DateTimeOffset? lastManualUpdate;
    private string? lastResolvedDirectory;
    private FailedRelease? lastFailedRelease;

    /// <param name="inUse">Release directories an engine has been told to load; they are never deleted.</param>
    public RoutingDataService(
        RoutingDataServiceOptions options,
        RoutingDataStore store,
        IRoutingDataSource source,
        IRoutingDataValidator validator,
        RoutingDataFileVerifier verifier,
        ILogger<RoutingDataService> logger,
        Func<CancellationToken, ValueTask<RoutingDataRouteState>>? routeState = null,
        BrokerSignal? signal = null,
        TimeProvider? timeProvider = null,
        Func<IEnumerable<string?>>? inUse = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.routeState = routeState ?? (_ => ValueTask.FromResult(new RoutingDataRouteState(false, false)));
        this.inUse = inUse ?? (() => []);
        this.signal = signal;
        clock = timeProvider ?? TimeProvider.System;
        // Creates and secures the root at startup, before anything reads from it.
        state = store.Load();
    }

    public RoutingDataStatus Status
    {
        get
        {
            lock (sync)
            {
                var installed = state.IsInstalled;
                return new RoutingDataStatus(
                    installed ? RoutingDataOrigin.Updated : RoutingDataOrigin.Bundled,
                    installed ? state.InstalledTag! : $"Xray {options.BundledXrayVersion}",
                    installed ? state.InstalledAt : null,
                    state.LastCheckedAt,
                    state.AutomaticUpdates,
                    isUpdating,
                    // Malware rules need the downloaded data, which a skipped release does not provide.
                    SupportsMalwareCategories: installed && !state.IsSkippedForLaunch,
                    state.LastError);
            }
        }
    }

    internal RoutingDataState State
    {
        get
        {
            lock (sync)
            {
                return state;
            }
        }
    }

    /// <summary>
    /// The data the next Xray start should load: the installed release once its files match
    /// the recorded sizes and digests, otherwise the bundled files.
    /// </summary>
    public async ValueTask<RoutingDataSet> ResolveForLaunchAsync(CancellationToken cancellationToken)
    {
        var current = State;
        if (!current.IsInstalled || current.IsSkippedForLaunch)
        {
            RememberResolved(null);
            return RoutingDataSet.Bundled;
        }

        try
        {
            if (store.EnsureRoot())
            {
                throw new InvalidDataException("The routing data directory had to be recreated.");
            }

            var set = new RoutingDataSet(store.ReleasePath(current.InstalledDirectory!), current.InstalledTag!, current.Files);
            // Remembered before verification, so an update or Use bundled data cannot delete
            // the files between this check and the Xray start that loads them.
            RememberResolved(set.AssetDirectory);
            await using var lease = await verifier.OpenAsync(set, cancellationToken).ConfigureAwait(false);
            return set;
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "The downloaded routing data failed verification; Xray uses its bundled data.");
            RememberResolved(null);
            await ForgetInstallationAsync(current.InstalledDirectory,
                    "The downloaded routing data failed its integrity check, so Xray uses its bundled data. Update again to replace it.")
                .ConfigureAwait(false);
            return RoutingDataSet.Bundled;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            // For example an antivirus scan holding the file: not corruption. Only this launch falls back.
            logger.LogWarning(exception, "The downloaded routing data could not be read; this Xray start uses its bundled data.");
            RememberResolved(null);
            return RoutingDataSet.Bundled;
        }
    }

    /// <summary>
    /// Records that Xray could not start with <paramref name="set"/> and was started with the
    /// bundled data instead. Data that failed verification is forgotten.
    /// </summary>
    public async ValueTask ReportLaunchFailureAsync(RoutingDataSet set, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!set.IsUpdated)
        {
            return;
        }

        logger.LogWarning(exception, "Xray could not start with routing data {Version}; it used the bundled data.", set.Version);
        var directory = Path.GetFileName(set.AssetDirectory);
        if (FindCause<InvalidDataException>(exception) is not null)
        {
            await ForgetInstallationAsync(directory,
                    "The downloaded routing data failed its integrity check when Xray started, so the bundled data is used. Update again to replace it.")
                .ConfigureAwait(false);
            return;
        }

        await MutateAsync(stored => stored with
            {
                LaunchTimedOutTag = set.Version,
                LastError = $"Xray did not start in time with the downloaded routing data {set.Version}, so PaqetFire uses the bundled data until the next routing data check.",
            })
            .ConfigureAwait(false);
    }

    public async Task<RoutingDataStatus> ApplyAsync(RoutingDataAction action, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case RoutingDataAction.UpdateNow:
                // Reported through snapshots; a download over a slow tunnel can take minutes.
                _ = StartUpdate(manual: true);
                break;

            case RoutingDataAction.EnableAutomaticUpdates:
                await MutateAsync(stored => stored with { AutomaticUpdates = true }).ConfigureAwait(false);
                Wake();
                break;

            case RoutingDataAction.DisableAutomaticUpdates:
                await MutateAsync(stored => stored with { AutomaticUpdates = false }).ConfigureAwait(false);
                break;

            case RoutingDataAction.UseBundled:
                await UseBundledAsync().ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException("The routing data action is not supported.");
        }

        return Status;
    }

    /// <summary>Returns the running update, or starts one.</summary>
    /// <exception cref="RoutingDataBusyException">A user's update came too soon after the last one, or the downloaded data is being removed.</exception>
    public Task<RoutingDataUpdateOutcome> StartUpdate(bool manual = false)
    {
        Task<RoutingDataUpdateOutcome> started;
        lock (sync)
        {
            if (removingDownload)
            {
                if (manual)
                {
                    throw new RoutingDataBusyException("The downloaded routing data is being removed. Try again in a moment.");
                }

                return Task.FromResult(RoutingDataUpdateOutcome.Cancelled);
            }

            if (running is { IsCompleted: false } existing)
            {
                return existing;
            }

            var now = clock.GetUtcNow();
            if (manual)
            {
                if (lastManualUpdate is { } last && now - last < options.ManualUpdateInterval && now >= last)
                {
                    var wait = Math.Max(1, (int)Math.Ceiling((last + options.ManualUpdateInterval - now).TotalMinutes));
                    throw new RoutingDataBusyException(
                        $"Routing data was checked moments ago. Try again in {wait} minute{(wait == 1 ? string.Empty : "s")}.");
                }

                lastManualUpdate = now;
            }

            isUpdating = true;
            runningCancellation?.Dispose();
            runningCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            var token = runningCancellation.Token;
            started = running = Task.Run(() => UpdateCoreAsync(token), CancellationToken.None);
        }

        signal?.Notify();
        return started;
    }

    /// <summary>Checks once a day while automatic updates are on, and an hour after a failure.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var registration = stoppingToken.Register(shutdown.Cancel);
        // Leftovers of an interrupted download or an earlier release.
        store.DeleteReleasesExcept(KeptDirectories(State.InstalledDirectory));
        try
        {
            await Task.Delay(options.StartupDelay, clock, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                DateTimeOffset? due;
                lock (sync)
                {
                    due = NextAutomaticCheck(state, retryAfter, now);
                }

                if (due is { } at && at <= now)
                {
                    if (await StartUpdate().ConfigureAwait(false) == RoutingDataUpdateOutcome.Cancelled)
                    {
                        // Cancelled by Use bundled data, which also turned the schedule off.
                        await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // Re-evaluated at least hourly, which also covers a clock change.
                var wait = due is { } next ? next - now : TimeSpan.FromHours(1);
                wait = wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
                await wake.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Broker shutdown.
        }
    }

    internal static DateTimeOffset? NextAutomaticCheck(RoutingDataState state, DateTimeOffset? retryAfter, DateTimeOffset now)
    {
        if (!state.AutomaticUpdates)
        {
            return null;
        }

        // A last check in the future means the clock moved back; checking again is safest.
        var due = state.LastCheckedAt is { } last && last <= now
            ? last + RoutingDataRelease.AutomaticCheckInterval
            : DateTimeOffset.MinValue;
        return retryAfter is { } retry && retry > due && retry - now <= TimeSpan.FromDays(1) ? retry : due;
    }

    public void Dispose()
    {
        shutdown.Cancel();
        shutdown.Dispose();
        lock (sync)
        {
            runningCancellation?.Dispose();
            runningCancellation = null;
        }
    }

    private async Task<RoutingDataUpdateOutcome> UpdateCoreAsync(CancellationToken cancellationToken)
    {
        string? staging = null;
        using var deadline = new CancellationTokenSource(options.AttemptDeadline, clock);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = attempt.Token;
        try
        {
            var path = await ChooseNetworkPathAsync(token).ConfigureAwait(false);
            var tag = await source.GetLatestTagAsync(path, token).ConfigureAwait(false);
            if (!RoutingDataRelease.IsValidTag(tag, clock.GetUtcNow()))
            {
                throw new InvalidDataException("GitHub named a routing data release PaqetFire does not accept.");
            }

            // Any other release than the installed one is taken, including an older one: the
            // newer release was withdrawn upstream.
            if (string.Equals(tag, State.InstalledTag, StringComparison.Ordinal))
            {
                // A check is also the retry for a release Xray once started too slowly with.
                await MutateAsync(stored => stored with { LastCheckedAt = clock.GetUtcNow(), LastError = null, LaunchTimedOutTag = null })
                    .ConfigureAwait(false);
                ClearRetry();
                logger.LogInformation("Routing data {Tag} is current.", tag);
                return RoutingDataUpdateOutcome.UpToDate;
            }

            var checksums = await DownloadChecksumsAsync(path, tag, token).ConfigureAwait(false);
            RejectRecentlyFailedRelease(tag, checksums);
            staging = store.CreateStagingDirectory();
            IReadOnlyList<RoutingDataFile> files;
            try
            {
                files = await DownloadFilesAsync(path, tag, checksums, staging, token).ConfigureAwait(false);
                await validator.ValidateAsync(staging, token).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                // The release itself is bad; do not fetch the same bytes again before the retry interval.
                lock (sync)
                {
                    lastFailedRelease = new FailedRelease(tag, checksums, clock.GetUtcNow(), Describe(exception));
                }

                throw;
            }

            token.ThrowIfCancellationRequested();
            var directory = RoutingDataStore.CreateReleaseDirectoryName(tag);
            store.Promote(staging, directory);
            staging = null;
            var previous = State.InstalledDirectory;
            var now = clock.GetUtcNow();
            await MutateAsync(stored => stored with
                {
                    InstalledTag = tag,
                    InstalledDirectory = directory,
                    InstalledAt = now,
                    Files = files,
                    LastCheckedAt = now,
                    LastError = null,
                    LaunchTimedOutTag = null,
                })
                .ConfigureAwait(false);
            ClearRetry();
            // The previous release stays until the next update: a connect may have chosen it already.
            store.DeleteReleasesExcept(KeptDirectories(directory, previous));
            logger.LogInformation("Installed routing data {Tag}; Xray uses it from its next start.", tag);
            return RoutingDataUpdateOutcome.Updated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RoutingDataUpdateOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && deadline.IsCancellationRequested)
            {
                exception = new TimeoutException(
                    $"The routing data update did not finish within {options.AttemptDeadline.TotalMinutes:0.#} minutes.");
            }

            logger.LogWarning(exception, "The routing data update failed; the current data stays in use.");
            lock (sync)
            {
                retryAfter = clock.GetUtcNow() + options.FailedCheckRetryInterval;
            }

            var message = Describe(exception);
            await MutateAsync(stored => stored with { LastError = message }).ConfigureAwait(false);
            return RoutingDataUpdateOutcome.Failed;
        }
        finally
        {
            RoutingDataStore.TryDeleteDirectory(staging);
            lock (sync)
            {
                isUpdating = false;
            }

            signal?.Notify();
        }
    }

    /// <summary>
    /// Through Paqet while the route is up, directly while it is down. Never directly while
    /// the route is up or the kill switch holds traffic: that would leave outside the route
    /// the user chose, so a failure there is reported instead.
    /// </summary>
    private async Task<RoutingDataNetworkPath> ChooseNetworkPathAsync(CancellationToken cancellationToken)
    {
        // A download through a route that is being rebuilt would fail.
        var deadline = clock.GetUtcNow() + options.TransitionWait;
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
                        "The kill switch is holding traffic while the route is down, so routing data is not downloaded outside it. The update runs once the route is back.");
                }

                return RoutingDataNetworkPath.Direct;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                throw new InvalidOperationException("A connection change was still running; the update will try again later.");
            }

            await Task.Delay(options.TransitionPollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> DownloadChecksumsAsync(
        RoutingDataNetworkPath path,
        string tag,
        CancellationToken cancellationToken)
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in RoutingDataRelease.FileNames)
        {
            using var checksum = new MemoryStream();
            await source.DownloadAsync(path, RoutingDataRelease.ChecksumUri(tag, name), checksum, MaximumChecksumBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!RoutingDataRelease.TryParseChecksum(Encoding.UTF8.GetString(checksum.ToArray()), name, out var expected))
            {
                throw new InvalidDataException($"The published checksum for {name} could not be read.");
            }

            checksums[name] = expected;
        }

        return checksums;
    }

    private void RejectRecentlyFailedRelease(string tag, IReadOnlyDictionary<string, string> checksums)
    {
        FailedRelease? failed;
        lock (sync)
        {
            failed = lastFailedRelease;
        }

        if (failed is not null &&
            string.Equals(failed.Tag, tag, StringComparison.Ordinal) &&
            RoutingDataRelease.FileNames.All(name => string.Equals(failed.Checksums[name], checksums[name], StringComparison.Ordinal)) &&
            clock.GetUtcNow() - failed.At < options.FailedCheckRetryInterval)
        {
            throw new InvalidDataException(failed.Message);
        }
    }

    private async Task<IReadOnlyList<RoutingDataFile>> DownloadFilesAsync(
        RoutingDataNetworkPath path,
        string tag,
        IReadOnlyDictionary<string, string> checksums,
        string staging,
        CancellationToken cancellationToken)
    {
        var files = new List<RoutingDataFile>();
        foreach (var name in RoutingDataRelease.FileNames)
        {
            var target = Path.Combine(staging, name);
            string digest;
            long size;
            await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await source.DownloadAsync(path, RoutingDataRelease.AssetUri(tag, name), file, RoutingDataRelease.MaximumFileBytes, cancellationToken)
                    .ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                size = file.Length;
                file.Position = 0;
                digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            }

            if (size == 0 || !string.Equals(digest, checksums[name], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The downloaded {name} does not match its published checksum.");
            }

            files.Add(new RoutingDataFile(name, size, digest));
        }

        return files;
    }

    private async Task UseBundledAsync()
    {
        lock (sync)
        {
            // Holds off any update, manual or scheduled, until the removal is complete.
            removingDownload = true;
        }

        try
        {
            await MutateAsync(stored => stored with { AutomaticUpdates = false }).ConfigureAwait(false);
            Task<RoutingDataUpdateOutcome>? update;
            lock (sync)
            {
                update = running;
                if (update is { IsCompleted: false })
                {
                    runningCancellation?.Cancel();
                }
            }

            if (update is not null)
            {
                await update.ConfigureAwait(false);
            }

            await MutateAsync(stored => stored.WithoutInstallation() with { LastError = null }).ConfigureAwait(false);
            // A release a connect has already chosen stays until the next update or broker start.
            store.DeleteReleasesExcept(KeptDirectories());
            logger.LogInformation("Downloaded routing data removed; Xray uses its bundled data from its next start.");
        }
        finally
        {
            lock (sync)
            {
                removingDownload = false;
            }
        }
    }

    private IEnumerable<string?> KeptDirectories(params string?[] also)
    {
        string? resolved;
        lock (sync)
        {
            resolved = lastResolvedDirectory;
        }

        return also
            .Append(resolved)
            .Concat(inUse())
            .Select(directory => directory is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))
            .ToArray();
    }

    private void RememberResolved(string? directory)
    {
        lock (sync)
        {
            lastResolvedDirectory = directory;
        }
    }

    private ValueTask ForgetInstallationAsync(string? directory, string message) =>
        MutateAsync(stored => stored.InstalledDirectory == directory && directory is not null
            ? stored.WithoutInstallation() with { LastError = message }
            : stored);

    private async ValueTask MutateAsync(Func<RoutingDataState, RoutingDataState> change)
    {
        await saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            RoutingDataState next;
            lock (sync)
            {
                next = change(state);
                if (next == state)
                {
                    return;
                }

                state = next;
            }

            try
            {
                await store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(exception, "The routing data state could not be saved.");
            }
        }
        finally
        {
            saveGate.Release();
        }

        signal?.Notify();
    }

    private void ClearRetry()
    {
        lock (sync)
        {
            retryAfter = null;
        }
    }

    private void Wake()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    internal static T? FindCause<T>(Exception? exception)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (FindCause<T>(inner) is { } nested)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>A short, user-facing reason for the Settings page and diagnostics.</summary>
    internal static string Describe(Exception exception)
    {
        var message = exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
                "GitHub refused the request, probably its hourly limit for this network. Try again later.",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                "The routing data release was not found on GitHub.",
            HttpRequestException { StatusCode: { } status } =>
                $"GitHub answered with HTTP {(int)status}.",
            HttpRequestException => "GitHub could not be reached.",
            TimeoutException timeout => timeout.Message,
            InvalidDataException invalid => invalid.Message,
            InvalidOperationException invalid => invalid.Message,
            CryptographicException => "The bundled Xray executable failed its integrity check.",
            IOException or UnauthorizedAccessException => $"The routing data could not be saved: {exception.Message}",
            _ => "The routing data update failed unexpectedly.",
        };
        return message.Length > MaximumErrorLength ? message[..MaximumErrorLength] + "…" : message;
    }

    private sealed record FailedRelease(string Tag, IReadOnlyDictionary<string, string> Checksums, DateTimeOffset At, string Message);
}
