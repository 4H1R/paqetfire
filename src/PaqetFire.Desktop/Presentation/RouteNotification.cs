using PaqetFire.Core.Ipc;
using CoreConnectionState = PaqetFire.Core.Connections.ConnectionState;
using TunnelHealthState = PaqetFire.Core.Connections.TunnelHealthState;

namespace PaqetFire.Desktop.Presentation;

public enum RouteNotificationKind
{
    RouteDropped,
    KillSwitchBlocking,
    Reconnecting,
    Recovered,
}

public sealed record RouteNotification(RouteNotificationKind Kind, string Title, string Message);

/// <summary>Decides which route transitions deserve a Windows notification while the window is hidden.</summary>
public static class RouteNotificationPolicy
{
    public static RouteNotification? Classify(BrokerSnapshot? previous, BrokerSnapshot current, bool userInitiated)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null || userInitiated)
        {
            return null;
        }

        if (current.ConnectionState == CoreConnectionState.Guarded &&
            previous.ConnectionState != CoreConnectionState.Guarded)
        {
            return new(RouteNotificationKind.KillSwitchBlocking,
                "Kill switch is blocking routed apps",
                "The route is down. Protected applications stay offline until PaqetFire reconnects or you turn the kill switch off.");
        }

        // A route counts as dropped when the broker has stopped trying, not while Paqet
        // restarts: during recovery the chain is not routing but Progress stays set.
        if ((previous.IsRouting || IsRecovering(previous)) && !current.IsRouting && current.Progress is null &&
            current.ConnectionState is not (CoreConnectionState.Guarded or CoreConnectionState.Connecting))
        {
            return new(RouteNotificationKind.RouteDropped,
                "PaqetFire route dropped",
                "Traffic is no longer routed through Paqet. Open PaqetFire to see what happened.");
        }

        // Once per episode: between attempts the broker reports a tunnel test, not a stable route.
        if (IsRecovering(current) && !IsRecovering(previous) && previous.IsRouting && previous.Progress is null)
        {
            return new(RouteNotificationKind.Reconnecting,
                "Reconnecting the Paqet transport",
                "The Paqet server stopped answering. PaqetFire is restarting the transport; routed traffic waits meanwhile.");
        }

        if ((IsRecovering(previous) || previous.Tunnel?.State == TunnelHealthState.Stalled) &&
            current.IsRouting && current.Progress is null &&
            current.Tunnel?.State is TunnelHealthState.Healthy)
        {
            return new(RouteNotificationKind.Recovered,
                "PaqetFire route recovered",
                "Traffic is passing through Paqet again.");
        }

        return null;
    }

    private static bool IsRecovering(BrokerSnapshot snapshot) =>
        snapshot.Progress?.Stage == ConnectionStage.RecoveringTransport ||
        (snapshot.IsRouting && snapshot.Tunnel?.State == TunnelHealthState.Recovering);
}
