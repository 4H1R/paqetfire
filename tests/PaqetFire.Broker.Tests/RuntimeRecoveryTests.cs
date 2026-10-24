using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Network;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.Routing;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class RuntimeRecoveryTests
{
    [Fact]
    public async Task APsiphonProfileWritesItsExitAndLocksItOutOfCapture()
    {
        var psiphon = new PsiphonProcessAdapter(new PsiphonProcessOptions
        {
            ExecutablePath = "missing-psiphon.exe",
            ConfigurationPath = "missing-config.json",
            DataDirectory = Path.GetTempPath(),
            ExpectedExecutableSha256 = new string('0', 64),
            Version = "test",
            DataDirectoryPolicy = null,
        });
        await using var fixture = await RuntimeFixture.StartAsync(
            settings => settings with { ExitProxy = ExitProxyKind.Psiphon, Psiphon = new PsiphonExitOptions("nl") },
            psiphon: psiphon);

        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.True(psiphon.IsEnabled);
        Assert.Equal(new ExitProxyOutbound(PsiphonJsonConfigurationWriter.LocalSocksPort), fixture.Xray.Last!.Exit);
        var configuration = fixture.Configurations.Files[fixture.Paths.PsiphonConfigurationPath];
        Assert.Contains("\"EgressRegion\": \"NL\"", configuration, StringComparison.Ordinal);
        Assert.Contains("@127.0.0.1:1080", configuration, StringComparison.Ordinal);
        var capture = fixture.Configurations.Files[fixture.Paths.ProxiFyreConfigurationPath];
        Assert.Contains(Path.GetFileName(fixture.Paths.PsiphonExecutablePath), capture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProfileWithoutAnExitLeavesPsiphonOff()
    {
        var psiphon = new PsiphonProcessAdapter(new PsiphonProcessOptions
        {
            ExecutablePath = "missing-psiphon.exe",
            ConfigurationPath = "missing-config.json",
            DataDirectory = Path.GetTempPath(),
            ExpectedExecutableSha256 = new string('0', 64),
            Version = "test",
            DataDirectoryPolicy = null,
        });
        psiphon.UseExit(true);
        await using var fixture = await RuntimeFixture.StartAsync(psiphon: psiphon);

        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.False(psiphon.IsEnabled);
        Assert.Null(fixture.Xray.Last!.Exit);
        Assert.False(fixture.Configurations.Files.ContainsKey(fixture.Paths.PsiphonConfigurationPath));
    }

    [Fact]
    public async Task RepeatedConnectKeepsAWorkingTunnel()
    {
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with { KillSwitchEnabled = true });
        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        var snapshot = await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.Equal(["guard", "connect"], fixture.Controller.Calls);
        Assert.Equal(ConnectionState.Connected, snapshot.ConnectionState);
    }

    [Fact]
    public async Task TransportRecoveryDropsTheHotspotShareInsteadOfFailing()
    {
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with { ShareViaHotspot = true, BypassLan = true });
        fixture.Hotspot = ("192.168.137.1", "Local Area Connection* 2");
        await fixture.Runtime.ConnectAsync(CancellationToken.None);
        Assert.NotNull(fixture.Xray.Last!.HotspotShare);

        // Windows turned Mobile hotspot off by itself.
        fixture.Hotspot = null;
        await fixture.Runtime.RestartTransportAsync(CancellationToken.None);

        Assert.Equal("restart+policy", fixture.Controller.Calls[^1]);
        Assert.Null(fixture.Xray.Last!.HotspotShare);
        var snapshot = await fixture.Runtime.GetSnapshotAsync(CancellationToken.None);
        Assert.Contains("hotspot", snapshot.OperationWarning, StringComparison.OrdinalIgnoreCase);

        // An explicit connect still reports the missing hotspot to the user.
        await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Runtime.ConnectAsync(CancellationToken.None).AsTask());
        Assert.Contains("hotspot", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkChangesRestartOnlyTheEnginesWhoseInputsChanged()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        await fixture.Runtime.HandleNetworkChangeAsync(resumed: false, CancellationToken.None);
        Assert.Equal(["connect"], fixture.Controller.Calls);

        fixture.GatewayMac = "02:AA:BB:CC:DD:EE";
        await fixture.Runtime.HandleNetworkChangeAsync(resumed: false, CancellationToken.None);
        Assert.Equal("restart", fixture.Controller.Calls[^1]);

        fixture.Adapter = fixture.Adapter with { DnsServers = [IPAddress.Parse("192.168.1.53")] };
        await fixture.Runtime.HandleNetworkChangeAsync(resumed: false, CancellationToken.None);
        Assert.Equal("restart+policy", fixture.Controller.Calls[^1]);

        await fixture.Runtime.HandleNetworkChangeAsync(resumed: true, CancellationToken.None);
        Assert.Equal(["connect", "restart", "restart+policy", "restart"], fixture.Controller.Calls);
        Assert.NotEqual(ConnectionStage.RecoveringTransport, (await fixture.Runtime.GetSnapshotAsync(CancellationToken.None)).Progress?.Stage);
    }

    [Fact]
    public async Task CarrierFaultIsRestartedInPlaceThenReleasedAfterRepeatedFailures()
    {
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with { KillSwitchEnabled = true });
        await fixture.Runtime.ConnectAsync(CancellationToken.None);
        fixture.Controller.FailRestart = true;
        fixture.Controller.Paqet = EngineState.Faulted;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Runtime.SuperviseAsync(CancellationToken.None);
            Assert.Equal(attempt, fixture.Controller.Calls.Count(call => call == "restart"));
            var recovering = await fixture.Runtime.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(ConnectionStage.RecoveringTransport, recovering.Progress?.Stage);
            Assert.Equal(attempt, recovering.Progress?.Attempt);
            Assert.Equal(ConnectionState.Connecting, recovering.ConnectionState);
        }

        // Xray and ProxiFyre were never touched while Paqet was retried.
        Assert.Equal(["guard", "connect", "restart", "restart", "restart", "restart", "restart"], fixture.Controller.Calls);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        Assert.Equal("guard", fixture.Controller.Calls[^1]);
        var released = await fixture.Runtime.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Guarded, released.ConnectionState);
        Assert.Contains("stopped retrying", released.OperationWarning, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.State.Load().DesiredConnected);

        // Suspended until a resume or network change re-arms it.
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        Assert.Equal("guard", fixture.Controller.Calls[^1]);
        fixture.Controller.FailRestart = false;
        await fixture.Runtime.HandleNetworkChangeAsync(resumed: true, CancellationToken.None);
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        Assert.Equal("connect", fixture.Controller.Calls[^1]);
    }

    [Fact]
    public async Task SupervisionNeverTearsDownARouteAnotherOperationIsBuilding()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        fixture.Controller.Xray = EngineState.Faulted;
        var release = new TaskCompletionSource();
        fixture.Controller.ConnectGate = release.Task;

        var connecting = fixture.Runtime.ConnectAsync(CancellationToken.None).AsTask();
        await fixture.Controller.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The chain looked broken before the connect began; supervision must not act on that.
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        release.SetResult();
        var snapshot = await connecting;
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Connected, snapshot.ConnectionState);
        Assert.Equal(["connect"], fixture.Controller.Calls);
    }

    [Fact]
    public async Task RouteLeftConnectedIsRestoredAfterABrokerRestart()
    {
        var state = new InMemoryBrokerStateStore();
        var catalog = new CatalogStore();
        await using (var before = await RuntimeFixture.StartAsync(state: state, catalog: catalog))
        {
            await before.Runtime.ConnectAsync(CancellationToken.None);
            Assert.True(state.Load().DesiredConnected);
            Assert.Equal(catalog.Catalog!.ActiveProfileId, state.Load().DesiredProfileId);
        }

        await using (var restarted = await RuntimeFixture.StartAsync(state: state, catalog: catalog))
        {
            await restarted.Runtime.SuperviseAsync(CancellationToken.None);
            Assert.Equal(["connect"], restarted.Controller.Calls);

            await restarted.Runtime.DisconnectAsync(CancellationToken.None);
            Assert.False(state.Load().DesiredConnected);
        }

        await using var afterDisconnect = await RuntimeFixture.StartAsync(state: state, catalog: catalog);
        await afterDisconnect.Runtime.SuperviseAsync(CancellationToken.None);
        Assert.Empty(afterDisconnect.Controller.Calls);
    }

    [Fact]
    public async Task DisconnectCancelsAConnectInProgress()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        fixture.Controller.ConnectGate = Task.Delay(Timeout.Infinite);

        var connecting = fixture.Runtime.ConnectAsync(CancellationToken.None).AsTask();
        await fixture.Controller.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disconnected = await fixture.Runtime.DisconnectAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = await connecting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("cancelled", cancelled.OperationWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Disconnected, disconnected.ConnectionState);
        Assert.Null(disconnected.Progress);
        Assert.Equal(["connect", "disconnect"], fixture.Controller.Calls);
        Assert.False(fixture.State.Load().DesiredConnected);
    }

    [Fact]
    public async Task ConnectPublishesEveryStage()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var stages = new List<ConnectionStage>();
        fixture.Progress.Changed += () =>
        {
            if (fixture.Progress.Current is { } current && (stages.Count == 0 || stages[^1] != current.Stage))
            {
                stages.Add(current.Stage);
            }
        };

        var snapshot = await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.Equal(
            [
                ConnectionStage.Preparing, ConnectionStage.VerifyingPayload, ConnectionStage.StartingTransport,
                ConnectionStage.StartingPolicy, ConnectionStage.StartingCapture, ConnectionStage.ProbingTunnel,
            ],
            stages);
        Assert.Equal(ConnectionStage.ProbingTunnel, snapshot.Progress?.Stage);
    }

    [Fact]
    public async Task ServerWithoutIpv6StopsAaaaAnswersFromTheNextConnectOnly()
    {
        var probe = new FixedIpv6Probe(Ipv6Capability.Unsupported);
        await using var fixture = await RuntimeFixture.StartAsync(ipv6Probe: probe);
        await fixture.Runtime.ConnectAsync(CancellationToken.None);
        Assert.True(fixture.Xray.Last!.Dns!.ResolveIpv6);

        fixture.Tunnel.Observe(true, DateTimeOffset.UtcNow);
        await WaitUntilAsync(() => fixture.State.Load().Ipv6Support.ContainsKey(RuntimeFixture.Server));
        Assert.False(fixture.State.Load().Ipv6Support[RuntimeFixture.Server]);
        Assert.Contains("IPv6", (await fixture.Runtime.GetSnapshotAsync(CancellationToken.None)).OperationWarning);

        // No Xray restart mid-session just for this.
        await fixture.Runtime.RestartTransportAsync(CancellationToken.None);
        Assert.Equal("restart", fixture.Controller.Calls[^1]);
        Assert.True(fixture.Xray.Last!.Dns!.ResolveIpv6);

        await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        await fixture.Runtime.ConnectAsync(CancellationToken.None);
        Assert.False(fixture.Xray.Last!.Dns!.ResolveIpv6);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FixedIpv6Probe(Ipv6Capability result) : IIpv6CapabilityProbe
    {
        public ValueTask<Ipv6Capability> ProbeAsync(CancellationToken cancellationToken) => new(result);
    }
}

