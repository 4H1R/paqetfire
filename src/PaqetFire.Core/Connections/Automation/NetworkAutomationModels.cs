using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections.Automation;

public enum NetworkKind
{
    Unknown,
    Ethernet,
    WiFi,
}

public enum NetworkConnectivity
{
    Offline,
    LocalOnly,
    CaptivePortal,
    Internet,
}

/// <summary>
/// A normalized view of the active upstream network. NetworkId must be a stable,
/// non-secret identifier supplied by the platform adapter (for example, a hash
/// derived from the adapter and gateway identity).
/// </summary>
public sealed record NetworkContext(
    string NetworkId,
    NetworkKind Kind,
    NetworkConnectivity Connectivity,
    bool IsHostingHotspot = false);

public sealed record NetworkAutomationRule(
    string NetworkId,
    bool IsTrusted,
    Guid? ProfileId = null);

public sealed record NetworkAutomationSettings
{
    public bool IsEnabled { get; init; }

    public bool ConnectOnTrustedNetworks { get; init; }

    public Guid? DefaultProfileId { get; init; }

    public IReadOnlyList<NetworkAutomationRule> NetworkRules { get; init; } = [];
}

public enum NetworkAutomationTrigger
{
    StatusPoll,
    Startup,
    NetworkChanged,
    Resumed,
    ConnectionAttemptFailed,
}

public sealed record NetworkAutomationObservation(
    DateTimeOffset Timestamp,
    NetworkAutomationTrigger Trigger,
    ConnectionState ConnectionState,
    bool IsKillSwitchEnabled,
    IReadOnlyList<EngineStatus> Engines,
    NetworkContext? Network,
    bool IsHotspotShareEnabled,
    NetworkAutomationSettings Settings);

public enum NetworkAutomationAction
{
    None,
    Connect,
    Disconnect,
}

public enum NetworkAutomationReason
{
    NoAction,
    AlreadyConnected,
    ConnectionNotReady,
    ConnectionTransitionInProgress,
    ConnectAlreadyRequested,
    AutomationDisabled,
    TrustedNetwork,
    NetworkUnavailable,
    CaptivePortal,
    CaptivePortalGrace,
    ManualPause,
    HotspotHostingInterlock,
    NetworkSettleDelay,
    ResumeSettleDelay,
    RetryBackoff,
    RetryLimitReached,
    SafeRecoveryRequired,
    NetworkChanged,
    ComputerResumed,
    CaptivePortalDetected,
    AutoConnectUntrustedNetwork,
    AutoConnectTrustedNetwork,
    RestoreAfterNetworkChange,
    RestoreAfterResume,
    RestoreAfterCaptivePortal,
    RestoreAfterFailure,
}

public sealed record NetworkAutomationDecision(
    NetworkAutomationAction Action,
    NetworkAutomationReason Reason,
    Guid? ProfileId = null,
    DateTimeOffset? RetryAt = null,
    int AttemptNumber = 0)
{
    public bool HasAction => Action != NetworkAutomationAction.None;
}

public sealed record NetworkAutomationPolicyOptions
{
    public TimeSpan NetworkSettleDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ResumeSettleDelay { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan CaptivePortalGrace { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan InitialReconnectBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumReconnectBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum total automated connect attempts for one network episode,
    /// including the first attempt.
    /// </summary>
    public int MaximumReconnectAttempts { get; init; } = 5;
}
