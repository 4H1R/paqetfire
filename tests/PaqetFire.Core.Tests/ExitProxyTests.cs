using System.Text.Json;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class ExitProxyTests
{
    private static readonly EngineSocksCredentials Credentials = new("engine-user", "p@ss:w/rd");

    [Fact]
    public void PsiphonConfigurationDialsOnlyThroughAuthenticatedPaqet()
    {
        var json = new PsiphonJsonConfigurationWriter().Write(new PsiphonClientProfile(1084, 1080, Credentials, "DE"));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("socks5://engine-user:p%40ss%3Aw%2Frd@127.0.0.1:1080", root.GetProperty("UpstreamProxyURL").GetString());
        Assert.Equal("DE", root.GetProperty("EgressRegion").GetString());
        Assert.Equal(1084, root.GetProperty("LocalSocksProxyPort").GetInt32());
        Assert.True(root.GetProperty("DisableLocalHTTPProxy").GetBoolean());
        Assert.False(root.GetProperty("EnableUpgradeDownload").GetBoolean());
        Assert.False(root.TryGetProperty("ListenInterface", out _));
        Assert.Equal("FFFFFFFFFFFFFFFF", root.GetProperty("PropagationChannelId").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("RemoteServerListSignaturePublicKey").GetString()));
    }

    [Fact]
    public void BestAvailablePsiphonServerOmitsTheEgressRegion()
    {
        var json = new PsiphonJsonConfigurationWriter().Write(new PsiphonClientProfile(1084, 1080, Credentials, string.Empty));
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("EgressRegion", out _));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("DEU")]
    [InlineData("D1")]
    public void PsiphonConfigurationRejectsMalformedCountries(string region) =>
        Assert.Throws<ConfigurationValidationException>(() =>
            new PsiphonJsonConfigurationWriter().Write(new PsiphonClientProfile(1084, 1080, Credentials, region)));

    [Fact]
    public void XrayWithAnExitSendsTcpThroughItAndKeepsUdpOnPaqet()
    {
        var json = new XrayJsonConfigurationWriter().Write(Policy(new SocksExitOutbound(1084)));
        using var document = JsonDocument.Parse(json);
        var outbounds = document.RootElement.GetProperty("outbounds").EnumerateArray().ToArray();
        var exit = Assert.Single(outbounds, outbound => outbound.GetProperty("tag").GetString() == XrayJsonConfigurationWriter.ExitOutboundTag);
        var server = exit.GetProperty("settings").GetProperty("servers")[0];
        Assert.Equal("127.0.0.1", server.GetProperty("address").GetString());
        Assert.Equal(1084, server.GetProperty("port").GetInt32());

        var rules = document.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        var last = rules[^2..];
        Assert.Equal(("tcp", "exit"), (last[0].GetProperty("network").GetString(), last[0].GetProperty("outboundTag").GetString()));
        Assert.Equal(("udp", "paqet"), (last[1].GetProperty("network").GetString(), last[1].GetProperty("outboundTag").GetString()));
        // Direct and DNS rules still come first.
        Assert.Contains(rules, rule => rule.GetProperty("outboundTag").GetString() == "direct");
    }

    [Fact]
    public void XrayWithoutAnExitIsUnchanged()
    {
        var json = new XrayJsonConfigurationWriter().Write(Policy(exit: null));
        using var document = JsonDocument.Parse(json);

        Assert.DoesNotContain(document.RootElement.GetProperty("outbounds").EnumerateArray(),
            outbound => outbound.GetProperty("tag").GetString() == XrayJsonConfigurationWriter.ExitOutboundTag);
        var last = document.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().Last();
        Assert.Equal("tcp,udp", last.GetProperty("network").GetString());
        Assert.Equal("paqet", last.GetProperty("outboundTag").GetString());
        Assert.False(last.TryGetProperty("port", out _));
    }

    [Theory]
    [InlineData(1080)]
    [InlineData(1081)]
    [InlineData(10085)]
    [InlineData(80)]
    public void XrayRejectsAnExitOnAReservedPort(int port) =>
        Assert.Throws<ConfigurationValidationException>(() =>
            new XrayJsonConfigurationWriter().Write(Policy(new SocksExitOutbound(port))));

    [Fact]
    public void ValidatorAcceptsPsiphonWithACountryOrBestAvailable()
    {
        Assert.Empty(PaqetFireSettingsValidator.Validate(ValidSettings() with
        {
            ExitProxy = ExitProxyKind.Psiphon,
            Psiphon = new PsiphonExitOptions("NL"),
        }));
        Assert.Empty(PaqetFireSettingsValidator.Validate(ValidSettings() with { ExitProxy = ExitProxyKind.Psiphon }));
    }

    [Fact]
    public void ValidatorRejectsInvalidExitSettings()
    {
        Assert.Contains("The Psiphon exit country is invalid.", PaqetFireSettingsValidator.Validate(ValidSettings() with
        {
            ExitProxy = ExitProxyKind.Psiphon,
            Psiphon = new PsiphonExitOptions("Germany"),
        }));
        Assert.Contains("The exit proxy is invalid.",
            PaqetFireSettingsValidator.Validate(ValidSettings() with { ExitProxy = (ExitProxyKind)42 }));
        Assert.Contains(PaqetFireSettingsValidator.Validate(ValidSettings() with
            {
                ExitProxy = ExitProxyKind.Psiphon,
                RouteTcp = false,
            }),
            error => error.Contains("TCP only", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnusedExitIgnoresItsOptions() =>
        Assert.Empty(PaqetFireSettingsValidator.Validate(ValidSettings() with { Psiphon = new PsiphonExitOptions("bad") }));

    [Fact]
    public void SharesCannotTakeThePsiphonPort() =>
        Assert.Contains(PaqetFireSettingsValidator.Validate(ValidSettings() with
            {
                ShareWithLan = true,
                LanSocksPort = PsiphonJsonConfigurationWriter.LocalSocksPort,
                LanSocksPassword = "password123",
            }),
            error => error.Contains("shared SOCKS5 port", StringComparison.Ordinal));

    [Fact]
    public async Task ConnectStartsTheExitAfterPaqetAndBeforeCapture()
    {
        var events = new List<string>();
        var paqet = new Engine(EngineKind.Paqet) { Events = events };
        var xray = new Engine(EngineKind.Xray) { Events = events };
        var proxiFyre = new Engine(EngineKind.ProxiFyre) { Events = events };
        var exit = new Engine(EngineKind.Psiphon) { Events = events, IsEnabled = true };
        using var controller = new ConnectionController(paqet, xray, proxiFyre, exit);
        var stages = new List<ConnectionStage>();

        await controller.ConnectAsync(stages.Add, CancellationToken.None);

        Assert.True(events.IndexOf("started Paqet") < events.IndexOf("start Psiphon"));
        Assert.True(events.IndexOf("started Psiphon") < events.IndexOf("start ProxiFyre"));
        Assert.Equal(
            [ConnectionStage.StartingTransport, ConnectionStage.StartingPolicy, ConnectionStage.StartingExit, ConnectionStage.StartingCapture],
            stages);
        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(EngineState.Running, status.Exit?.State);
        Assert.Equal([EngineKind.Paqet, EngineKind.Xray, EngineKind.Psiphon, EngineKind.ProxiFyre], status.Engines.Select(engine => engine.Engine));
    }

    [Fact]
    public async Task ADisabledExitIsNeitherStartedNorReported()
    {
        var exit = new Engine(EngineKind.Psiphon);
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), exit);

        await controller.ConnectAsync(CancellationToken.None);

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(0, exit.StartCount);
        Assert.Null(status.Exit);
        Assert.Equal(3, status.Engines.Count);
    }

    [Fact]
    public async Task AnExitThatCannotConnectFailsTheConnectWithoutCapture()
    {
        var paqet = new Engine(EngineKind.Paqet);
        var xray = new Engine(EngineKind.Xray);
        var proxiFyre = new Engine(EngineKind.ProxiFyre);
        var exit = new Engine(EngineKind.Psiphon) { IsEnabled = true, FailOnStart = true };
        using var controller = new ConnectionController(paqet, xray, proxiFyre, exit);

        var error = await Assert.ThrowsAsync<ConnectionTransitionException>(async () =>
            await controller.ConnectAsync(CancellationToken.None));

        Assert.Contains("Psiphon", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, proxiFyre.StartCount);
        Assert.Equal(EngineState.Stopped, paqet.State);
        Assert.Equal(EngineState.Stopped, xray.State);
        Assert.Equal(EngineState.Stopped, exit.State);
    }

    [Fact]
    public async Task DisconnectAndGuardStopTheExitBeforeTheCarriers()
    {
        var stops = new List<EngineKind>();
        var exit = new Engine(EngineKind.Psiphon, stops) { IsEnabled = true };
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet, stops), new Engine(EngineKind.Xray, stops), new Engine(EngineKind.ProxiFyre, stops), exit);

        await controller.ConnectAsync(CancellationToken.None);
        await controller.DisconnectAsync(CancellationToken.None);
        Assert.Equal([EngineKind.ProxiFyre, EngineKind.Psiphon, EngineKind.Xray, EngineKind.Paqet], stops);

        stops.Clear();
        await controller.ConnectAsync(CancellationToken.None);
        await controller.GuardAsync(CancellationToken.None);
        Assert.Equal([EngineKind.Psiphon, EngineKind.Xray, EngineKind.Paqet], stops);
        Assert.Equal(ConnectionState.Guarded, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task RestartingTheTransportRestartsTheExitOnTopOfPaqet()
    {
        var events = new List<string>();
        var exit = new Engine(EngineKind.Psiphon) { Events = events, IsEnabled = true };
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet) { Events = events }, new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), exit);
        await controller.ConnectAsync(CancellationToken.None);
        events.Clear();

        await controller.RestartTransportAsync(CancellationToken.None);

        Assert.Equal(["stop Psiphon", "stop Paqet", "start Paqet", "started Paqet", "start Psiphon", "started Psiphon"], events);
        Assert.Equal(ConnectionState.Connected, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AFaultedExitMakesTheRouteFaulted()
    {
        var exit = new Engine(EngineKind.Psiphon) { IsEnabled = true };
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), exit);
        await controller.ConnectAsync(CancellationToken.None);

        exit.Fault();

        Assert.Equal(ConnectionState.Faulted, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public void AnExitFaultAloneIsRepairedByRestartingTheTransport()
    {
        EngineStatus[] engines =
        [
            new(EngineKind.Paqet, EngineState.Running),
            new(EngineKind.Xray, EngineState.Running),
            new(EngineKind.Psiphon, EngineState.Faulted),
            new(EngineKind.ProxiFyre, EngineState.Running),
        ];

        Assert.True(ConnectionRecoveryPolicy.IsTransportOnlyFault(engines));
        Assert.Equal(ConnectionRecoveryAction.RestartTransport,
            ConnectionRecoveryPolicy.Evaluate(ConnectionState.Faulted, false, engines));
    }

    [Fact]
    public void AGuardedRouteWithAStoppedExitNeedsNoRecovery()
    {
        EngineStatus[] engines =
        [
            new(EngineKind.Paqet, EngineState.Stopped),
            new(EngineKind.Xray, EngineState.Stopped),
            new(EngineKind.Psiphon, EngineState.Stopped),
            new(EngineKind.ProxiFyre, EngineState.Running),
        ];

        Assert.Equal(ConnectionRecoveryAction.None, ConnectionRecoveryPolicy.Evaluate(ConnectionState.Guarded, true, engines));
    }

    [Fact]
    public void ARunningExitWithAStoppedPolicyIsReleased()
    {
        EngineStatus[] engines =
        [
            new(EngineKind.Paqet, EngineState.Running),
            new(EngineKind.Xray, EngineState.Faulted),
            new(EngineKind.Psiphon, EngineState.Running),
            new(EngineKind.ProxiFyre, EngineState.Running),
        ];

        Assert.Equal(ConnectionRecoveryAction.SafeRecovery, ConnectionRecoveryPolicy.Evaluate(ConnectionState.Faulted, false, engines));
    }

    [Fact]
    public async Task OnlyTheSelectedExitStartsWhenSeveralAreRegistered()
    {
        var psiphon = new Engine(EngineKind.Psiphon);
        var openVpn = new Engine(EngineKind.OpenVpn) { IsEnabled = true };
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), psiphon, openVpn);

        await controller.ConnectAsync(CancellationToken.None);

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(0, psiphon.StartCount);
        Assert.Equal(1, openVpn.StartCount);
        Assert.Equal(EngineKind.OpenVpn, status.Exit?.Engine);
    }

    [Fact]
    public async Task AnExitThatIsNotInstalledDoesNotBlockOtherRoutes()
    {
        // Psiphon is downloaded only on request; a route without it must connect and disconnect.
        var psiphon = new Engine(EngineKind.Psiphon) { Installed = false };
        var openVpn = new Engine(EngineKind.OpenVpn) { Installed = false };
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), psiphon, openVpn);

        await controller.ConnectAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, (await controller.GetStatusAsync(CancellationToken.None)).State);
        await controller.DisconnectAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Disconnected, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task ASelectedExitThatIsNotInstalledExplainsHowToGetIt()
    {
        var psiphon = new Engine(EngineKind.Psiphon) { IsEnabled = true, Installed = false };
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), psiphon);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ConnectAsync(CancellationToken.None));

        Assert.Equal("Download it first.", error.Message);
        Assert.Equal(ConnectionState.NotReady, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task TwoSelectedExitsAreRefused()
    {
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre),
            new Engine(EngineKind.Psiphon) { IsEnabled = true }, new Engine(EngineKind.OpenVpn) { IsEnabled = true });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public void OnlyExitEnginesRegisterAsExits()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), new Engine(EngineKind.Xray)));
        Assert.Throws<ArgumentException>(() => new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre),
            new Engine(EngineKind.OpenVpn), new Engine(EngineKind.OpenVpn)));
    }

    [Fact]
    public async Task AFaultedOpenVpnExitIsNamedAndRepairedByRestartingTheTransport()
    {
        var openVpn = new Engine(EngineKind.OpenVpn) { IsEnabled = true };
        using var controller = new ConnectionController(
            new Engine(EngineKind.Paqet), new Engine(EngineKind.Xray), new Engine(EngineKind.ProxiFyre), openVpn);
        await controller.ConnectAsync(CancellationToken.None);

        openVpn.Fault();

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Faulted, status.State);
        Assert.Contains("OpenVpn: Faulted", status.Detail, StringComparison.Ordinal);
        Assert.Equal(ConnectionRecoveryAction.RestartTransport,
            ConnectionRecoveryPolicy.Evaluate(status.State, false, status.Engines));
    }

    private static XrayRoutingPolicy Policy(ExitProxyOutbound? exit) => new(
        RegionalRoutingPreset.IranDirect,
        XrayDomainStrategy.IPIfNonMatch,
        BypassLan: true,
        BlockAds: true,
        BlockQuic: true,
        DirectBitTorrent: true,
        Dns: new XrayDnsPolicy(XrayDnsResolver.Cloudflare, ["192.168.1.1"], UseFakeDns: true),
        EngineCredentials: Credentials,
        Exit: exit);

    private static PaqetFireSettings ValidSettings() => new()
    {
        ServerEndpoint = "server.example:8443",
        TransportKey = "secret",
    };

    private sealed class Engine(EngineKind kind, IList<EngineKind>? stops = null) : IExitEngineAdapter
    {
        public EngineKind Kind { get; } = kind;

        public bool IsEnabled { get; init; }

        public EngineState State { get; private set; } = EngineState.Stopped;

        public bool FailOnStart { get; init; }

        public bool Installed { get; init; } = true;

        public int StartCount { get; private set; }

        public List<string>? Events { get; init; }

        public void Fault() => State = EngineState.Faulted;

        public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Installed
                ? new EngineStatus(Kind, State)
                : new EngineStatus(Kind, EngineState.NotInstalled, Detail: "Download it first."));

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            Events?.Add($"start {Kind}");
            if (FailOnStart)
            {
                State = EngineState.Faulted;
                throw new InvalidOperationException($"{Kind} failed to start.");
            }

            State = EngineState.Running;
            Events?.Add($"started {Kind}");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Events?.Add($"stop {Kind}");
            stops?.Add(Kind);
            State = EngineState.Stopped;
            return ValueTask.CompletedTask;
        }
    }
}