internal sealed class RuntimeFixture : IAsyncDisposable
{
    public const string Server = "203.0.113.10:8443";

    private readonly PaqetProcessAdapter paqet;
    private readonly XrayProcessAdapter xray;
    private readonly string root;

    public static async Task<RuntimeFixture> StartAsync(
        Func<PaqetFireSettings, PaqetFireSettings>? configure = null,
        IBrokerStateStore? state = null,
        CatalogStore? catalog = null,
        IIpv6CapabilityProbe? ipv6Probe = null,
        RoutingDataService? routingData = null,
        PsiphonProcessAdapter? psiphon = null)
    {
        var fixture = new RuntimeFixture(configure, state, catalog, ipv6Probe, routingData, psiphon);
        await fixture.Runtime.InitializeAsync(CancellationToken.None);
        return fixture;
    }

    private RuntimeFixture(
        Func<PaqetFireSettings, PaqetFireSettings>? configure = null,
        IBrokerStateStore? state = null,
        CatalogStore? catalog = null,
        IIpv6CapabilityProbe? ipv6Probe = null,
        RoutingDataService? routingData = null,
        PsiphonProcessAdapter? psiphon = null)
    {
        root = Path.Combine(Path.GetTempPath(), "PaqetFire-runtime-tests", Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "engines"));
        var engineFile = Path.Combine(payload, "engines", "engine.bin");
        File.WriteAllBytes(engineFile, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(payload, "payload-manifest.json"), $$"""
            {"schemaVersion":"1","architecture":"x64","prerequisites":[],"engines":[
              {"engine":"paqet","version":"test","entryPoint":"engines/engine.bin","files":[
                {"path":"engines/engine.bin","size":4,"sha256":"{{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engineFile)))}}"}]}]}
            """);
        Paths = new RuntimePaths(payload, Path.Combine(payload, "pf-test-paqet.exe"), Path.Combine(payload, "paqet.yaml"),
            Path.Combine(payload, "pf-test-xray.exe"), Path.Combine(payload, "xray.json"), Path.Combine(payload, "geoip.dat"),
            Path.Combine(payload, "geosite.dat"), Path.Combine(payload, "pf-test-proxifyre.exe"),
            Path.Combine(payload, "proxifyre.json"), Path.Combine(root, "settings.json"));

