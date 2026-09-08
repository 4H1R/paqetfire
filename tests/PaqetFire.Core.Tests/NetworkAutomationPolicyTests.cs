using PaqetFire.Core.Connections;
using PaqetFire.Core.Connections.Automation;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class NetworkAutomationPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid DefaultProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HomeProfileId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void UnknownNetwork_AutoConnectsWithDefaultProfile()
    {
        var policy = CreatePolicy();

        var decision = policy.Evaluate(Observation(
            Start,
            settings: EnabledSettings(DefaultProfileId),
            network: InternetNetwork("unknown-wifi")));

        Assert.Equal(NetworkAutomationAction.Connect, decision.Action);
        Assert.Equal(NetworkAutomationReason.AutoConnectUntrustedNetwork, decision.Reason);
        Assert.Equal(DefaultProfileId, decision.ProfileId);
        Assert.Equal(1, decision.AttemptNumber);
    }

    [Fact]
    public void MissingPrerequisites_DoNotStartAnImpossibleConnection()
    {
        var policy = CreatePolicy();

        var decision = policy.Evaluate(Observation(
            Start,
            state: ConnectionState.NotReady,
            settings: EnabledSettings(),
            network: InternetNetwork("unknown-wifi"),
            engines:
            [
                Status(EngineKind.Paqet, EngineState.NotInstalled),
                Status(EngineKind.Xray, EngineState.Stopped),
                Status(EngineKind.ProxiFyre, EngineState.Stopped),
            ]));

        Assert.Equal(NetworkAutomationAction.None, decision.Action);
        Assert.Equal(NetworkAutomationReason.ConnectionNotReady, decision.Reason);
    }

    [Fact]
    public void TrustedNetwork_StaysDisconnectedUnlessExplicitlyEnabled()
    {
        var settings = EnabledSettings(DefaultProfileId) with
        {
            NetworkRules = [new NetworkAutomationRule("home", IsTrusted: true, HomeProfileId)],
        };

        var blocked = CreatePolicy().Evaluate(Observation(
            Start,
            settings: settings,
            network: InternetNetwork("home", NetworkKind.Ethernet)));
        var allowed = CreatePolicy().Evaluate(Observation(
            Start,
            settings: settings with { ConnectOnTrustedNetworks = true },
            network: InternetNetwork("home", NetworkKind.Ethernet)));

        Assert.Equal(NetworkAutomationAction.None, blocked.Action);
        Assert.Equal(NetworkAutomationReason.TrustedNetwork, blocked.Reason);
        Assert.Equal(NetworkAutomationAction.Connect, allowed.Action);
        Assert.Equal(NetworkAutomationReason.AutoConnectTrustedNetwork, allowed.Reason);
        Assert.Equal(HomeProfileId, allowed.ProfileId);
    }

    [Theory]
    [InlineData(NetworkAutomationTrigger.NetworkChanged, NetworkAutomationReason.NetworkChanged,
        NetworkAutomationReason.RestoreAfterNetworkChange)]
    [InlineData(NetworkAutomationTrigger.Resumed, NetworkAutomationReason.ComputerResumed,
        NetworkAutomationReason.RestoreAfterResume)]
    public void ActiveRoute_IsRestartedAfterNetworkOrResumeSettleDelay(
        NetworkAutomationTrigger trigger,
        NetworkAutomationReason disconnectReason,
        NetworkAutomationReason reconnectReason)
    {
        var policy = CreatePolicy(networkDelay: TimeSpan.FromSeconds(5), resumeDelay: TimeSpan.FromSeconds(5));
        policy.Evaluate(Observation(
            Start,
            state: ConnectionState.Connected,
            settings: DisabledSettings(),
            network: InternetNetwork("network-a"),
            engines: RunningEngines()));

        var disconnect = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            trigger,
            ConnectionState.Connected,
            DisabledSettings(),
            InternetNetwork("network-a"),
            engines: RunningEngines()));
        var settling = policy.Evaluate(Observation(
            Start.AddSeconds(2),
            settings: DisabledSettings(),
            network: InternetNetwork("network-a")));
        var reconnect = policy.Evaluate(Observation(
            Start.AddSeconds(6),
            settings: DisabledSettings(),
            network: InternetNetwork("network-a")));

        Assert.Equal(NetworkAutomationAction.Disconnect, disconnect.Action);
        Assert.Equal(disconnectReason, disconnect.Reason);
        Assert.Equal(NetworkAutomationAction.None, settling.Action);
        Assert.Equal(Start.AddSeconds(6), settling.RetryAt);
        Assert.Equal(NetworkAutomationAction.Connect, reconnect.Action);
        Assert.Equal(reconnectReason, reconnect.Reason);
    }

    [Fact]
    public void CaptivePortal_DisconnectsThenWaitsForClearAndGracePeriod()
    {
        var policy = CreatePolicy(captivePortalGrace: TimeSpan.FromSeconds(4));
        policy.Evaluate(Observation(
            Start,
            state: ConnectionState.Connected,
            settings: DisabledSettings(),
            network: InternetNetwork("hotel"),
            engines: RunningEngines()));

        var disconnect = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            state: ConnectionState.Connected,
            settings: DisabledSettings(),
            network: new NetworkContext("hotel", NetworkKind.WiFi, NetworkConnectivity.CaptivePortal),
            engines: RunningEngines()));
        var captive = policy.Evaluate(Observation(
            Start.AddSeconds(2),
            settings: DisabledSettings(),
            network: new NetworkContext("hotel", NetworkKind.WiFi, NetworkConnectivity.CaptivePortal)));
        var grace = policy.Evaluate(Observation(
            Start.AddSeconds(3),
            settings: DisabledSettings(),
            network: InternetNetwork("hotel")));
        var reconnect = policy.Evaluate(Observation(
            Start.AddSeconds(7),
            settings: DisabledSettings(),
            network: InternetNetwork("hotel")));

        Assert.Equal(NetworkAutomationAction.Disconnect, disconnect.Action);
        Assert.Equal(NetworkAutomationReason.CaptivePortalDetected, disconnect.Reason);
        Assert.Equal(NetworkAutomationReason.CaptivePortal, captive.Reason);
        Assert.Equal(NetworkAutomationReason.CaptivePortalGrace, grace.Reason);
        Assert.Equal(Start.AddSeconds(7), grace.RetryAt);
        Assert.Equal(NetworkAutomationAction.Connect, reconnect.Action);
        Assert.Equal(NetworkAutomationReason.RestoreAfterCaptivePortal, reconnect.Reason);
    }

    [Fact]
    public void CaptivePortal_DoesNotWeakenAnActiveKillSwitch()
    {
        var policy = CreatePolicy();
        policy.Evaluate(Observation(
            Start,
            state: ConnectionState.Connected,
            network: InternetNetwork("hotel"),
            engines: RunningEngines()));

        var decision = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            state: ConnectionState.Connected,
            network: new NetworkContext("hotel", NetworkKind.WiFi, NetworkConnectivity.CaptivePortal),
            isKillSwitchEnabled: true,
            engines: RunningEngines()));

        Assert.Equal(NetworkAutomationAction.None, decision.Action);
        Assert.Equal(NetworkAutomationReason.CaptivePortal, decision.Reason);
    }

    [Fact]
    public void ManualPause_SuppressesAutoConnectAndCanBeEndedEarly()
    {
        var policy = CreatePolicy();
        policy.PauseAutoConnect(Start, TimeSpan.FromMinutes(10));

        var paused = policy.Evaluate(Observation(
            Start,
            settings: EnabledSettings(),
            network: InternetNetwork("cafe")));
        policy.ResumeAutoConnect();
        var resumed = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            settings: EnabledSettings(),
            network: InternetNetwork("cafe")));

        Assert.Equal(NetworkAutomationReason.ManualPause, paused.Reason);
        Assert.Equal(Start.AddMinutes(10), paused.RetryAt);
        Assert.Equal(NetworkAutomationAction.Connect, resumed.Action);
    }

    [Fact]
    public void HotspotHosting_BlocksAutoConnectUntilHotspotShareIsEnabled()
    {
        var policy = CreatePolicy();
        var hotspotNetwork = InternetNetwork("upstream") with { IsHostingHotspot = true };

        var blocked = policy.Evaluate(Observation(
            Start,
            settings: EnabledSettings(),
            network: hotspotNetwork));
        var allowed = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            settings: EnabledSettings(),
            network: hotspotNetwork,
            isHotspotShareEnabled: true));

        Assert.Equal(NetworkAutomationReason.HotspotHostingInterlock, blocked.Reason);
        Assert.Equal(NetworkAutomationAction.Connect, allowed.Action);
    }

    [Fact]
    public void TemporaryNetworkBlocker_CancelsPendingRequestSoItCanBeRetried()
    {
        var policy = CreatePolicy(captivePortalGrace: TimeSpan.FromSeconds(1));
        var initial = policy.Evaluate(Observation(
            Start,
            settings: EnabledSettings(),
            network: InternetNetwork("train")));
        var blocked = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            settings: EnabledSettings(),
            network: new NetworkContext("train", NetworkKind.WiFi, NetworkConnectivity.CaptivePortal)));
        var grace = policy.Evaluate(Observation(
            Start.AddSeconds(2),
            settings: EnabledSettings(),
            network: InternetNetwork("train")));
        var retried = policy.Evaluate(Observation(
            Start.AddSeconds(3),
            settings: EnabledSettings(),
            network: InternetNetwork("train")));

        Assert.Equal(NetworkAutomationAction.Connect, initial.Action);
        Assert.Equal(NetworkAutomationReason.CaptivePortal, blocked.Reason);
        Assert.Equal(NetworkAutomationReason.CaptivePortalGrace, grace.Reason);
        Assert.Equal(NetworkAutomationAction.Connect, retried.Action);
        Assert.Equal(1, retried.AttemptNumber);
    }

    [Fact]
    public void FailedAttempts_UseCappedBackoffAndStopAtConfiguredLimit()
    {
        var policy = CreatePolicy(
            initialBackoff: TimeSpan.FromSeconds(2),
            maximumBackoff: TimeSpan.FromSeconds(3),
            maximumAttempts: 3);

        var first = policy.Evaluate(Observation(
            Start,
            settings: EnabledSettings(),
            network: InternetNetwork("airport")));
        var firstBackoff = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            NetworkAutomationTrigger.ConnectionAttemptFailed,
            settings: EnabledSettings(),
            network: InternetNetwork("airport")));
        var second = policy.Evaluate(Observation(
            Start.AddSeconds(3),
            settings: EnabledSettings(),
            network: InternetNetwork("airport")));
        var secondBackoff = policy.Evaluate(Observation(
            Start.AddSeconds(4),
            NetworkAutomationTrigger.ConnectionAttemptFailed,
            settings: EnabledSettings(),
            network: InternetNetwork("airport")));
        var third = policy.Evaluate(Observation(
            Start.AddSeconds(7),
            settings: EnabledSettings(),
            network: InternetNetwork("airport")));
        var exhausted = policy.Evaluate(Observation(
            Start.AddSeconds(8),
            NetworkAutomationTrigger.ConnectionAttemptFailed,
            settings: EnabledSettings(),
            network: InternetNetwork("airport")));

        Assert.Equal(1, first.AttemptNumber);
        Assert.Equal(NetworkAutomationReason.RetryBackoff, firstBackoff.Reason);
        Assert.Equal(Start.AddSeconds(3), firstBackoff.RetryAt);
        Assert.Equal(2, second.AttemptNumber);
        Assert.Equal(NetworkAutomationReason.RetryBackoff, secondBackoff.Reason);
        Assert.Equal(Start.AddSeconds(7), secondBackoff.RetryAt);
        Assert.Equal(3, third.AttemptNumber);
        Assert.Equal(NetworkAutomationAction.None, exhausted.Action);
        Assert.Equal(NetworkAutomationReason.RetryLimitReached, exhausted.Reason);
    }

    [Fact]
    public void BrokenEngineChain_UsesExistingSafeRecoveryPolicyBeforeReconnect()
    {
        var policy = CreatePolicy();
        EngineStatus[] brokenEngines =
        [
            Status(EngineKind.Paqet, EngineState.Running),
            Status(EngineKind.Xray, EngineState.Faulted),
            Status(EngineKind.ProxiFyre, EngineState.Stopped),
        ];

        var recover = policy.Evaluate(Observation(
            Start,
            state: ConnectionState.Faulted,
            settings: DisabledSettings(),
            network: InternetNetwork("office"),
            engines: brokenEngines));
        var reconnect = policy.Evaluate(Observation(
            Start.AddSeconds(1),
            settings: DisabledSettings(),
            network: InternetNetwork("office")));

        Assert.Equal(NetworkAutomationAction.Disconnect, recover.Action);
        Assert.Equal(NetworkAutomationReason.SafeRecoveryRequired, recover.Reason);
        Assert.Equal(NetworkAutomationAction.Connect, reconnect.Action);
        Assert.Equal(NetworkAutomationReason.RestoreAfterFailure, reconnect.Reason);
    }

    [Fact]
    public void NetworkIdentityChange_IsInferredEvenWithoutExplicitPlatformTrigger()
    {
        var policy = CreatePolicy(networkDelay: TimeSpan.FromSeconds(2));
        policy.Evaluate(Observation(
            Start,
            settings: EnabledSettings(),
            network: InternetNetwork("wifi-a")));
        policy.Evaluate(Observation(
            Start.AddSeconds(1),
            NetworkAutomationTrigger.ConnectionAttemptFailed,
            settings: EnabledSettings(),
            network: InternetNetwork("wifi-a")));

        var changed = policy.Evaluate(Observation(
            Start.AddSeconds(2),
            settings: EnabledSettings(),
            network: InternetNetwork("wifi-b")));
        var newNetworkAttempt = policy.Evaluate(Observation(
            Start.AddSeconds(4),
            settings: EnabledSettings(),
            network: InternetNetwork("wifi-b")));

        Assert.Equal(NetworkAutomationReason.NetworkSettleDelay, changed.Reason);
        Assert.Equal(Start.AddSeconds(4), changed.RetryAt);
        Assert.Equal(NetworkAutomationAction.Connect, newNetworkAttempt.Action);
        Assert.Equal(1, newNetworkAttempt.AttemptNumber);
    }

    [Fact]
    public void ObservationsMustBeChronological()
    {
        var policy = CreatePolicy();
        policy.Evaluate(Observation(Start, settings: DisabledSettings()));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(Observation(
            Start.AddSeconds(-1),
            settings: DisabledSettings())));
    }

    private static NetworkAutomationPolicy CreatePolicy(
        TimeSpan? networkDelay = null,
        TimeSpan? resumeDelay = null,
        TimeSpan? captivePortalGrace = null,
        TimeSpan? initialBackoff = null,
        TimeSpan? maximumBackoff = null,
        int maximumAttempts = 5) =>
        new(new NetworkAutomationPolicyOptions
        {
            NetworkSettleDelay = networkDelay ?? TimeSpan.Zero,
            ResumeSettleDelay = resumeDelay ?? TimeSpan.Zero,
            CaptivePortalGrace = captivePortalGrace ?? TimeSpan.Zero,
            InitialReconnectBackoff = initialBackoff ?? TimeSpan.FromSeconds(1),
            MaximumReconnectBackoff = maximumBackoff ?? TimeSpan.FromSeconds(30),
            MaximumReconnectAttempts = maximumAttempts,
        });

    private static NetworkAutomationObservation Observation(
        DateTimeOffset timestamp,
        NetworkAutomationTrigger trigger = NetworkAutomationTrigger.StatusPoll,
        ConnectionState state = ConnectionState.Disconnected,
        NetworkAutomationSettings? settings = null,
        NetworkContext? network = null,
        bool isHotspotShareEnabled = false,
        bool isKillSwitchEnabled = false,
        IReadOnlyList<EngineStatus>? engines = null) =>
        new(
            timestamp,
            trigger,
            state,
            IsKillSwitchEnabled: isKillSwitchEnabled,
            engines ?? StoppedEngines(),
            network,
            isHotspotShareEnabled,
            settings ?? DisabledSettings());

    private static NetworkAutomationSettings EnabledSettings(Guid? profileId = null) =>
        new()
        {
            IsEnabled = true,
            DefaultProfileId = profileId,
        };

    private static NetworkAutomationSettings DisabledSettings() => new();

    private static NetworkContext InternetNetwork(
        string id,
        NetworkKind kind = NetworkKind.WiFi) =>
        new(id, kind, NetworkConnectivity.Internet);

    private static EngineStatus[] RunningEngines() =>
    [
        Status(EngineKind.Paqet, EngineState.Running),
        Status(EngineKind.Xray, EngineState.Running),
        Status(EngineKind.ProxiFyre, EngineState.Running),
    ];

    private static EngineStatus[] StoppedEngines() =>
    [
        Status(EngineKind.Paqet, EngineState.Stopped),
        Status(EngineKind.Xray, EngineState.Stopped),
        Status(EngineKind.ProxiFyre, EngineState.Stopped),
    ];

    private static EngineStatus Status(EngineKind kind, EngineState state) =>
        new(kind, state, "test");
}
