using System.Net;
using System.Net.NetworkInformation;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Network;
using PaqetFire.Core.Routing;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class DesktopPresentationTests
{
    [Theory]
    [InlineData(ConnectionStage.Preparing, 1, "Preparing")]
    [InlineData(ConnectionStage.VerifyingPayload, 1, "Checking engine files")]
    [InlineData(ConnectionStage.StartingTransport, 1, "Starting Paqet transport")]
    [InlineData(ConnectionStage.StartingPolicy, 1, "Starting Xray policy")]
    [InlineData(ConnectionStage.StartingExit, 1, "Connecting the exit proxy")]
    [InlineData(ConnectionStage.StartingCapture, 1, "Starting application capture")]
    [InlineData(ConnectionStage.ProbingTunnel, 1, "Testing tunnel")]
    [InlineData(ConnectionStage.RecoveringTransport, 3, "Reconnecting transport (attempt 3)")]
    public void StagesHaveFriendlyLabels(ConnectionStage stage, int attempt, string expected) =>
        Assert.Equal(expected, ConnectionProgressText.GetStageLabel(stage, attempt));

    [Fact]
    public void EveryStageHasALabel()
    {
        foreach (var stage in Enum.GetValues<ConnectionStage>())
        {
            Assert.NotEqual("Working", ConnectionProgressText.GetStageLabel(stage));
        }
    }

    [Fact]
    public void StepperMarksEarlierStagesDoneAndLaterPending()
    {
        var now = DateTimeOffset.UtcNow;
        var steps = ConnectionProgressText.CreateSteps(new ConnectionProgress(ConnectionStage.StartingPolicy, now, now));
        Assert.Equal(
            [ProgressStepState.Done, ProgressStepState.Done, ProgressStepState.Done,
             ProgressStepState.Active, ProgressStepState.Pending, ProgressStepState.Pending],
            steps.Select(step => step.State));
        Assert.Equal("Starting Xray policy: in progress", steps[3].AccessibleName);
        Assert.Empty(ConnectionProgressText.CreateSteps(null));
    }

    [Fact]
    public void SummaryIncludesStepAndElapsedTime()
    {
        var started = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var progress = new ConnectionProgress(ConnectionStage.ProbingTunnel, started, started.AddSeconds(50));
        Assert.Equal("Step 6 of 6 · Testing tunnel · 1 min 05 s",
            ConnectionProgressText.CreateSummary(progress, started.AddSeconds(65)));
        Assert.Equal("Reconnecting transport (attempt 2) (9 s)", ConnectionProgressText.CreateTrayText(
            new ConnectionProgress(ConnectionStage.RecoveringTransport, started, started, 2), started.AddSeconds(9)));
        Assert.Equal("0 s", ConnectionProgressText.FormatElapsed(TimeSpan.FromSeconds(-4)));
    }

    [Fact]
    public void ChecklistWalksSoftwareThenProfileThenConnect()
    {
        var missing = Snapshot() with
        {
            IsConfigured = false,
            Prerequisites = [new PrerequisiteStatus("npcap", "Npcap", false, "Missing"), new PrerequisiteStatus("vcredist-x64", "VC++", true, "OK")],
        };
        var checklist = SetupChecklist.Create(missing, isConnected: false);
        Assert.Equal(SetupStep.RequiredSoftware, checklist.CurrentStep);
        Assert.Equal("npcap", Assert.Single(checklist.MissingPrerequisites).Id);
        Assert.True(checklist.ShouldShow);

        checklist = SetupChecklist.Create(missing with { Prerequisites = [new PrerequisiteStatus("npcap", "Npcap", true, "OK")] }, false);
        Assert.Equal(SetupStep.ServerProfile, checklist.CurrentStep);

        checklist = SetupChecklist.Create(missing with { IsConfigured = true, Prerequisites = [] }, false);
        Assert.Equal(SetupStep.Connect, checklist.CurrentStep);
        Assert.False(checklist.ShouldShow);

        Assert.True(SetupChecklist.Create(missing with { IsConfigured = true, Prerequisites = [] }, true).IsComplete);
    }

    [Fact]
    public void UnknownPrerequisitesAreNotReportedAsInstalled()
    {
        var checklist = SetupChecklist.Create(Snapshot() with { Prerequisites = null }, false);
        Assert.False(checklist.Items[0].IsDone);
        Assert.Equal(SetupStep.RequiredSoftware, checklist.CurrentStep);
    }

    [Fact]
    public void NotificationsCoverUnexpectedRouteTransitionsOnly()
    {
        var connected = Snapshot() with
        {
            IsRouting = true,
            ConnectionState = ConnectionState.Connected,
            Tunnel = new TunnelHealthReport(TunnelHealthState.Healthy, DateTimeOffset.UtcNow, 0, 0, null),
        };
        var dropped = Snapshot();
        var guarded = Snapshot() with { ConnectionState = ConnectionState.Guarded, IsKillSwitchEnabled = true };
        var recovering = connected with { Tunnel = connected.Tunnel! with { State = TunnelHealthState.Recovering } };

        Assert.Equal(RouteNotificationKind.RouteDropped, RouteNotificationPolicy.Classify(connected, dropped, false)?.Kind);
        Assert.Equal(RouteNotificationKind.KillSwitchBlocking, RouteNotificationPolicy.Classify(connected, guarded, false)?.Kind);
        Assert.Equal(RouteNotificationKind.Reconnecting, RouteNotificationPolicy.Classify(connected, recovering, false)?.Kind);
        Assert.Equal(RouteNotificationKind.Recovered, RouteNotificationPolicy.Classify(recovering, connected, false)?.Kind);
        Assert.Null(RouteNotificationPolicy.Classify(connected, dropped, userInitiated: true));
        Assert.Null(RouteNotificationPolicy.Classify(connected, connected, false));
        Assert.Null(RouteNotificationPolicy.Classify(null, dropped, false));
    }

    [Fact]
    public void ProgressRecoveryCountsAsReconnecting()
    {
        var connected = Snapshot() with { IsRouting = true, ConnectionState = ConnectionState.Connected };
        var now = DateTimeOffset.UtcNow;
        var recovering = connected with { Progress = new ConnectionProgress(ConnectionStage.RecoveringTransport, now, now) };
        Assert.Equal(RouteNotificationKind.Reconnecting, RouteNotificationPolicy.Classify(connected, recovering, false)?.Kind);
    }

    [Theory]
    [InlineData("vpn.example.com:9999", "vpn.example.com")]
    [InlineData(" 203.0.113.5:443 ", "203.0.113.5")]
    [InlineData("[2001:db8::1]:9999", "2001:db8::1")]
    public void ServerHostIsTakenFromTheEndpoint(string endpoint, string host)
    {
        Assert.True(ServerReachabilityText.TryGetHost(endpoint, out var parsed));
        Assert.Equal(host, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vpn.example.com")]
    [InlineData("vpn.example.com:0")]
    [InlineData("2001:db8::1:9999")]
    [InlineData("bad host:80")]
    public void InvalidEndpointsHaveNoHost(string endpoint) =>
        Assert.False(ServerReachabilityText.TryGetHost(endpoint, out _));

    [Fact]
    public async Task ServerPingAveragesRepliesAndSkipsNameLookupWhileConnected()
    {
        var resolved = 0;
        var samples = new Queue<long?>([40, null, 60]);
        var probe = new ServerReachabilityProbe(
            (_, _) =>
            {
                resolved++;
                return Task.FromResult(new[] { IPAddress.Parse("203.0.113.9") });
            },
            (_, _) => Task.FromResult(samples.Dequeue()));

        var skipped = await probe.CheckAsync("vpn.example.com:9999", allowNameResolution: false);
        Assert.Equal(ServerPingStatus.ResolutionSkipped, skipped.Status);
        Assert.Equal(0, resolved);

        var result = await probe.CheckAsync("vpn.example.com:9999", allowNameResolution: true);
        Assert.Equal(ServerPingStatus.Replied, result.Status);
        Assert.Equal(50, result.AverageMilliseconds);
        Assert.Equal(2, result.Replies);
        Assert.Contains("network latency", ServerReachabilityText.Format(result));
    }

    [Fact]
    public async Task NoIcmpReplyIsNotReportedAsAFailedServer()
    {
        var probe = new ServerReachabilityProbe(ping: (_, _) => Task.FromResult<long?>(null));
        var result = await probe.CheckAsync("203.0.113.9:9999", allowNameResolution: false);
        Assert.Equal(ServerPingStatus.NoReply, result.Status);
        Assert.Contains("may block ping", ServerReachabilityText.Format(result));
    }

    [Theory]
    [InlineData(true, true, XrayDnsResolver.Cloudflare, true, true)]
    [InlineData(false, true, XrayDnsResolver.Cloudflare, true, false)]
    [InlineData(true, false, XrayDnsResolver.Cloudflare, true, false)]
    [InlineData(true, false, XrayDnsResolver.System, true, true)]
    [InlineData(true, false, XrayDnsResolver.Quad9, false, true)]
    public void FakeDnsConflictsMatchBrokerValidation(bool routeAll, bool exclusions, XrayDnsResolver resolver, bool ipv4, bool conflict)
    {
        Assert.Equal(conflict, FakeDnsCompatibility.GetConflict(routeAll, exclusions, resolver, ipv4) is not null);
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "vpn.example.com:9999",
            TransportKey = "secret-key",
            FakeDns = true,
            DnsResolver = resolver,
            RouteIpv4 = ipv4,
            RoutingMode = routeAll ? RoutingMode.AllApplications : RoutingMode.SelectedApplications,
            SelectedApplications = routeAll ? [] : ["chrome.exe"],
            UserExclusions = exclusions ? ["steam.exe"] : [],
        };
        Assert.Equal(conflict, PaqetFireSettingsValidator.Validate(settings).Any(error => error.Contains("FakeDNS")));
    }

    [Fact]
    public void StartupCommandLaunchesInTheBackgroundAndMigratesOldValues()
    {
        const string path = @"C:\Program Files\PaqetFire\app\PaqetFire.Desktop.exe";
        Assert.Equal($"\"{path}\" --background", StartupCommand.Create(path));
        Assert.True(StartupCommand.NeedsUpdate($"\"{path}\"", path));
        Assert.False(StartupCommand.NeedsUpdate(StartupCommand.Create(path), path));
        Assert.True(StartupCommand.IsBackgroundLaunch([path, "--BACKGROUND"]));
        Assert.False(StartupCommand.IsBackgroundLaunch([path]));
    }

    [Fact]
    public void FormStateIgnoresCosmeticDifferences()
    {
        var applied = ConnectionFormState.FromSettings(PaqetFireSettingsView.FromSettings(new PaqetFireSettings
        {
            SelectedApplications = ["chrome.exe", "telegram.exe"],
            LocalTcpFlags = ["PA", "S"],
        }));
        var edited = applied with { SelectedApplications = "chrome.exe\r\r telegram.exe \rCHROME.EXE", LocalTcpFlags = "PA ,S" };
        Assert.True(edited.IsEquivalentTo(applied));
        Assert.False((applied with { BlockQuic = !applied.BlockQuic }).IsEquivalentTo(applied));
        Assert.False((applied with { Advanced = applied.Advanced with { KcpMtu = 1200 } }).IsEquivalentTo(applied));
    }

    [Fact]
    public void FormStateRoundTripsBrokerSettings()
    {
        var original = new PaqetFireSettings
        {
            ProfileName = "Office",
            ServerEndpoint = "vpn.example.com:9999",
            RoutingMode = RoutingMode.SelectedApplications,
            SelectedApplications = ["chrome.exe"],
            DirectRouteDestinations = ["example.com", "203.0.113.0/24"],
            FakeDns = true,
            BlockQuic = true,
            KcpMode = "fast2",
            LocalTcpFlags = ["PA"],
            RemoteTcpFlags = ["PA"],
            HotspotSocksPort = 10900,
            ExitProxy = ExitProxyKind.Psiphon,
            Psiphon = new PsiphonExitOptions("SE"),
        };
        var settings = ConnectionFormState.FromSettings(PaqetFireSettingsView.FromSettings(original))
            .ToSettings("key", "sharing-password");
        Assert.Equal(original with { TransportKey = "key", LanSocksPassword = "sharing-password" },
            settings with
            {
                SelectedApplications = original.SelectedApplications,
                UserExclusions = original.UserExclusions,
                DirectRouteDestinations = original.DirectRouteDestinations,
                LocalTcpFlags = original.LocalTcpFlags,
                RemoteTcpFlags = original.RemoteTcpFlags,
            });
        Assert.Equal(original.DirectRouteDestinations, settings.DirectRouteDestinations);
        Assert.Equal(original.LocalTcpFlags, settings.LocalTcpFlags);
    }

    [Fact]
    public void DesktopDefaultsMatchCoreTransportAndPolicyDefaults()
    {
        var preferences = new DesktopPreferences();
        var core = new PaqetFireSettings();
        Assert.Equal(string.Join(", ", core.LocalTcpFlags), preferences.LocalTcpFlags);
        Assert.Equal(string.Join(", ", core.RemoteTcpFlags), preferences.RemoteTcpFlags);
        Assert.True(preferences.FakeDns);
        Assert.True(preferences.BlockQuic);
    }

    [Fact]
    public void ResolvedRouterMacIsReusedUntilAnExplicitRefresh()
    {
        var adapter = new NetworkAdapterDetails(
            Guid.Parse("33333333-3333-3333-3333-333333333333"), "Wi-Fi",
            IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.1"), NetworkInterfaceType.Wireless80211);
        var lookups = 0;
        var cache = new GatewayMacCache();
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(() => [adapter], (_, _) =>
        {
            lookups++;
            return "02:11:22:33:44:55";
        }), cache);

        preview.Refresh(null);
        var state = preview.Refresh(null);
        Assert.Equal(1, lookups);
        Assert.Equal("02:11:22:33:44:55", state.Detected!.RouterMac);
        Assert.True(cache.TryGet(GatewayMacCache.CreateKey(adapter.InterfaceGuid, adapter.Gateway, adapter.LocalAddress), out _));

        preview.Refresh(null, refreshRouterMac: true);
        Assert.Equal(2, lookups);
    }

    [Fact]
    public void UnresolvedRouterMacIsNotCached()
    {
        var adapter = new NetworkAdapterDetails(
            Guid.Parse("44444444-4444-4444-4444-444444444444"), "Ethernet",
            IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.1"), NetworkInterfaceType.Ethernet);
        var lookups = 0;
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(() => [adapter], (_, _) =>
        {
            lookups++;
            return null;
        }));
        preview.Refresh(null);
        preview.Refresh(null);
        Assert.Equal(2, lookups);
    }

    private static BrokerSnapshot Snapshot() => new([], false, false, DateTimeOffset.UtcNow, IsConfigured: true,
        ConnectionState: ConnectionState.Disconnected);
}
