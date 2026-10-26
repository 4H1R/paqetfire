using PaqetFire.Broker.Configuration;
using PaqetFire.Core.Usage;

namespace PaqetFire.Broker.Diagnostics;

/// <summary>One run of the Xray process, and the profile its traffic is charged to.</summary>
public sealed record TrafficSession(DateTimeOffset StartedAt, Guid ProfileId);

public interface IDataUsageService
{
    ValueTask<DataUsageReport> GetReportAsync(CancellationToken cancellationToken);

    ValueTask<DataUsageReport> ResetAsync(DataUsageReset reset, CancellationToken cancellationToken);
}

/// <summary>
/// Accumulates the routed traffic counters of each Xray run into daily totals per profile.
/// Xray's counters restart at zero with its process, so each sample adds only what grew
/// since the previous one. A run is charged to the profile that was active when it was
/// first sampled: a profile switch saves the new profile before the old route stops.
/// </summary>
public sealed class DataUsageRecorder(
    IXrayTrafficStats trafficStats,
    Func<CancellationToken, ValueTask<TrafficSession?>> currentSession,
    IDataUsageStore store,
    ILogger<DataUsageRecorder> logger,
    TimeProvider? timeProvider = null) : IDataUsageService
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DataUsageLedger? ledger;
    private Baseline? baseline;
    private bool dirty;
    private DateTimeOffset lastSavedAt;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SampleInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await SampleAsync(stoppingToken).ConfigureAwait(false);
                    if (dirty && clock.GetUtcNow() - lastSavedAt >= SaveInterval)
                    {
                        await SaveAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }

        await CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Records what Xray counted so far and saves it; called before Xray stops.</summary>
    public async ValueTask CheckpointAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SampleAsync(cancellationToken).ConfigureAwait(false);
            if (dirty)
            {
                await SaveAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<DataUsageReport> GetReportAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SampleAsync(cancellationToken).ConfigureAwait(false);
            return CreateReport();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<DataUsageReport> ResetAsync(DataUsageReset reset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reset);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Count up to now first, so the reset also clears what the current run already moved.
            await SampleAsync(cancellationToken).ConfigureAwait(false);
            if (Ledger.Reset(reset.ProfileId))
            {
                dirty = true;
                await SaveAsync().ConfigureAwait(false);
            }

            return CreateReport();
        }
        finally
        {
            gate.Release();
        }
    }

    private DataUsageLedger Ledger
    {
        get
        {
            if (ledger is null)
            {
                try
                {
                    ledger = new DataUsageLedger(store.Load());
                }
                catch (ArgumentException exception)
                {
                    logger.LogWarning(exception, "The recorded data usage was invalid and has been discarded.");
                    ledger = new DataUsageLedger();
                }

                dirty = ledger.Prune(Today);
                lastSavedAt = clock.GetUtcNow();
            }

            return ledger;
        }
    }

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone).DateTime);

    private DataUsageReport CreateReport()
    {
        var today = Today;
        dirty |= Ledger.Prune(today);
        return new DataUsageReport(today, Ledger.ToProfiles());
    }

    // Callers hold the gate.
    private async ValueTask SampleAsync(CancellationToken cancellationToken)
    {
        var usage = Ledger;
        TrafficSession? session;
        XrayTrafficCounters? counters;
        try
        {
            session = await currentSession(cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                baseline = null;
                return;
            }

            counters = await trafficStats.QueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Xray traffic counters are unavailable for data usage.");
            return;
        }

        if (counters is null)
        {
            return;
        }

        var previous = baseline;
        if (previous is null ||
            previous.StartedAt != session.StartedAt ||
            counters.DownlinkBytes < previous.DownlinkBytes ||
            counters.UplinkBytes < previous.UplinkBytes)
        {
            // A new Xray run: its counters started at zero.
            previous = new Baseline(session.StartedAt, session.ProfileId, 0, 0);
        }

        var download = counters.DownlinkBytes - previous.DownlinkBytes;
        var upload = counters.UplinkBytes - previous.UplinkBytes;
        if (download > 0 || upload > 0)
        {
            usage.Add(previous.ProfileId, Today, download, upload);
            dirty = true;
        }

        baseline = previous with { DownlinkBytes = counters.DownlinkBytes, UplinkBytes = counters.UplinkBytes };
    }

    // Callers hold the gate.
    private async ValueTask SaveAsync()
    {
        try
        {
            Ledger.Prune(Today);
            await store.SaveAsync(Ledger.ToProfiles(), CancellationToken.None).ConfigureAwait(false);
            dirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The data usage history could not be saved.");
        }
        finally
        {
            // A failed save is retried at the next interval, not on every sample.
            lastSavedAt = clock.GetUtcNow();
        }
    }

    private sealed record Baseline(DateTimeOffset StartedAt, Guid ProfileId, long DownlinkBytes, long UplinkBytes);
}
