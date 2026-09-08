using System.Text.Json;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void RouteEverything_UsesCatchAllAndFullPathEngineExclusions()
    {
        var paqet = @"C:\Program Files\PaqetFire\broker\payload\engines\paqet\x64\paqet.exe";
        var proxiFyre = @"C:\Program Files\PaqetFire\broker\payload\engines\proxifyre\x64\ProxiFyre.exe";
        var broker = @"C:\Program Files\PaqetFire\broker\PaqetFire.Broker.exe";
        var policy = new RoutingPolicy(
            RoutingMode.AllApplications,
            "127.0.0.1:1080",
            [],
            ["steam.exe"],
            BypassLan: false);

        var plan = RoutingPolicyCompiler.Compile(policy, paqet, proxiFyre, [broker]);
        var locked = RoutingPolicyCompiler.CreateLockedExclusions(paqet, proxiFyre, [broker]);

        Assert.Equal(string.Empty, Assert.Single(Assert.Single(plan.Rules).Applications));
        Assert.Contains(paqet, plan.Exclusions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(proxiFyre, plan.Exclusions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(broker, plan.Exclusions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("steam.exe", plan.Exclusions, StringComparer.OrdinalIgnoreCase);

        var json = new ProxiFyreJsonConfigurationWriter().Write(plan, locked);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("bypassLan").GetBoolean());
        Assert.Equal(
            string.Empty,
            document.RootElement.GetProperty("proxies")[0].GetProperty("appNames")[0].GetString());
    }

    [Fact]
    public void SelectedApplications_RequiresAtLeastOneExecutable()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            RoutingMode = RoutingMode.SelectedApplications,
        };

        var errors = PaqetFireSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("at least one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoutingLists_RejectOversizedProfiles()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            SelectedApplications = Enumerable.Range(0, 129).Select(index => $"app-{index}.exe").ToArray(),
            UserExclusions = Enumerable.Range(0, 9).Select(index => $"{index}-{new string('x', 1022)}").ToArray(),
            DirectRouteDestinations = Enumerable.Range(0, 129).Select(index => $"host-{index}.example.com").ToArray(),
        };

        var errors = PaqetFireSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("selected application list is too large", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("exclusion list is too large", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("direct-route destination list is too large", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProtocolSelection_IsPreservedAndRequiresOneChoicePerGroup()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            RouteTcp = false,
            RouteUdp = true,
            RouteIpv4 = true,
            RouteIpv6 = false,
            DirectRouteDestinations = ["*.example.com", "203.0.113.10"],
        };

        var view = PaqetFireSettingsView.FromSettings(settings);

        Assert.False(view.RouteTcp);
        Assert.True(view.RouteUdp);
        Assert.True(view.RouteIpv4);
        Assert.False(view.RouteIpv6);
        Assert.Equal(settings.DirectRouteDestinations, view.DirectRouteDestinations);
        Assert.Empty(PaqetFireSettingsValidator.Validate(settings));

        var invalid = settings with
        {
            RouteUdp = false,
            RouteIpv4 = false,
        };
        var errors = PaqetFireSettingsValidator.Validate(invalid);
        Assert.Contains(errors, error => error.Contains("protocol", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("address family", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PaqetYaml_ContainsDetectedNetworkAndTransportSettings()
    {
        var profile = new PaqetProfile(
            "203.0.113.10:8443",
            "127.0.0.1:1080",
            "Ethernet",
            "7b35c9bb-cf61-4c14-9f3d-53b7468c7cc8",
            "192.168.1.20",
            "aa:bb:cc:dd:ee:ff",
            ["PA"],
            ["PA"],
            "fast2");

        var yaml = new PaqetYamlConfigurationWriter().Write(profile, "transport-secret");

        Assert.Contains("role: \"client\"", yaml, StringComparison.Ordinal);
        Assert.Contains("listen: \"127.0.0.1:1080\"", yaml, StringComparison.Ordinal);
        Assert.Contains("addr: \"203.0.113.10:8443\"", yaml, StringComparison.Ordinal);
        Assert.Contains("mode: \"fast2\"", yaml, StringComparison.Ordinal);
        Assert.Contains("key: \"transport-secret\"", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void IranPreset_BypassesIranAndSendsRemainingTrafficToPaqet()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.IranDirect,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1081, root.GetProperty("inbounds")[0].GetProperty("port").GetInt32());
        Assert.Contains("geosite:category-ir", json, StringComparison.Ordinal);
        Assert.Contains("geoip:ir", json, StringComparison.Ordinal);
        Assert.Contains("geoip:private", json, StringComparison.Ordinal);
        Assert.Equal("paqet", root.GetProperty("routing").GetProperty("rules")[4]
            .GetProperty("outboundTag").GetString());
    }

    [Fact]
    public void NoRegionalPreset_SendsAllDestinationsToPaqet()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: false,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false));

        Assert.DoesNotContain("category-ir", json, StringComparison.Ordinal);
        Assert.DoesNotContain("geoip:ir", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var rules = document.RootElement.GetProperty("routing").GetProperty("rules");
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal("paqet", rules[0].GetProperty("outboundTag").GetString());
    }

    [Fact]
    public void OptionalXrayRules_AreExplicitAndOrderedBeforeTheCatchAll()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPOnDemand,
            BypassLan: false,
            BlockAds: true,
            BlockQuic: true,
            DirectBitTorrent: true));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("IPOnDemand", root.GetProperty("routing").GetProperty("domainStrategy").GetString());
        var rules = root.GetProperty("routing").GetProperty("rules");
        Assert.Equal("block", rules[0].GetProperty("outboundTag").GetString());
        Assert.Equal("443", rules[1].GetProperty("port").GetString());
        Assert.Equal("direct", rules[2].GetProperty("outboundTag").GetString());
        Assert.Equal("paqet", rules[3].GetProperty("outboundTag").GetString());
    }

    [Fact]
    public void CustomDirectDestinations_NormalizeDomainsWildcardsIpsAndCidrs()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: false,
            BlockAds: true,
            BlockQuic: true,
            DirectBitTorrent: false,
            DirectRouteDestinations:
            [
                "Example.COM",
                "*.another-example.com",
                "203.0.113.10",
                "2001:db8::/32",
            ]));

        using var document = JsonDocument.Parse(json);
        var rules = document.RootElement.GetProperty("routing").GetProperty("rules");
        var domainRule = rules[0];
        Assert.Equal("direct", domainRule.GetProperty("outboundTag").GetString());
        Assert.Equal(
            ["domain:example.com", "domain:another-example.com"],
            domainRule.GetProperty("domain").EnumerateArray().Select(value => value.GetString()!).ToArray());

        var ipRule = rules[1];
        Assert.Equal("direct", ipRule.GetProperty("outboundTag").GetString());
        Assert.Equal(
            ["203.0.113.10", "2001:db8::/32"],
            ipRule.GetProperty("ip").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal("block", rules[2].GetProperty("outboundTag").GetString());
        Assert.Equal("paqet", rules[rules.GetArrayLength() - 1].GetProperty("outboundTag").GetString());
    }

    [Theory]
    [InlineData("foo.*.example.com")]
    [InlineData("https://example.com")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("*.127.0.0.1")]
    public void CustomDirectDestinations_RejectInvalidEntries(string entry)
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            DirectRouteDestinations = [entry],
        };

        Assert.Contains(
            PaqetFireSettingsValidator.Validate(settings),
            error => error.Contains("direct-route destination", StringComparison.OrdinalIgnoreCase));

        var policy = new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: false,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false,
            DirectRouteDestinations: [entry]);
        Assert.Throws<ConfigurationValidationException>(() =>
            new XrayJsonConfigurationWriter().Write(policy));
    }

    [Fact]
    public void LanSharing_AddsASeparateAuthenticatedListenerOnTheLanAddress()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false,
            new LanSocksShare("192.168.50.12", 2082, "family", "correct-horse")));

        using var document = JsonDocument.Parse(json);
        var inbounds = document.RootElement.GetProperty("inbounds");
        Assert.Equal(2, inbounds.GetArrayLength());
        var shared = inbounds[1];
        Assert.Equal("lan-share-in", shared.GetProperty("tag").GetString());
        Assert.Equal("192.168.50.12", shared.GetProperty("listen").GetString());
        Assert.Equal(2082, shared.GetProperty("port").GetInt32());
        var settings = shared.GetProperty("settings");
        Assert.Equal("password", settings.GetProperty("auth").GetString());
        Assert.Equal("family", settings.GetProperty("accounts")[0].GetProperty("user").GetString());
        Assert.Equal("correct-horse", settings.GetProperty("accounts")[0].GetProperty("pass").GetString());
        Assert.True(settings.GetProperty("udp").GetBoolean());
        Assert.Equal("192.168.50.12", settings.GetProperty("ip").GetString());
    }

    [Fact]
    public void XraySniffing_UsesOnlyProtocolsAcceptedByTheBundledEngine()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: false,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: true,
            new LanSocksShare("192.168.50.12", 2082, "family", "correct-horse")));

        using var document = JsonDocument.Parse(json);
        foreach (var inbound in document.RootElement.GetProperty("inbounds").EnumerateArray())
        {
            var overrides = inbound.GetProperty("sniffing").GetProperty("destOverride")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();

            Assert.Equal(["http", "tls", "quic"], overrides);
            Assert.DoesNotContain("bittorrent", overrides);
        }

        Assert.Contains("\"protocol\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"bittorrent\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void XraySniffing_OverridesIpDestinationsWithTheRecoveredDomain()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: false,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false,
            new LanSocksShare("192.168.50.12", 2082, "family", "correct-horse")));

        using var document = JsonDocument.Parse(json);
        foreach (var inbound in document.RootElement.GetProperty("inbounds").EnumerateArray())
        {
            Assert.False(inbound.GetProperty("sniffing").GetProperty("routeOnly").GetBoolean());
        }
    }

    [Fact]
    public void LanSharing_RequiresAValidPortUsernameAndPassword()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            ShareWithLan = true,
            LanSocksPort = 1081,
            LanSocksUsername = string.Empty,
            LanSocksPassword = "short",
        };

        var errors = PaqetFireSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("port", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("username", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LanSharing_RejectsAPublicListenAddress()
    {
        var policy = new XrayRoutingPolicy(
            RegionalRoutingPreset.None,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false,
            new LanSocksShare("203.0.113.10", 2082, "family", "correct-horse"));

        Assert.Throws<ConfigurationValidationException>(() =>
            new XrayJsonConfigurationWriter().Write(policy));
    }
}