        var settings = new PaqetFireSettings
        {
            ProfileName = "Test",
            ServerEndpoint = Server,
            TransportKey = "test-key",
            LanSocksPassword = "local-password",
        };
        Catalog = catalog ?? new CatalogStore();
        Catalog.Catalog ??= ProfileCatalog.Create(Guid.NewGuid(), configure?.Invoke(settings) ?? settings);
        State = state ?? new InMemoryBrokerStateStore();
        Clock = new ManualClock();
        Progress = new ConnectionProgressTracker(Clock);
        paqet = new PaqetProcessAdapter(BundledEngineOptions.CreatePaqet(Paths));
        xray = new XrayProcessAdapter(BundledEngineOptions.CreateXray(Paths));

        Runtime = new PaqetFireRuntime(
            Controller,
            paqet,
            xray,
            new SettingsStore(),
            Catalog,
            Configurations,
            new TextPaqetWriter(),
            Xray,
            new ProxiFyreJsonConfigurationWriter(),
            new NetworkEnvironmentDetector(new NetworkAdapterDetector(() => [Adapter], (_, _) => GatewayMac)),
            new PaqetFire.Broker.Network.HotspotNetworkDetector(() => Hotspot),
            new PayloadIntegrityInspector(payload),
            new PrerequisiteInspector(() => [new PrerequisiteStatus("npcap", "Npcap", true, "test")]),
            new EngineFirewallRules(new InMemoryFirewallRuleStore(), Paths, NullLogger<EngineFirewallRules>.Instance),
            null!,
            EngineSocksCredentials.Generate(),
            Tunnel,
            Paths,
            NullLogger<PaqetFireRuntime>.Instance,
            State,
            Progress,
            ipv6Probe,
            Clock,
            routingData,
            psiphon);
    }

    public XrayProcessAdapter XrayAdapter => xray;

    public RuntimePaths Paths { get; }

    public PaqetFireRuntime Runtime { get; }

    public ScriptedController Controller { get; } = new();

    public RecordingConfigurations Configurations { get; } = new();

    public RecordingXrayWriter Xray { get; } = new();

    public CatalogStore Catalog { get; }

    public IBrokerStateStore State { get; }

    public TunnelHealthTracker Tunnel { get; } = new();

    public ConnectionProgressTracker Progress { get; }

    public ManualClock Clock { get; }

    public NetworkAdapterDetails Adapter { get; set; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "Ethernet",
        IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.1"), NetworkInterfaceType.Ethernet)
    {
        DnsServers = [IPAddress.Parse("192.168.1.1")],
    };

    public string GatewayMac { get; set; } = "02:11:22:33:44:55";

    public (string Address, string Name)? Hotspot { get; set; }

    public async ValueTask DisposeAsync()
    {
        await paqet.DisposeAsync();
        await xray.DisposeAsync();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort for a temporary directory.
        }
    }

    private sealed class SettingsStore : IMachineSettingsStore
    {
        public ValueTask<PaqetFireSettings?> LoadAsync(CancellationToken token) => ValueTask.FromResult<PaqetFireSettings?>(null);

        public ValueTask SaveAsync(PaqetFireSettings settings, CancellationToken token) => ValueTask.CompletedTask;
    }

    private sealed class TextPaqetWriter : IPaqetConfigurationWriter
    {
        public string Write(PaqetProfile profile, string transportKey) =>
            $"{profile.ServerEndpoint}|{profile.InterfaceGuid}|{profile.LocalIpv4Address}|{profile.RouterMac}";
    }
}

