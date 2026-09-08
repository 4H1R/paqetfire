using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using System.Threading.Channels;

namespace PaqetFire.Broker;

public sealed class BrokerWorker(
    ILogger<BrokerWorker> logger,
    PayloadIntegrityInspector payloadInspector,
    IPaqetFireRuntime runtime,
    NamedPipeBrokerServer pipeServer) : BackgroundService
{
    private readonly Channel<BrokerEvent> publications = Channel.CreateBounded<BrokerEvent>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var payload = await payloadInspector.InspectAsync(stoppingToken);
        if (payload.State == PayloadInspectionState.Ready)
        {
            logger.LogInformation(
                "PaqetFire Broker started with a verified bundled engine payload.");
        }
        else
        {
            logger.LogWarning(
                "PaqetFire Broker started without an active engine payload: {Detail}",
                payload.Detail);
        }

        await runtime.InitializeAsync(stoppingToken);

        var pipeTask = pipeServer.RunAsync(stoppingToken);
        var monitorTask = MonitorAsync(stoppingToken);
        var publicationTask = PublishEventsAsync(stoppingToken);
        await Task.WhenAll(pipeTask, monitorTask, publicationTask);

        logger.LogInformation("PaqetFire Broker stopped.");
    }

    internal async Task MonitorAsync(CancellationToken stoppingToken, TimeSpan? interval = null)
    {
        BrokerSnapshot? previousSnapshot = null;
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                var snapshot = await runtime.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                if (ConnectionRecoveryPolicy.RequiresSafeRecovery(
                        snapshot.ConnectionState,
                        snapshot.IsKillSwitchEnabled,
                        snapshot.Engines))
                {
                    logger.LogWarning(
                        "The engine chain is unhealthy; restoring the configured safe disconnected state.");
                    snapshot = await runtime.DisconnectAsync(stoppingToken).ConfigureAwait(false);
                }

                if (previousSnapshot is not null && HasSamePublishedState(previousSnapshot, snapshot))
                {
                    previousSnapshot = snapshot;
                    continue;
                }

                previousSnapshot = snapshot;

                publications.Writer.TryWrite(
                        new BrokerEvent(
                            Guid.NewGuid(),
                            IpcProtocol.Version,
                            BrokerEventKind.SnapshotChanged,
                            DateTimeOffset.UtcNow,
                            snapshot));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The connection health monitor failed.");
                previousSnapshot = null;
                publications.Writer.TryWrite(
                        new BrokerEvent(
                            Guid.NewGuid(),
                            IpcProtocol.Version,
                            BrokerEventKind.Faulted,
                            DateTimeOffset.UtcNow,
                            Error: new BrokerError(
                                BrokerErrorCode.InternalError,
                                "The connection health check failed.")));
            }
        }
    }

    private async Task PublishEventsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var publication in publications.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await pipeServer.PublishAsync(publication, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "The broker snapshot could not be published.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
    }

    private static bool HasSamePublishedState(BrokerSnapshot previous, BrokerSnapshot current) =>
        previous.IsRouting == current.IsRouting &&
        previous.IsKillSwitchEnabled == current.IsKillSwitchEnabled &&
        previous.IsConfigured == current.IsConfigured &&
        previous.ConnectionState == current.ConnectionState &&
        string.Equals(previous.StatusMessage, current.StatusMessage, StringComparison.Ordinal) &&
        HasSameSettings(previous.Settings, current.Settings) &&
        previous.Engines.SequenceEqual(current.Engines) &&
        (previous.Prerequisites ?? []).SequenceEqual(current.Prerequisites ?? []) &&
        (previous.RecentLogs ?? []).SequenceEqual(current.RecentLogs ?? []);

    private static bool HasSameSettings(PaqetFire.Core.Configuration.PaqetFireSettingsView? previous,
        PaqetFire.Core.Configuration.PaqetFireSettingsView? current)
    {
        if (ReferenceEquals(previous, current))
        {
            return true;
        }

        if (previous is null || current is null)
        {
            return false;
        }

        return previous.ProfileName == current.ProfileName &&
               previous.ServerEndpoint == current.ServerEndpoint &&
               previous.HasTransportKey == current.HasTransportKey &&
               previous.RoutingMode == current.RoutingMode &&
               previous.BypassLan == current.BypassLan &&
               previous.RouteTcp == current.RouteTcp &&
               previous.RouteUdp == current.RouteUdp &&
               previous.RouteIpv4 == current.RouteIpv4 &&
               previous.RouteIpv6 == current.RouteIpv6 &&
               previous.RegionalPreset == current.RegionalPreset &&
               previous.DomainStrategy == current.DomainStrategy &&
               previous.BlockAds == current.BlockAds &&
               previous.BlockQuic == current.BlockQuic &&
               previous.DirectBitTorrent == current.DirectBitTorrent &&
               previous.KillSwitchEnabled == current.KillSwitchEnabled &&
               previous.ShareWithLan == current.ShareWithLan &&
               previous.LanSocksPort == current.LanSocksPort &&
               previous.LanSocksUsername == current.LanSocksUsername &&
               previous.HasLanSocksPassword == current.HasLanSocksPassword &&
               previous.ShareViaHotspot == current.ShareViaHotspot &&
               previous.HotspotSocksPort == current.HotspotSocksPort &&
               previous.KcpMode == current.KcpMode &&
               previous.SelectedApplications.SequenceEqual(current.SelectedApplications) &&
               previous.UserExclusions.SequenceEqual(current.UserExclusions) &&
               previous.LocalTcpFlags.SequenceEqual(current.LocalTcpFlags) &&
               previous.RemoteTcpFlags.SequenceEqual(current.RemoteTcpFlags);
    }
}
