using System.Text.Json;
using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class EncryptedDnsTests
{
    private static readonly EngineSocksCredentials Credentials = new("engine-user", "engine-password");

    [Fact]
    public void EncryptedDnsAnswersCapturedLookupsAndPinsResolverTrafficFirst()
    {
        using var document = Write(Policy(
            new XrayDnsPolicy(XrayDnsResolver.Cloudflare, ["192.168.1.1"]),
            RegionalRoutingPreset.IranDirect,
            directDestinations: ["example.com"],
            lanShare: new LanSocksShare("192.168.1.20", 2082, "family", "correct-horse")));
        var root = document.RootElement;

        var servers = root.GetProperty("dns").GetProperty("servers");
        Assert.Equal("https://1.1.1.1/dns-query", servers[0].GetProperty("address").GetString());
        Assert.Equal("https://1.0.0.1/dns-query", servers[1].GetProperty("address").GetString());
        Assert.All([servers[0], servers[1]], server =>
        {
            Assert.Equal("dns-remote", server.GetProperty("tag").GetString());
            Assert.False(server.TryGetProperty("skipFallback", out _));
        });
        var domestic = servers[2];
        Assert.Equal("192.168.1.1", domestic.GetProperty("address").GetString());
        Assert.True(domestic.GetProperty("skipFallback").GetBoolean());
        Assert.Equal(
            ["geosite:private", "geosite:category-ir", "domain:example.com"],
            Strings(domestic.GetProperty("domains")));
        Assert.Equal("UseIP", root.GetProperty("dns").GetProperty("queryStrategy").GetString());

        var rules = root.GetProperty("routing").GetProperty("rules");
        Assert.Equal(["dns-domestic"], Strings(rules[0].GetProperty("inboundTag")));
        Assert.Equal("direct", rules[0].GetProperty("outboundTag").GetString());
        Assert.Equal(["dns-remote"], Strings(rules[1].GetProperty("inboundTag")));
        Assert.Equal("paqet", rules[1].GetProperty("outboundTag").GetString());
        Assert.Equal(["proxifyre-in", "lan-share-in"], Strings(rules[2].GetProperty("inboundTag")));
        Assert.Equal("53", rules[2].GetProperty("port").GetString());
        Assert.Equal("dns-out", rules[2].GetProperty("outboundTag").GetString());

        var outbounds = root.GetProperty("outbounds").EnumerateArray().ToDictionary(
            outbound => outbound.GetProperty("tag").GetString()!);
        var dnsOut = outbounds["dns-out"];
        // Xray 26 deprecated nonIPQuery; its "skip" is written as rules.
        Assert.False(dnsOut.GetProperty("settings").TryGetProperty("nonIPQuery", out _));
        var dnsRules = dnsOut.GetProperty("settings").GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(("hijack", "1,28"), (dnsRules[0].GetProperty("action").GetString(), dnsRules[0].GetProperty("qType").GetString()));
        Assert.Equal("direct", dnsRules[1].GetProperty("action").GetString());
        Assert.False(dnsRules[1].TryGetProperty("qType", out _));
        Assert.Equal("1.1.1.1", dnsOut.GetProperty("settings").GetProperty("address").GetString());
        Assert.Equal("paqet", dnsOut.GetProperty("streamSettings").GetProperty("sockopt").GetProperty("dialerProxy").GetString());
        Assert.False(outbounds["direct"].TryGetProperty("settings", out _));
        Assert.Equal("UseIP", outbounds["direct"].GetProperty("streamSettings").GetProperty("sockopt").GetProperty("domainStrategy").GetString());
    }

    [Theory]
    [InlineData(XrayDnsResolver.Google, "https://8.8.8.8/dns-query")]
    [InlineData(XrayDnsResolver.Quad9, "https://9.9.9.9/dns-query")]
    public void EveryEncryptedResolverUsesAnIpLiteralEndpoint(XrayDnsResolver resolver, string expected)
    {
        using var document = Write(Policy(new XrayDnsPolicy(resolver, [])));

        Assert.Equal(expected, document.RootElement.GetProperty("dns").GetProperty("servers")[0]
            .GetProperty("address").GetString());
    }

    [Fact]
    public void WithoutANetworkResolverEveryLookupUsesEncryptedDns()
    {
        using var document = Write(Policy(
            new XrayDnsPolicy(XrayDnsResolver.Cloudflare, []),
            RegionalRoutingPreset.IranDirect));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("dns").GetProperty("servers").GetArrayLength());
        var rules = root.GetProperty("routing").GetProperty("rules");
        Assert.Equal(["dns-remote"], Strings(rules[0].GetProperty("inboundTag")));
        Assert.DoesNotContain("dns-domestic", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRouteDirectPresetIsNotAnsweredByTheDomesticResolver()
    {
        using var document = Write(Policy(
            new XrayDnsPolicy(XrayDnsResolver.Cloudflare, ["192.168.1.1"]),
            RegionalRoutingPreset.None));

        var domestic = document.RootElement.GetProperty("dns").GetProperty("servers")[2];
        Assert.Equal(["geosite:private"], Strings(domestic.GetProperty("domains")));
    }

    [Theory]
    [InlineData(true, false, "UseIPv4")]
    [InlineData(false, true, "UseIPv6")]
    public void UnroutedAddressFamiliesAreNotResolved(bool ipv4, bool ipv6, string expected)
    {
        using var document = Write(Policy(new XrayDnsPolicy(XrayDnsResolver.Cloudflare, [], ipv4, ipv6)));

        Assert.Equal(expected, document.RootElement.GetProperty("dns").GetProperty("queryStrategy").GetString());
    }

    [Fact]
    public void SystemResolverKeepsThePreviousConfiguration()
    {
        using var document = Write(Policy(new XrayDnsPolicy(XrayDnsResolver.System, ["192.168.1.1"])));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("dns", out _));
        Assert.DoesNotContain("dns-out", root.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, root.GetProperty("routing").GetProperty("rules").GetArrayLength());
    }

    [Theory]
    [InlineData("router.lan")]
    [InlineData("2001:db8::1")]
    public void DomesticResolversMustBeIpv4Addresses(string server)
    {
        Assert.Throws<ConfigurationValidationException>(() => new XrayJsonConfigurationWriter().Write(
            Policy(new XrayDnsPolicy(XrayDnsResolver.Cloudflare, [server]))));
    }

    [Fact]
    public void FakeDnsIssuesIpv4PlaceholdersOnlyForUndirectedLookups()
    {
        using var document = Write(Policy(
            new XrayDnsPolicy(XrayDnsResolver.Cloudflare, ["192.168.1.1"], UseFakeDns: true),
            RegionalRoutingPreset.IranDirect,
            lanShare: new LanSocksShare("192.168.1.20", 2082, "family", "correct-horse")));
        var root = document.RootElement;

        var pool = Assert.Single(root.GetProperty("fakedns").EnumerateArray());
        Assert.Equal("198.18.0.0/15", pool.GetProperty("ipPool").GetString());
        var servers = root.GetProperty("dns").GetProperty("servers");
        Assert.Equal("fakedns", servers[0].GetString());
        Assert.Equal("https://1.1.1.1/dns-query", servers[1].GetProperty("address").GetString());
        Assert.True(servers[3].GetProperty("skipFallback").GetBoolean());
        foreach (var inbound in root.GetProperty("inbounds").EnumerateArray())
        {
            Assert.Equal(["http", "tls", "quic", "fakedns"], Strings(inbound.GetProperty("sniffing").GetProperty("destOverride")));
        }
    }

    [Fact]
    public void FakeDnsIsOffByDefault()
    {
        using var document = Write(Policy(new XrayDnsPolicy(XrayDnsResolver.Cloudflare, [])));

        Assert.False(document.RootElement.TryGetProperty("fakedns", out _));
        Assert.DoesNotContain("\"fakedns\"", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(XrayDnsResolver.System, true)]
    [InlineData(XrayDnsResolver.Cloudflare, false)]
    public void FakeDnsRequiresEncryptedDnsAndIpv4(XrayDnsResolver resolver, bool ipv4)
    {
        Assert.Throws<ConfigurationValidationException>(() => new XrayJsonConfigurationWriter().Write(
            Policy(new XrayDnsPolicy(resolver, [], ResolveIpv4: ipv4, UseFakeDns: true))));
    }

    [Fact]
    public void FakeDnsSettingsRejectCombinationsWherePlaceholdersEscapeTheRoute()
    {
        var valid = new PaqetFireSettings { ServerEndpoint = "server.example:8443", TransportKey = "secret", FakeDns = true };
        Assert.Empty(PaqetFireSettingsValidator.Validate(valid));

        foreach (var invalid in new[]
                 {
                     valid with { DnsResolver = XrayDnsResolver.System },
                     valid with { RouteIpv4 = false },
                     valid with { UserExclusions = ["game.exe"] },
                 })
        {
            Assert.Contains(PaqetFireSettingsValidator.Validate(invalid), error => error.Contains("FakeDNS", StringComparison.Ordinal));
        }

        Assert.Empty(PaqetFireSettingsValidator.Validate(valid with
        {
            RoutingMode = PaqetFire.Core.Routing.RoutingMode.SelectedApplications,
            SelectedApplications = ["browser.exe"],
            UserExclusions = ["game.exe"],
        }));
    }

    [Fact]
    public void EngineCredentialsProtectBothLoopbackSocksHops()
    {
        using var document = Write(Policy(dns: null) with { EngineCredentials = Credentials });
        var root = document.RootElement;

        var inbound = root.GetProperty("inbounds")[0].GetProperty("settings");
        Assert.Equal("password", inbound.GetProperty("auth").GetString());
        Assert.Equal("engine-user", inbound.GetProperty("accounts")[0].GetProperty("user").GetString());
        Assert.Equal("engine-password", inbound.GetProperty("accounts")[0].GetProperty("pass").GetString());
        Assert.True(inbound.GetProperty("udp").GetBoolean());

        var paqet = root.GetProperty("outbounds")[0].GetProperty("settings").GetProperty("servers")[0];
        Assert.Equal("engine-user", paqet.GetProperty("users")[0].GetProperty("user").GetString());
        Assert.Equal("engine-password", paqet.GetProperty("users")[0].GetProperty("pass").GetString());
    }

    [Fact]
    public void GeneratedEngineCredentialsAreUniqueAndNotPrinted()
    {
        var first = EngineSocksCredentials.Generate();
        var second = EngineSocksCredentials.Generate();

        Assert.NotEqual(first.Password, second.Password);
        Assert.Equal(32, first.Password.Length);
        Assert.DoesNotContain(first.Password, first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PaqetDefaultsLogWarningsAndWidenTheKcpWindows()
    {
        var profile = new PaqetAdvancedOptions().ApplyTo(new PaqetProfile(
            "203.0.113.10:8443", "127.0.0.1:1080", "Ethernet", "7b35c9bb-cf61-4c14-9f3d-53b7468c7cc8",
            "192.168.1.20", "aa:bb:cc:dd:ee:ff", ["PA"], ["PA"])) with
        {
            SocksUsername = Credentials.Username,
            SocksPassword = Credentials.Password,
        };

        var yaml = new PaqetYamlConfigurationWriter().Write(profile, "transport-secret");

        Assert.Contains("level: \"warn\"", yaml, StringComparison.Ordinal);
        Assert.Contains("rcvwnd: 1024", yaml, StringComparison.Ordinal);
        Assert.Contains("sndwnd: 512", yaml, StringComparison.Ordinal);
        Assert.Contains("username: \"engine-user\"", yaml, StringComparison.Ordinal);
        Assert.Contains("password: \"engine-password\"", yaml, StringComparison.Ordinal);
    }

    private static XrayRoutingPolicy Policy(
        XrayDnsPolicy? dns,
        RegionalRoutingPreset preset = RegionalRoutingPreset.None,
        IReadOnlyList<string>? directDestinations = null,
        LanSocksShare? lanShare = null) => new(
        preset,
        XrayDomainStrategy.IPIfNonMatch,
        BypassLan: false,
        BlockAds: false,
        BlockQuic: false,
        DirectBitTorrent: false,
        LanShare: lanShare,
        DirectRouteDestinations: directDestinations,
        Dns: dns);

    private static JsonDocument Write(XrayRoutingPolicy policy) =>
        JsonDocument.Parse(new XrayJsonConfigurationWriter().Write(policy));

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(value => value.GetString()!).ToArray();
}