internal sealed class CatalogStore : IMachineProfileCatalogStore
{
    public ProfileCatalog? Catalog { get; set; }

    public ValueTask<ProfileCatalog?> LoadAsync(CancellationToken token) => ValueTask.FromResult(Catalog);

    public ValueTask SaveAsync(ProfileCatalog catalog, CancellationToken token)
    {
        Catalog = catalog;
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingXrayWriter : IXrayConfigurationWriter
{
    public XrayRoutingPolicy? Last { get; private set; }

    public string Write(XrayRoutingPolicy policy)
    {
        Last = policy;
        return $"lan={policy.LanShare?.ListenAddress};hotspot={policy.HotspotShare?.ListenAddress};" +
               $"dns={string.Join(',', policy.Dns?.DomesticServers ?? [])};ipv6={policy.Dns?.ResolveIpv6}";
    }
}

internal sealed class RecordingConfigurations : IAtomicConfigurationStore
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task WriteAsync(string path, string text, CancellationToken token = default)
    {
        Files[path] = text;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string destinationPath, CancellationToken token = default)
    {
        Files.Remove(destinationPath);
        return Task.CompletedTask;
    }

    public Task RollbackAsync(string path, CancellationToken token = default) => Task.CompletedTask;
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan duration) => now += duration;
}

