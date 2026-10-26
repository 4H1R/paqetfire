using System.Text;
using System.Text.Json;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Profiles;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class OpenVpnExitTests
{
    // Shaped like a Proton VPN Windows TCP download, including the Linux DNS scripts it ships with.
    private const string ProviderConfiguration = """
        # ProtonVPN TCP configuration
        client
        dev tun
        proto tcp
        remote 185.159.157.1 443
        remote 185.159.157.2 7770
        remote-random
        resolv-retry infinite
        nobind
        cipher AES-256-GCM
        setenv CLIENT_CERT 0
        tun-mtu 1500
        mssfix 0
        persist-key
        persist-tun
        reneg-sec 0
        remote-cert-tls server
        auth-user-pass
        pull
        fast-io
        script-security 2
        up /etc/openvpn/update-resolv-conf
        down /etc/openvpn/update-resolv-conf
        redirect-gateway def1
        block-outside-dns
        <ca>
        -----BEGIN CERTIFICATE-----
        MIIFnTCCA4WgAwIBAgIUCI574SM3Lyh47GyNl0WAOYrqb5QwDQYJKoZIhvcNAQEL
        -----END CERTIFICATE-----
        </ca>
        key-direction 1
        <tls-crypt>
        -----BEGIN OpenVPN Static key V1-----
        6acef03f62675b4b1bbd03e53b187727
        -----END OpenVPN Static key V1-----
        </tls-crypt>
        """;

    private const string MinimalBlocks = """
        <ca>
        -----BEGIN CERTIFICATE-----
        AAAA
        -----END CERTIFICATE-----
        </ca>
        """;

    [Fact]
    public void AProviderConfigurationKeepsItsClientOptionsAndDropsTheRest()
    {
        var configuration = OpenVpnConfiguration.Parse(ProviderConfiguration);

        Assert.True(configuration.IsValid, string.Join(" ", configuration.Errors));
        Assert.True(configuration.RequiresCredentials);
        Assert.Equal(new OpenVpnRemote("185.159.157.1", 443), configuration.Remote);
        Assert.Equal(2, configuration.Remotes.Count);
        Assert.Equal(["setenv", "script-security", "up", "down", "redirect-gateway", "block-outside-dns"], configuration.IgnoredOptions);
    }

    [Fact]
    public void TheRuntimeConfigurationReachesTheServerOnlyThroughAuthenticatedPaqet()
    {
        var text = Write(ProviderConfiguration, userCredentials: @"C:\Program Files\PaqetFire\payload\engines\openvpn\credentials.txt");
        var lines = Lines(text);

        Assert.Contains("proto tcp-client", lines);
        Assert.Contains("socks-proxy 127.0.0.1 1080 \"C:/Program Files/PaqetFire/payload/engines/openvpn/socks-auth.txt\"", lines);
        Assert.Contains("auth-user-pass \"C:/Program Files/PaqetFire/payload/engines/openvpn/credentials.txt\"", lines);
        Assert.Contains("remote 185.159.157.1 443", lines);
        Assert.Contains("remote 185.159.157.2 7770", lines);
        Assert.Contains("cipher AES-256-GCM", lines);
        Assert.Contains("key-direction 1", lines);
        Assert.Contains("<tls-crypt>", lines);
        Assert.Contains("6acef03f62675b4b1bbd03e53b187727", lines);
    }

    [Fact]
    public void TheRuntimeConfigurationChangesNothingOutsideItsAdapter()
    {
        var lines = Lines(Write(ProviderConfiguration));

        Assert.Contains("dev-node \"PaqetFire Exit\"", lines);
        Assert.Contains("windows-driver tap-windows6", lines);
        Assert.Contains("script-security 1", lines);
        Assert.Contains("auth-retry none", lines);
        Assert.Contains("route-nopull", lines);
        Assert.Contains("pull-filter ignore dns", lines);
        Assert.Contains("pull-filter ignore redirect-private", lines);
        Assert.Contains("pull-filter ignore redirect-gateway", lines);
        Assert.Contains("pull-filter ignore \"route \"", lines);
        Assert.Contains("pull-filter ignore dhcp-option", lines);
        Assert.Contains("pull-filter ignore block-outside-dns", lines);
        // The only route: a default route on the exit adapter that ordinary traffic never prefers.
        Assert.Equal(["route 0.0.0.0 0.0.0.0 vpn_gateway 9999"], lines.Where(line => line.StartsWith("route ", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, line => line.StartsWith("redirect-gateway", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("block-outside-dns", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("up \"C:\\\\Windows\\\\System32\\\\cmd.exe /c whoami\"")]
    [InlineData("down evil.bat")]
    [InlineData("route-up evil.bat")]
    [InlineData("plugin C:\\evil.dll")]
    [InlineData("tls-verify evil.bat")]
    [InlineData("auth-user-pass-verify evil.bat via-env")]
    [InlineData("learn-address evil.bat")]
    [InlineData("ipchange evil.bat")]
    [InlineData("client-connect evil.bat")]
    [InlineData("script-security 3")]
    [InlineData("management 0.0.0.0 7505")]
    [InlineData("log C:\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("log-append C:\\Windows\\win.ini")]
    [InlineData("status C:\\Windows\\win.ini")]
    [InlineData("writepid C:\\Windows\\win.ini")]
    [InlineData("config C:\\other.ovpn")]
    [InlineData("cd C:\\Windows")]
    [InlineData("--up evil.bat")]
    [InlineData("UP evil.bat")]
    public void NothingThatRunsProgramsOrWritesFilesReachesOpenVpn(string option)
    {
        var text = Write($"client\nproto tcp\nremote vpn.example.net 443\n{option}\n{MinimalBlocks}");

        Assert.DoesNotContain("evil", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("win.ini", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hosts", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7505", text, StringComparison.Ordinal);
        Assert.Contains("script-security 1", Lines(text));
        Assert.DoesNotContain(Lines(text), line => line.StartsWith("script-security", StringComparison.Ordinal) && line != "script-security 1");
    }

    [Fact]
    public void AnArgumentCannotSmuggleASecondOption()
    {
        var text = Write($"client\nproto tcp\nremote vpn.example.net 443\ncipher \"AES-256-GCM up evil.bat\"\n{MinimalBlocks}");

        Assert.Contains("cipher \"AES-256-GCM up evil.bat\"", Lines(text));
        Assert.DoesNotContain(Lines(text), line => line.StartsWith("up ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("proto udp")]
    [InlineData("proto udp4")]
    [InlineData("")]
    public void AUdpServerIsRefused(string protocol)
    {
        var configuration = OpenVpnConfiguration.Parse($"client\n{protocol}\nremote vpn.example.net 1194\n{MinimalBlocks}");

        Assert.Contains(configuration.Errors, error => error.Contains("TCP version", StringComparison.Ordinal));
    }

    [Fact]
    public void RemotesThatNameTcpNeedNoProtoLine()
    {
        var configuration = OpenVpnConfiguration.Parse($"client\nremote a.example.net 443 tcp\nremote b.example.net tcp-client\n{MinimalBlocks}");

        Assert.Contains(configuration.Errors, error => error.Contains("invalid port", StringComparison.Ordinal));
        configuration = OpenVpnConfiguration.Parse($"client\nport 8443\nremote a.example.net 443 tcp\nremote b.example.net 8443 tcp-client\n{MinimalBlocks}");
        Assert.True(configuration.IsValid, string.Join(" ", configuration.Errors));
    }

    [Fact]
    public void ARemoteWithoutAPortUsesThePortOption()
    {
        var configuration = OpenVpnConfiguration.Parse($"client\nproto tcp\nport 8443\nremote vpn.example.net\n{MinimalBlocks}");

        Assert.Equal(new OpenVpnRemote("vpn.example.net", 8443), configuration.Remote);
    }

    [Theory]
    [InlineData("ca ca.crt", "points to a file")]
    [InlineData("tls-auth ta.key 1", "points to a file")]
    [InlineData("dev tap", "dev tun")]
    [InlineData("<connection>\nremote a.example.net 443\n</connection>", "<connection>")]
    [InlineData("<cert>\nAAAA", "not closed")]
    public void ConfigurationsPaqetFireCannotRunAreExplained(string line, string expected)
    {
        var configuration = OpenVpnConfiguration.Parse($"client\nproto tcp\nremote vpn.example.net 443\n{line}\n{MinimalBlocks}");

        Assert.Contains(configuration.Errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AConfigurationNeedsAServerAndItsCertificateAuthority()
    {
        var configuration = OpenVpnConfiguration.Parse("client\nproto tcp\n");

        Assert.Contains(configuration.Errors, error => error.Contains("names no server", StringComparison.Ordinal));
        Assert.Contains(configuration.Errors, error => error.Contains("<ca>", StringComparison.Ordinal));
        Assert.False(OpenVpnConfiguration.Parse(new string('a', OpenVpnConfiguration.MaximumLength + 1)).IsValid);
    }

    [Fact]
    public void ValidatorAsksForTheAccountTheConfigurationNeeds()
    {
        var errors = PaqetFireSettingsValidator.Validate(ValidSettings() with
        {
            ExitProxy = ExitProxyKind.OpenVpn,
            OpenVpn = new OpenVpnExitOptions(ProviderConfiguration),
        });

        Assert.Contains(errors, error => error.Contains("OpenVPN username", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("OpenVPN password", StringComparison.Ordinal));
        Assert.Empty(PaqetFireSettingsValidator.Validate(ValidSettings() with
        {
            ExitProxy = ExitProxyKind.OpenVpn,
            OpenVpn = new OpenVpnExitOptions(ProviderConfiguration, "user+pmp", "secret"),
            // Unlike Psiphon, the OpenVPN tunnel carries UDP as well.
            RouteTcp = false,
        }));
    }

    [Fact]
    public void ValidatorExplainsAMissingOrUnusableConfiguration()
    {
        Assert.Contains("Import an OpenVPN configuration (.ovpn) to use the OpenVPN exit.",
            PaqetFireSettingsValidator.Validate(ValidSettings() with { ExitProxy = ExitProxyKind.OpenVpn }));
        Assert.Contains(PaqetFireSettingsValidator.Validate(ValidSettings() with
            {
                ExitProxy = ExitProxyKind.OpenVpn,
                OpenVpn = new OpenVpnExitOptions(ProviderConfiguration.Replace("proto tcp", "proto udp", StringComparison.Ordinal), "u", "p"),
            }),
            error => error.StartsWith("OpenVPN configuration:", StringComparison.Ordinal));
    }

    [Fact]
    public void XrayBindsTheOpenVpnExitToItsAdapterForTcpAndUdp()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.IranDirect,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true,
            BlockAds: true,
            BlockQuic: true,
            DirectBitTorrent: true,
            Dns: new XrayDnsPolicy(XrayDnsResolver.Cloudflare, ["192.168.1.1"], UseFakeDns: true),
            EngineCredentials: new EngineSocksCredentials("engine-user", "engine-password"),
            Exit: ExitProxyOutbound.For(ExitProxyKind.OpenVpn)));
        using var document = JsonDocument.Parse(json);

        var exit = Assert.Single(document.RootElement.GetProperty("outbounds").EnumerateArray(),
            outbound => outbound.GetProperty("tag").GetString() == XrayJsonConfigurationWriter.ExitOutboundTag);
        Assert.Equal("freedom", exit.GetProperty("protocol").GetString());
        Assert.Equal("ForceIPv4", exit.GetProperty("settings").GetProperty("domainStrategy").GetString());
        Assert.Equal("PaqetFire Exit", exit.GetProperty("streamSettings").GetProperty("sockopt").GetProperty("interface").GetString());
        var rules = document.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        var last = rules[^1];
        Assert.Equal(("tcp,udp", "exit"), (last.GetProperty("network").GetString(), last.GetProperty("outboundTag").GetString()));
        // Xray stays the only resolver; its encrypted queries leave through the exit as well.
        var remoteDns = Assert.Single(rules, rule =>
            rule.TryGetProperty("inboundTag", out var tags) && tags.EnumerateArray().Any(tag => tag.GetString() == "dns-remote"));
        Assert.Equal("exit", remoteDns.GetProperty("outboundTag").GetString());
    }

    [Fact]
    public void SavedProfilesKeepTheConfigurationAndPasswordEncrypted()
    {
        var catalog = ProfileCatalog.Create(Guid.NewGuid(), ValidSettings() with
        {
            ExitProxy = ExitProxyKind.OpenVpn,
            OpenVpn = new OpenVpnExitOptions(ProviderConfiguration, "vpn-user", "vpn-password"),
        });
        var protector = new ReversingProtector();

        var json = ProfileCatalogJson.WriteProtected(catalog, protector);

        Assert.DoesNotContain("vpn-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("6acef03f62675b4b1bbd03e53b187727", json, StringComparison.Ordinal);
        Assert.Contains("vpn-user", json, StringComparison.Ordinal);
        var restored = ProfileCatalogJson.ReadProtected(json, protector).ActiveProfile.Settings;
        Assert.Equal(new OpenVpnExitOptions(ProviderConfiguration, "vpn-user", "vpn-password"), restored.OpenVpn);
        Assert.Equal(ExitProxyKind.OpenVpn, restored.ExitProxy);
    }

    [Fact]
    public void PortableExportsCarryTheConfigurationButNotThePassword()
    {
        var catalog = ProfileCatalog.Create(Guid.NewGuid(), ValidSettings() with
        {
            ExitProxy = ExitProxyKind.OpenVpn,
            OpenVpn = new OpenVpnExitOptions(ProviderConfiguration, "vpn-user", "vpn-password"),
        });

        var export = ProfileCatalogJson.WritePortableExport(catalog);
        var imported = ProfileCatalogJson.ReadImport(export).ActiveProfile.Settings;

        Assert.DoesNotContain("vpn-password", export, StringComparison.Ordinal);
        Assert.Equal(ExitProxyKind.OpenVpn, imported.ExitProxy);
        Assert.Equal(new OpenVpnExitOptions(ProviderConfiguration, "vpn-user"), imported.OpenVpn);
    }

    [Fact]
    public void TheDesktopSeesWhetherAConfigurationIsSavedButNotItsContents()
    {
        var view = PaqetFireSettingsView.FromSettings(ValidSettings() with
        {
            OpenVpn = new OpenVpnExitOptions(ProviderConfiguration, "vpn-user", "vpn-password"),
        });

        Assert.Equal(new OpenVpnExitView("vpn-user", HasConfiguration: true, HasPassword: true, "185.159.157.1:443"), view.OpenVpn);
        Assert.DoesNotContain("6acef03f62675b4b1bbd03e53b187727", JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    private static string Write(string configuration, string? userCredentials = @"C:\data\credentials.txt")
    {
        var parsed = OpenVpnConfiguration.Parse(configuration);
        Assert.True(parsed.IsValid, string.Join(" ", parsed.Errors));
        return new OpenVpnConfigurationWriter().Write(new OpenVpnClientProfile(
            parsed,
            1080,
            @"C:\Program Files\PaqetFire\payload\engines\openvpn\socks-auth.txt",
            parsed.RequiresCredentials ? userCredentials : null));
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static PaqetFireSettings ValidSettings() => new()
    {
        ServerEndpoint = "server.example:8443",
        TransportKey = "secret",
    };

    private sealed class ReversingProtector : IProfileSecretProtector
    {
        public string Protect(string purpose, string clearText) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(purpose + "|" + new string(clearText.Reverse().ToArray())));

        public string Unprotect(string purpose, string protectedText)
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            Assert.StartsWith(purpose + "|", text, StringComparison.Ordinal);
            return new string(text[(purpose.Length + 1)..].Reverse().ToArray());
        }
    }
}
