using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public enum ConnectionRecoveryAction
{
    None,
    /// <summary>Only Paqet is down; Xray and ProxiFyre keep routed traffic failing closed.</summary>
    RestartTransport,
    SafeRecovery,
}

public static class ConnectionRecoveryPolicy
{
    public static ConnectionRecoveryAction Evaluate(
        ConnectionState connectionState,
        bool isKillSwitchEnabled,
        IReadOnlyList<EngineStatus> engines)
    {
        if (connectionState is ConnectionState.Connecting or ConnectionState.Disconnecting)
        {
            return ConnectionRecoveryAction.None;
        }

        if (IsTransportOnlyFault(engines))
        {
            return ConnectionRecoveryAction.RestartTransport;
        }

        var paqet = Find(engines, EngineKind.Paqet);
        var xray = Find(engines, EngineKind.Xray);
        var proxiFyre = Find(engines, EngineKind.ProxiFyre);
        // The exit engine is listed only while the profile uses it.
        var exit = FindExit(engines);
        var allHealthy = paqet?.State == EngineState.Running &&
                         xray?.State == EngineState.Running &&
                         proxiFyre?.State == EngineState.Running &&
                         exit?.State is null or EngineState.Running;
        var guarded = isKillSwitchEnabled &&
                      paqet?.State == EngineState.Stopped &&
                      xray?.State == EngineState.Stopped &&
                      proxiFyre?.State == EngineState.Running &&
                      exit?.State is null or EngineState.Stopped;
        var anyEngineActive = engines.Any(engine =>
            engine.State is EngineState.Running or EngineState.Starting or EngineState.Faulted);

        return !allHealthy && !guarded && anyEngineActive
            ? ConnectionRecoveryAction.SafeRecovery
            : ConnectionRecoveryAction.None;
    }

    /// <summary>
    /// Whether the chain must be released to the configured safe state. A fault of the
    /// carrier alone is excluded: the broker restarts Paqet in place for it.
    /// </summary>
    public static bool RequiresSafeRecovery(
        ConnectionState connectionState,
        bool isKillSwitchEnabled,
        IReadOnlyList<EngineStatus> engines) =>
        Evaluate(connectionState, isKillSwitchEnabled, engines) == ConnectionRecoveryAction.SafeRecovery;

    /// <summary>
    /// Paqet, the exit that rides on it, or both are down while Xray and ProxiFyre keep
    /// routed traffic failing closed; restarting the transport restarts both.
    /// </summary>
    public static bool IsTransportOnlyFault(IReadOnlyList<EngineStatus> engines)
    {
        var paqet = Find(engines, EngineKind.Paqet)?.State;
        var exit = FindExit(engines)?.State;
        var exitSettled = exit is null or EngineState.Running or EngineState.Faulted or EngineState.Stopped;
        var transportDown = paqet is EngineState.Faulted or EngineState.Stopped
            ? exitSettled
            : paqet == EngineState.Running && exit is EngineState.Faulted or EngineState.Stopped;
        return transportDown &&
               Find(engines, EngineKind.Xray)?.State == EngineState.Running &&
               Find(engines, EngineKind.ProxiFyre)?.State == EngineState.Running;
    }

    private static EngineStatus? Find(IReadOnlyList<EngineStatus> engines, EngineKind kind) =>
        engines.FirstOrDefault(engine => engine.Engine == kind);

    private static EngineStatus? FindExit(IReadOnlyList<EngineStatus> engines) =>
        engines.FirstOrDefault(engine => engine.Engine.IsExit());
}