/// <summary>An engine chain whose states and failures each test scripts directly.</summary>
internal sealed class ScriptedController : IConnectionController
{
    public List<string> Calls { get; } = [];

    public EngineState Paqet { get; set; } = EngineState.Stopped;

    public EngineState Xray { get; set; } = EngineState.Stopped;

    public EngineState ProxiFyre { get; set; } = EngineState.Stopped;

    public bool FailRestart { get; set; }

    public Task? ConnectGate { get; set; }

    public TaskCompletionSource ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var state = (Paqet, Xray, ProxiFyre) switch
        {
            (EngineState.Running, EngineState.Running, EngineState.Running) => ConnectionState.Connected,
            (EngineState.Stopped, EngineState.Stopped, EngineState.Stopped) => ConnectionState.Disconnected,
            (EngineState.Stopped, EngineState.Stopped, EngineState.Running) => ConnectionState.Guarded,
            _ when new[] { Paqet, Xray, ProxiFyre }.Contains(EngineState.Faulted) => ConnectionState.Faulted,
            _ => ConnectionState.Degraded,
        };
        return ValueTask.FromResult(new ConnectionStatus(
            state,
            new EngineStatus(EngineKind.Paqet, Paqet),
            new EngineStatus(EngineKind.Xray, Xray),
            new EngineStatus(EngineKind.ProxiFyre, ProxiFyre)));
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken) => ConnectAsync(null, cancellationToken);

    /// <summary>Failures the next connects throw, one each, before any engine starts.</summary>
    public Queue<Exception> ConnectFailures { get; } = new();

    public async ValueTask ConnectAsync(Action<ConnectionStage>? stageStarted, CancellationToken cancellationToken)
    {
        Calls.Add("connect");
        ConnectEntered.TrySetResult();
        if (ConnectFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        stageStarted?.Invoke(ConnectionStage.StartingTransport);
        if (ConnectGate is not null)
        {
            await ConnectGate.WaitAsync(cancellationToken);
        }

        if (FailConnect)
        {
            // Rolled back like the real controller: nothing it started stays running.
            Paqet = Xray = EngineState.Stopped;
            throw new InvalidOperationException("Paqet failed to start.");
        }

        stageStarted?.Invoke(ConnectionStage.StartingPolicy);
        stageStarted?.Invoke(ConnectionStage.StartingCapture);
        Paqet = Xray = ProxiFyre = EngineState.Running;
    }

    public bool FailConnect { get; set; }

    public ValueTask GuardAsync(CancellationToken cancellationToken)
    {
        Calls.Add("guard");
        Paqet = Xray = EngineState.Stopped;
        ProxiFyre = EngineState.Running;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        Calls.Add("disconnect");
        Paqet = Xray = ProxiFyre = EngineState.Stopped;
        return ValueTask.CompletedTask;
    }

    public ValueTask RestartTransportAsync(CancellationToken cancellationToken) =>
        RestartTransportAsync(false, cancellationToken);

    public ValueTask RestartTransportAsync(bool includePolicy, CancellationToken cancellationToken)
    {
        Calls.Add(includePolicy ? "restart+policy" : "restart");
        if (FailRestart)
        {
            Paqet = EngineState.Faulted;
            throw new InvalidOperationException("Paqet failed to start.");
        }

        Paqet = EngineState.Running;
        if (includePolicy)
        {
            Xray = EngineState.Running;
        }

        return ValueTask.CompletedTask;
    }
}
