using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class HotspotSharingTests
{
    [Fact]
    public void HotspotSharingRequiresLocalNetworkBypass()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            ShareViaHotspot = true,
            BypassLan = false,
            LanSocksPassword = "password123",
        };
        Assert.Contains(PaqetFireSettingsValidator.Validate(settings),
            error => error.Contains("requires direct access", StringComparison.Ordinal));
        Assert.Empty(PaqetFireSettingsValidator.Validate(settings with { BypassLan = true }));
    }

    [Theory]
    [InlineData(System.Net.NetworkInformation.OperationalStatus.Down, "Microsoft Wi-Fi Direct Virtual Adapter")]
    [InlineData(System.Net.NetworkInformation.OperationalStatus.Up, "Intel Ethernet Controller")]
    [InlineData(System.Net.NetworkInformation.OperationalStatus.Up, "Hyper-V Virtual Ethernet Adapter")]
    public void DetectionRejectsInactiveOrUnrelatedAdapters(
        System.Net.NetworkInformation.OperationalStatus status, string description)
    {
        Assert.Null(new HotspotNetworkDetector().TryDetect([new RejectedAdapter(status, description)]));
    }

    private sealed class RejectedAdapter(
        System.Net.NetworkInformation.OperationalStatus status,
        string description) : System.Net.NetworkInformation.NetworkInterface
    {
        public override System.Net.NetworkInformation.OperationalStatus OperationalStatus => status;
        public override System.Net.NetworkInformation.NetworkInterfaceType NetworkInterfaceType =>
            System.Net.NetworkInformation.NetworkInterfaceType.Ethernet;
        public override string Description => description;
        public override string Name => "Local Area Connection";
        public override System.Net.NetworkInformation.IPInterfaceProperties GetIPProperties() =>
            throw new InvalidOperationException("Rejected adapters must not be inspected for hotspot addresses.");
    }

    [Fact]
    public void HotspotShare_EmitsSecondInboundOnHotspotAddress()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.IranDirect,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true, BlockAds: true, BlockQuic: false, DirectBitTorrent: true,
            LanShare: new LanSocksShare("192.168.1.10", 1082, "paqetfire", "correct-horse-1"),
            HotspotShare: new LanSocksShare("192.168.137.1", 10808, "paqetfire", "correct-horse-1")));

        using var document = JsonDocument.Parse(json);
        var inbounds = document.RootElement.GetProperty("inbounds").EnumerateArray().ToArray();
        Assert.Equal(3, inbounds.Length);
        var hotspot = inbounds.First(i => i.GetProperty("tag").GetString() == "hotspot-share-in");
        Assert.Equal("192.168.137.1", hotspot.GetProperty("listen").GetString());
        Assert.Equal(10808, hotspot.GetProperty("port").GetInt32());
        Assert.Equal("password", hotspot.GetProperty("settings").GetProperty("auth").GetString());
        Assert.Equal("paqetfire", hotspot.GetProperty("settings").GetProperty("accounts")[0].GetProperty("user").GetString());
        Assert.Equal("correct-horse-1", hotspot.GetProperty("settings").GetProperty("accounts")[0].GetProperty("pass").GetString());
        Assert.True(hotspot.GetProperty("settings").GetProperty("udp").GetBoolean());
        Assert.False(hotspot.GetProperty("sniffing").GetProperty("routeOnly").GetBoolean());
    }

    [Fact]
    public void HotspotOnly_RejectsCredentialsOutsideLanRules()
    {
        static PaqetFireSettings HotspotOnly(string username, string password) => new()
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            ShareViaHotspot = true,
            HotspotSocksPort = 10808,
            LanSocksUsername = username,
            LanSocksPassword = password,
        };

        var cases = new[]
        {
            HotspotOnly(new string('u', 100), "correct-horse-1"),
            HotspotOnly("bad\tuser", "correct-horse-1"),
            HotspotOnly("paqetfire", new string('p', 129)),
            HotspotOnly("paqetfire", "bad\tpassword"),
        };

        foreach (var settings in cases)
        {
            Assert.Contains(
                PaqetFireSettingsValidator.Validate(settings),
                error => error.Contains("Hotspot sharing requires a valid proxy username", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void HotspotOnly_ValidSettings_PassesValidationWithoutLanShare()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            ShareWithLan = false,
            ShareViaHotspot = true,
            BypassLan = true,
            HotspotSocksPort = 10808,
            LanSocksUsername = "paqetfire",
            LanSocksPassword = "correct-horse-battery",
        };

        var errors = PaqetFireSettingsValidator.Validate(settings);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("192.168.137.1", true)]
    [InlineData("192.168.173.5", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("10.0.0.5", false)]
    public void IsHotspotAddress_MatchesOnlyIcsDefaults(string ip, bool expected)
    {
        Assert.Equal(expected, HotspotNetworkDetector.IsHotspotAddress(System.Net.IPAddress.Parse(ip)));
    }

    [Fact]
    public void CoreHotspotNetworkDetector_IsDirectlyUsable()
    {
        var detector = new PaqetFire.Core.Network.HotspotNetworkDetector();
        Assert.NotNull(detector);
        Assert.True(PaqetFire.Core.Network.HotspotNetworkDetector.IsHotspotAddress(System.Net.IPAddress.Parse("192.168.137.1")));
    }

    [Fact]
    public void HotspotValidation_RejectsCollisionWithLanPort()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            ShareWithLan = true,
            LanSocksPort = 10808,
            LanSocksUsername = "paqetfire",
            LanSocksPassword = "correct-horse-1",
            ShareViaHotspot = true,
            HotspotSocksPort = 10808,
        };
        Assert.Contains(PaqetFireSettingsValidator.Validate(settings),
            e => e.Contains("must differ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PaqetFireRuntime_CanBeActivatedByServiceContainer_WithHotspotDetector()
    {
        var services = new ServiceCollection();
        var tempDir = AppContext.BaseDirectory;
        var paths = new RuntimePaths(
            tempDir,
            Path.Combine(tempDir, "p.exe"),
            Path.Combine(tempDir, "p.yaml"),
            Path.Combine(tempDir, "x.exe"),
            Path.Combine(tempDir, "x.json"),
            Path.Combine(tempDir, "g.dat"),
            Path.Combine(tempDir, "s.dat"),
            Path.Combine(tempDir, "pf.exe"),
            Path.Combine(tempDir, "a.json"),
            Path.Combine(tempDir, "s.json"));

        services.AddSingleton(paths);
        services.AddSingleton<PayloadIntegrityInspector>();
        services.AddSingleton<PrerequisiteInspector>();
        services.AddSingleton<NetworkEnvironmentDetector>();
        services.AddSingleton<HotspotNetworkDetector>();
        services.AddSingleton<IPaqetConfigurationWriter, PaqetYamlConfigurationWriter>();
        services.AddSingleton<IXrayConfigurationWriter, XrayJsonConfigurationWriter>();
        services.AddSingleton<IProxiFyreConfigurationWriter, ProxiFyreJsonConfigurationWriter>();
        services.AddSingleton<IMachineSettingsStore>(_ => new MachineSettingsStore(paths.MachineSettingsPath));
        services.AddSingleton<IAtomicConfigurationStore>(_ => new AtomicConfigurationStore(paths.PayloadRoot, [paths.PaqetConfigurationPath]));
        services.AddSingleton(_ => new PaqetProcessAdapter(new PaqetProcessOptions
        {
            ExecutablePath = paths.PaqetExecutablePath,
            ConfigurationPath = paths.PaqetConfigurationPath,
            TrustedExecutableRoot = paths.PayloadRoot,
            TrustedConfigurationRoot = paths.PayloadRoot,
            SocksEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1080),
            Version = "v1.0.0",
            ExpectedExecutableSha256 = "7d73f5130757b538c26c3ed76439a150978c3e06289c724de37d916789aaf5dc",
            ReadinessTimeout = TimeSpan.FromSeconds(1),
        }));
        services.AddSingleton(_ => new XrayProcessAdapter(new XrayProcessOptions
        {
            ExecutablePath = paths.XrayExecutablePath,
            ConfigurationPath = paths.XrayConfigurationPath,
            GeoIpPath = paths.XrayGeoIpPath,
            GeoSitePath = paths.XrayGeoSitePath,
            Version = "1.0.0",
            ExpectedExecutableSha256 = "15c2d007954ac53ba69b80ec91242786b3c0b71d52649165b4ca1d5cc96ef8f1",
            InboundEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 10808),
            ReadinessTimeout = TimeSpan.FromSeconds(1),
        }));
        services.AddSingleton(_ => new ProxiFyreProcessAdapter(new ProxiFyreProcessOptions
        {
            ExecutablePath = paths.ProxiFyreExecutablePath,
            ConfigurationPath = paths.ProxiFyreConfigurationPath,
            Version = "1.0.0",
            ExpectedExecutableSha256 = "eaa48f0efc0dfbbab6f4ea6fc2ff6c7b45164dca544921962025b698bd59869f",
            ReadinessTimeout = TimeSpan.FromSeconds(1),
        }));
        services.AddSingleton<IConnectionController>(s => new ConnectionController(
            s.GetRequiredService<PaqetProcessAdapter>(),
            s.GetRequiredService<XrayProcessAdapter>(),
            s.GetRequiredService<ProxiFyreProcessAdapter>()));
        services.AddSingleton<IPaqetFireRuntime, PaqetFireRuntime>();
        services.AddLogging();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var runtime = provider.GetRequiredService<IPaqetFireRuntime>();
        Assert.NotNull(runtime);
    }

    [Fact]
    public void PaqetFireRuntime_ActivationFails_WhenHotspotDetectorIsMissing()
    {
        var services = new ServiceCollection();
        var tempDir = AppContext.BaseDirectory;
        var paths = new RuntimePaths(
            tempDir,
            Path.Combine(tempDir, "p.exe"),
            Path.Combine(tempDir, "p.yaml"),
            Path.Combine(tempDir, "x.exe"),
            Path.Combine(tempDir, "x.json"),
            Path.Combine(tempDir, "g.dat"),
            Path.Combine(tempDir, "s.dat"),
            Path.Combine(tempDir, "pf.exe"),
            Path.Combine(tempDir, "a.json"),
            Path.Combine(tempDir, "s.json"));

        services.AddSingleton(paths);
        services.AddSingleton<PayloadIntegrityInspector>();
        services.AddSingleton<PrerequisiteInspector>();
        services.AddSingleton<NetworkEnvironmentDetector>();
        // Intentionally omit HotspotNetworkDetector to verify fail-fast behavior
        services.AddSingleton<IPaqetConfigurationWriter, PaqetYamlConfigurationWriter>();
        services.AddSingleton<IXrayConfigurationWriter, XrayJsonConfigurationWriter>();
        services.AddSingleton<IProxiFyreConfigurationWriter, ProxiFyreJsonConfigurationWriter>();
        services.AddSingleton<IMachineSettingsStore>(_ => new MachineSettingsStore(paths.MachineSettingsPath));
        services.AddSingleton<IAtomicConfigurationStore>(_ => new AtomicConfigurationStore(paths.PayloadRoot, [paths.PaqetConfigurationPath]));
        services.AddSingleton(_ => new PaqetProcessAdapter(new PaqetProcessOptions
        {
            ExecutablePath = paths.PaqetExecutablePath,
            ConfigurationPath = paths.PaqetConfigurationPath,
            TrustedExecutableRoot = paths.PayloadRoot,
            TrustedConfigurationRoot = paths.PayloadRoot,
            SocksEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1080),
            Version = "v1.0.0",
            ExpectedExecutableSha256 = "7d73f5130757b538c26c3ed76439a150978c3e06289c724de37d916789aaf5dc",
            ReadinessTimeout = TimeSpan.FromSeconds(1),
        }));
        services.AddSingleton(_ => new XrayProcessAdapter(new XrayProcessOptions
        {
            ExecutablePath = paths.XrayExecutablePath,
            ConfigurationPath = paths.XrayConfigurationPath,
            GeoIpPath = paths.XrayGeoIpPath,
            GeoSitePath = paths.XrayGeoSitePath,
            Version = "1.0.0",
            ExpectedExecutableSha256 = "15c2d007954ac53ba69b80ec91242786b3c0b71d52649165b4ca1d5cc96ef8f1",
            InboundEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 10808),
            ReadinessTimeout = TimeSpan.FromSeconds(1),
        }));
        services.AddSingleton(_ => new ProxiFyreProcessAdapter(new ProxiFyreProcessOptions
        {
            ExecutablePath = paths.ProxiFyreExecutablePath,
            ConfigurationPath = paths.ProxiFyreConfigurationPath,
            Version = "1.0.0",
            ExpectedExecutableSha256 = "eaa48f0efc0dfbbab6f4ea6fc2ff6c7b45164dca544921962025b698bd59869f",
            ReadinessTimeout = TimeSpan.FromSeconds(1),
        }));
        services.AddSingleton<IConnectionController>(s => new ConnectionController(
            s.GetRequiredService<PaqetProcessAdapter>(),
            s.GetRequiredService<XrayProcessAdapter>(),
            s.GetRequiredService<ProxiFyreProcessAdapter>()));
        services.AddSingleton<IPaqetFireRuntime, PaqetFireRuntime>();
        services.AddLogging();

        var ex = Assert.Throws<AggregateException>(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        });

        Assert.Contains("HotspotNetworkDetector", ex.Message);
    }
}
