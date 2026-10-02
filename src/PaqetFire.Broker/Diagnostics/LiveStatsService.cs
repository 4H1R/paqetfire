using PaqetFire.Core.Connections;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Diagnostics;

public interface ILiveStatsService
{
    ValueTask<LiveConnectionStats> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Serves connection telemetry without blocking the IPC request loop. Traffic counters
/// are read synchronously from loopback Xray; network probes run in the background and
/// the latest results are returned on the next poll.
/// </summary>
public sealed class LiveStatsService(
    IConnectionController connectionController,
    IXrayTrafficStats trafficStats,
    ITunnelProbe tunnelProbe,
    IGeoIpCountryLookup geoIpLookup,
    ILogger<LiveStatsService> logger,
    TimeProvider? timeProvider = null) : ILiveStatsService
{
    private static readonly TimeSpan LatencyInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublicAddressRetryInterval = TimeSpan.FromSeconds(30);

    private readonly ITunnelProbe tunnelProbe = tunnelProbe;
    private readonly IGeoIpCountryLookup geoIpLookup = geoIpLookup;
    private readonly ILogger<LiveStatsService> logger = logger;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock sync = new();
    private Session? session;

    public async ValueTask<LiveConnectionStats> GetAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var status = await connectionController.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.State != ConnectionState.Connected)
        {
            EndSession();
            return new LiveConnectionStats(now, IsConnected: false);
        }

        var connectedSince = new[] { status.Paqet.ChangedAt, status.Xray.ChangedAt, status.ProxiFyre.ChangedAt }
            .Where(value => value is not null)
            .Max() ?? now;
        var current = GetOrStartSession(connectedSince);

        XrayTrafficCounters? counters = null;
        try
        {
            counters = await trafficStats.QueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Xray traffic counters are unavailable.");
        }

        current.ScheduleProbes(now);
        return new LiveConnectionStats(
            now,
            IsConnected: true,
            ConnectedSince: connectedSince,
            UplinkBytes: counters?.UplinkBytes,
            DownlinkBytes: counters?.DownlinkBytes,
            LatencyMilliseconds: current.LatencyMilliseconds,
            LatencyMeasuredAt: current.LatencyMeasuredAt,
            PublicAddress: current.PublicAddress,
            PublicAddressCountryCode: current.PublicAddressCountryCode);
    }

    private Session GetOrStartSession(DateTimeOffset connectedSince)
    {
        lock (sync)
        {
            if (session?.ConnectedSince == connectedSince)
            {
                return session;
            }

            session?.Dispose();
            session = new Session(this, connectedSince);
            return session;
        }
    }

    private void EndSession()
    {
        lock (sync)
        {
            session?.Dispose();
            session = null;
        }
    }

    private sealed class Session(LiveStatsService owner, DateTimeOffset connectedSince) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private int probing;
        private DateTimeOffset nextLatencyAt;
        private DateTimeOffset nextPublicAddressAt;

        public DateTimeOffset ConnectedSince { get; } = connectedSince;

        public volatile string? PublicAddress;

        public volatile string? PublicAddressCountryCode;

        private int latencyMilliseconds = -1;
        private long latencyMeasuredAtTicks;

        public int? LatencyMilliseconds => Volatile.Read(ref latencyMilliseconds) is var value and >= 0 ? value : null;

        public DateTimeOffset? LatencyMeasuredAt => Interlocked.Read(ref latencyMeasuredAtTicks) is var ticks and > 0
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

        public void ScheduleProbes(DateTimeOffset now)
        {
            var needsLatency = now >= nextLatencyAt;
            var needsAddress = PublicAddress is null && now >= nextPublicAddressAt;
            if ((!needsLatency && !needsAddress) || Interlocked.CompareExchange(ref probing, 1, 0) != 0)
            {
                return;
            }

            if (needsLatency)
            {
                nextLatencyAt = now + LatencyInterval;
            }
            if (needsAddress)
            {
                nextPublicAddressAt = now + PublicAddressRetryInterval;
            }

            var token = cancellation.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (needsLatency)
                    {
                        var latency = await owner.tunnelProbe.MeasureLatencyAsync(token).ConfigureAwait(false);
                        Volatile.Write(ref latencyMilliseconds, latency ?? -1);
                        Interlocked.Exchange(ref latencyMeasuredAtTicks, owner.clock.GetUtcNow().UtcTicks);
                    }

                    if (needsAddress)
                    {
                        var address = await owner.tunnelProbe.GetPublicAddressAsync(token).ConfigureAwait(false);
                        if (address is not null)
                        {
                            PublicAddressCountryCode = await TryFindCountryAsync(address, token).ConfigureAwait(false);
                            PublicAddress = address.ToString();
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // The connection ended while a probe was running.
                }
                catch (Exception exception)
                {
                    owner.logger.LogDebug(exception, "A live connection probe failed.");
                }
                finally
                {
                    Volatile.Write(ref probing, 0);
                }
            }, token);
        }

        // The source is only cancelled, never disposed, so an in-flight probe can
        // still observe its token safely.
        public void Dispose() => cancellation.Cancel();

        private async ValueTask<string?> TryFindCountryAsync(System.Net.IPAddress address, CancellationToken token)
        {
            try
            {
                return await owner.geoIpLookup.FindCountryCodeAsync(address, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                owner.logger.LogDebug(exception, "The exit address country could not be resolved.");
                return null;
            }
        }
    }
}
