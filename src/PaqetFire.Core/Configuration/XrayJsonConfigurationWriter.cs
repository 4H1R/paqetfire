using System.Text;
using System.Text.Json;

namespace PaqetFire.Core.Configuration;

public sealed class XrayJsonConfigurationWriter : IXrayConfigurationWriter
{
    public const int InboundPort = 1081;
    public const int PaqetPort = 1080;
    public const int StatsApiPort = 10085;
    public const string PaqetOutboundTag = "paqet";
    public const string ExitOutboundTag = "exit";
    private const string DnsOutboundTag = "dns-out";
    private const string RemoteDnsTag = "dns-remote";
    private const string DomesticDnsTag = "dns-domestic";

    // ProxiFyre's LAN bypass exempts fc00::/7, which holds Xray's default IPv6 pool, so
    // only IPv4 placeholders are issued; AAAA lookups fall through to encrypted DNS.
    public const string FakeDnsIpv4Pool = "198.18.0.0/15";

    public static IReadOnlyList<string> MalwareSiteCategories { get; } =
        ["geosite:malware", "geosite:phishing", "geosite:cryptominers"];

    public static IReadOnlyList<string> MalwareIpCategories { get; } =
        ["geoip:malware", "geoip:phishing"];

    public string Write(XrayRoutingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.RegionalPreset))
        {
            throw new ConfigurationValidationException(["The regional routing preset is invalid."]);
        }
        if (!Enum.IsDefined(policy.DomainStrategy))
        {
            throw new ConfigurationValidationException(["The Xray domain strategy is invalid."]);
        }

        if (policy.Exit is SocksExitOutbound { Port: < 1024 or > 65535 or PaqetPort or InboundPort or StatsApiPort })
        {
            throw new ConfigurationValidationException(["The exit proxy port is invalid."]);
        }

        if (policy.Exit is InterfaceExitOutbound { InterfaceName: var adapter } &&
            (string.IsNullOrWhiteSpace(adapter) || adapter.Length > 64 || adapter.Any(char.IsControl)))
        {
            throw new ConfigurationValidationException(["The exit proxy adapter name is invalid."]);
        }

        var directDestinations = ParseDirectDestinations(policy.DirectRouteDestinations);
        var dns = ValidateDns(policy.Dns);

        if (policy.LanShare is not null && policy.HotspotShare is not null &&
            policy.LanShare.Port == policy.HotspotShare.Port)
        {
            throw new ConfigurationValidationException(["The hotspot port must differ from the LAN share port."]);
        }

        var directDomains = directDestinations
            .Where(destination => destination.Kind == DirectRouteDestinationKind.Domain)
            .Select(destination => destination.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directIps = directDestinations
            .Where(destination => destination.Kind == DirectRouteDestinationKind.Ip)
            .Select(destination => destination.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var userInboundTags = new List<string> { "proxifyre-in" };
        if (policy.LanShare is not null) userInboundTags.Add("lan-share-in");
        if (policy.HotspotShare is not null) userInboundTags.Add("hotspot-share-in");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("log");
            writer.WriteString("loglevel", "warning");
            writer.WriteEndObject();

            if (dns is not null)
            {
                WriteDnsModule(writer, dns, policy.RegionalPreset, directDomains);
            }

            if (dns?.UseFakeDns == true)
            {
                writer.WriteStartArray("fakedns");
                writer.WriteStartObject();
                writer.WriteString("ipPool", FakeDnsIpv4Pool);
                writer.WriteNumber("poolSize", 65535);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            // Loopback-only StatsService exposes traffic counters for the live
            // connection card. HandlerService is deliberately not enabled.
            writer.WriteStartObject("api");
            writer.WriteString("tag", "api");
            writer.WriteString("listen", $"127.0.0.1:{StatsApiPort}");
            writer.WriteStartArray("services");
            writer.WriteStringValue("StatsService");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("stats");
            writer.WriteEndObject();
            writer.WriteStartObject("policy");
            writer.WriteStartObject("system");
            writer.WriteBoolean("statsOutboundUplink", true);
            writer.WriteBoolean("statsOutboundDownlink", true);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartArray("inbounds");
            writer.WriteStartObject();
            writer.WriteString("tag", "proxifyre-in");
            writer.WriteString("listen", "127.0.0.1");
            writer.WriteNumber("port", InboundPort);
            writer.WriteString("protocol", "socks");
            writer.WriteStartObject("settings");
            if (policy.EngineCredentials is { } engineCredentials)
            {
                WritePasswordAuthentication(writer, engineCredentials.Username, engineCredentials.Password);
            }
            else
            {
                writer.WriteString("auth", "noauth");
            }

            writer.WriteBoolean("udp", true);
            writer.WriteEndObject();
            writer.WriteStartObject("sniffing");
            writer.WriteBoolean("enabled", true);
            writer.WriteStartArray("destOverride");
            writer.WriteStringValue("http");
            writer.WriteStringValue("tls");
            writer.WriteStringValue("quic");
            WriteFakeDnsOverride(writer, dns);
            writer.WriteEndArray();
            // ProxiFyre can only forward the resolved IP address. Let Xray replace
            // that address with a hostname recovered from HTTP/TLS/QUIC so the
            // Paqet SOCKS hop performs remote DNS resolution. Keeping routeOnly
            // enabled breaks destinations whose locally resolved IP is synthetic
            // or unusable from the remote Paqet endpoint (for example YouTube).
            writer.WriteBoolean("routeOnly", false);
            writer.WriteEndObject();
            writer.WriteEndObject();

            if (policy.LanShare is { } lanShare)
            {
                ValidateLanShare(lanShare);
                writer.WriteStartObject();
                writer.WriteString("tag", "lan-share-in");
                writer.WriteString("listen", lanShare.ListenAddress);
                writer.WriteNumber("port", lanShare.Port);
                writer.WriteString("protocol", "socks");
                writer.WriteStartObject("settings");
                WritePasswordAuthentication(writer, lanShare.Username, lanShare.Password);
                writer.WriteBoolean("udp", true);
                writer.WriteString("ip", lanShare.ListenAddress);
                writer.WriteEndObject();
                writer.WriteStartObject("sniffing");
                writer.WriteBoolean("enabled", true);
                writer.WriteStartArray("destOverride");
                writer.WriteStringValue("http");
                writer.WriteStringValue("tls");
                writer.WriteStringValue("quic");
                WriteFakeDnsOverride(writer, dns);
                writer.WriteEndArray();
                writer.WriteBoolean("routeOnly", false);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            if (policy.HotspotShare is { } hotspotShare)
            {
                ValidateLanShare(hotspotShare);
                writer.WriteStartObject();
                writer.WriteString("tag", "hotspot-share-in");
                writer.WriteString("listen", hotspotShare.ListenAddress);
                writer.WriteNumber("port", hotspotShare.Port);
                writer.WriteString("protocol", "socks");
                writer.WriteStartObject("settings");
                WritePasswordAuthentication(writer, hotspotShare.Username, hotspotShare.Password);
                writer.WriteBoolean("udp", true);
                writer.WriteString("ip", hotspotShare.ListenAddress);
                writer.WriteEndObject();
                writer.WriteStartObject("sniffing");
                writer.WriteBoolean("enabled", true);
                writer.WriteStartArray("destOverride");
                writer.WriteStringValue("http");
                writer.WriteStringValue("tls");
                writer.WriteStringValue("quic");
                WriteFakeDnsOverride(writer, dns);
                writer.WriteEndArray();
                writer.WriteBoolean("routeOnly", false);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("outbounds");
            WritePaqetOutbound(writer, policy.EngineCredentials);
            if (policy.Exit is { } exit)
            {
                WriteExitOutbound(writer, exit);
            }

            if (dns is null)
            {
                WriteSimpleOutbound(writer, "direct", "freedom");
            }
            else
            {
                // Resolve direct destinations with Xray's DNS policy; the Windows resolver
                // would send the lookup back through capture or out in plaintext.
                writer.WriteStartObject();
                writer.WriteString("tag", "direct");
                writer.WriteString("protocol", "freedom");
                writer.WriteStartObject("settings");
                writer.WriteString("domainStrategy", "UseIP");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            WriteSimpleOutbound(writer, "block", "blackhole");
            if (dns is not null)
            {
                // A and AAAA queries are answered by the DNS module. Other types, such as
                // SRV, TXT, or MX, stay plain DNS but are sent to the provider through Paqet:
                // the original resolver may be a LAN address unreachable from the server.
                writer.WriteStartObject();
                writer.WriteString("tag", DnsOutboundTag);
                writer.WriteString("protocol", "dns");
                writer.WriteStartObject("settings");
                writer.WriteString("nonIPQuery", "skip");
                writer.WriteString("address", PlainDnsAddress(dns.Resolver));
                writer.WriteNumber("port", 53);
                writer.WriteEndObject();
                writer.WriteStartObject("streamSettings");
                writer.WriteStartObject("sockopt");
                writer.WriteString("dialerProxy", PaqetOutboundTag);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("routing");
            writer.WriteString("domainStrategy", policy.DomainStrategy.ToString());
            writer.WriteStartArray("rules");
            if (dns is not null)
            {
                WriteDnsRules(writer, dns, userInboundTags);
            }

            if (directDomains.Length > 0)
            {
                WriteRule(writer, "domain", directDomains, "direct");
            }
            if (directIps.Length > 0)
            {
                WriteRule(writer, "ip", directIps, "direct");
            }

            if (policy.BlockAds)
            {
                WriteRule(writer, "domain", ["geosite:category-ads-all"], "block");
            }

            // These categories exist only in updated routing data; the broker sets
            // BlockMalware only when the installed data provides them.
            if (policy.BlockMalware)
            {
                WriteRule(writer, "domain", MalwareSiteCategories, "block");
                WriteRule(writer, "ip", MalwareIpCategories, "block");
            }

            if (policy.DirectBitTorrent)
            {
                WriteRule(writer, "protocol", ["bittorrent"], "direct");
            }

            if (policy.BypassLan)
            {
                WriteRule(writer, "domain", ["geosite:private"], "direct");
                WriteRule(writer, "ip", ["geoip:private"], "direct");
            }

            if (policy.RegionalPreset == RegionalRoutingPreset.IranDirect)
            {
                WriteRule(writer, "domain", ["geosite:category-ir"], "direct");
                WriteRule(writer, "ip", ["geoip:ir"], "direct");
            }

            // After every direct rule, so only QUIC headed into the tunnel is blocked;
            // direct destinations such as Iranian sites keep HTTP/3.
            if (policy.BlockQuic)
            {
                WriteNetworkRule(writer, "udp", "443", "block");
            }

            if (policy.Exit is { CarriesUdp: false })
            {
                // The exit relays TCP only, so UDP still leaves from the Paqet server.
                WriteNetworkRule(writer, "tcp", port: null, ExitOutboundTag);
                WriteNetworkRule(writer, "udp", port: null, PaqetOutboundTag);
            }
            else
            {
                WriteNetworkRule(writer, "tcp,udp", port: null, policy.Exit is null ? PaqetOutboundTag : ExitOutboundTag);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static IReadOnlyList<DirectRouteDestination> ParseDirectDestinations(
        IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var destinations = new List<DirectRouteDestination>(values.Count);
        foreach (var value in values)
        {
            if (!DirectRouteDestination.TryParse(value, out var destination))
            {
                throw new ConfigurationValidationException(
                    ["Every direct-route destination must be a domain, *.domain wildcard, IP address, or CIDR range."]);
            }

            destinations.Add(destination);
        }

        return destinations;
    }

    private static void ValidateLanShare(LanSocksShare share)
    {
        if (!System.Net.IPAddress.TryParse(share.ListenAddress, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            System.Net.IPAddress.IsLoopback(address) ||
            address.Equals(System.Net.IPAddress.Any) ||
            !IsPrivateIpv4(address))
        {
            throw new ConfigurationValidationException(["The shared SOCKS5 listener requires a specific private LAN IPv4 address."]);
        }

        if (share.Port is < 1024 or > 65535 || share.Port is PaqetPort or InboundPort or PsiphonJsonConfigurationWriter.LocalSocksPort)
        {
            throw new ConfigurationValidationException(["The shared SOCKS5 port is invalid."]);
        }

        if (string.IsNullOrWhiteSpace(share.Username) || string.IsNullOrEmpty(share.Password))
        {
            throw new ConfigurationValidationException(["The shared SOCKS5 listener requires authentication."]);
        }
    }

    private static bool IsPrivateIpv4(System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>
    /// The exit engine's own entry point. Either way the engine reaches its network only
    /// through Paqet, so nothing routed to the exit leaves the computer directly.
    /// </summary>
    private static void WriteExitOutbound(Utf8JsonWriter writer, ExitProxyOutbound exit)
    {
        writer.WriteStartObject();
        writer.WriteString("tag", ExitOutboundTag);
        switch (exit)
        {
            case SocksExitOutbound socks:
                writer.WriteString("protocol", "socks");
                writer.WriteStartObject("settings");
                writer.WriteStartArray("servers");
                writer.WriteStartObject();
                writer.WriteString("address", "127.0.0.1");
                writer.WriteNumber("port", socks.Port);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            case InterfaceExitOutbound adapter:
                // Sockets bound to the engine's adapter fail while its tunnel is down instead
                // of following the default route. The tunnel is IPv4 only, so names resolve
                // through Xray's DNS to IPv4, and fail rather than fall back to the system resolver.
                writer.WriteString("protocol", "freedom");
                writer.WriteStartObject("settings");
                writer.WriteString("domainStrategy", "ForceIPv4");
                writer.WriteEndObject();
                writer.WriteStartObject("streamSettings");
                writer.WriteStartObject("sockopt");
                writer.WriteString("interface", adapter.InterfaceName);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            default:
                throw new ConfigurationValidationException(["The exit proxy is invalid."]);
        }

        writer.WriteEndObject();
    }

    private static void WriteNetworkRule(
        Utf8JsonWriter writer,
        string network,
        string? port,
        string outboundTag)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "field");
        writer.WriteString("network", network);
        if (port is not null)
        {
            writer.WriteString("port", port);
        }

        writer.WriteString("outboundTag", outboundTag);
        writer.WriteEndObject();
    }

    private static XrayDnsPolicy? ValidateDns(XrayDnsPolicy? dns)
    {
        if (dns is null)
        {
            return null;
        }

        if (!Enum.IsDefined(dns.Resolver))
        {
            throw new ConfigurationValidationException(["The DNS resolver is invalid."]);
        }

        if (!dns.ResolveIpv4 && !dns.ResolveIpv6)
        {
            throw new ConfigurationValidationException(["DNS must resolve at least one address family."]);
        }

        if (dns.UseFakeDns && (dns.Resolver == XrayDnsResolver.System || !dns.ResolveIpv4))
        {
            throw new ConfigurationValidationException(["FakeDNS requires encrypted DNS and IPv4 routing."]);
        }

        if (dns.DomesticServers.Any(server =>
                !System.Net.IPAddress.TryParse(server, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            throw new ConfigurationValidationException(["Every domestic DNS server must be an IPv4 address."]);
        }

        return dns.Resolver == XrayDnsResolver.System ? null : dns;
    }

    private static IReadOnlyList<string> RemoteDnsAddresses(XrayDnsResolver resolver) => resolver switch
    {
        // IP-literal endpoints need no bootstrap lookup; each certificate covers its IP.
        XrayDnsResolver.Cloudflare => ["https://1.1.1.1/dns-query", "https://1.0.0.1/dns-query"],
        XrayDnsResolver.Google => ["https://8.8.8.8/dns-query", "https://8.8.4.4/dns-query"],
        XrayDnsResolver.Quad9 => ["https://9.9.9.9/dns-query", "https://149.112.112.112/dns-query"],
        _ => throw new ConfigurationValidationException(["The DNS resolver is invalid."]),
    };

    private static string PlainDnsAddress(XrayDnsResolver resolver) => resolver switch
    {
        XrayDnsResolver.Cloudflare => "1.1.1.1",
        XrayDnsResolver.Google => "8.8.8.8",
        XrayDnsResolver.Quad9 => "9.9.9.9",
        _ => throw new ConfigurationValidationException(["The DNS resolver is invalid."]),
    };

    private static void WriteDnsModule(
        Utf8JsonWriter writer,
        XrayDnsPolicy dns,
        RegionalRoutingPreset regionalPreset,
        IReadOnlyList<string> directDomains)
    {
        writer.WriteStartObject("dns");
        writer.WriteStartArray("servers");
        if (dns.UseFakeDns)
        {
            // First among servers without domains, so it answers every lookup that is
            // not pinned to the domestic resolver. Xray's own lookups always skip it.
            writer.WriteStringValue("fakedns");
        }

        foreach (var address in RemoteDnsAddresses(dns.Resolver))
        {
            writer.WriteStartObject();
            writer.WriteString("address", address);
            writer.WriteString("tag", RemoteDnsTag);
            writer.WriteEndObject();
        }

        // Local names and direct-routed destinations keep the network's own answers,
        // such as Iranian CDN edges; these lookups reveal nothing the direct
        // connection does not. Everything else never reaches this resolver.
        var domesticDomains = new List<string> { "geosite:private" };
        if (regionalPreset == RegionalRoutingPreset.IranDirect)
        {
            domesticDomains.Add("geosite:category-ir");
        }

        domesticDomains.AddRange(directDomains);
        foreach (var server in dns.DomesticServers.Distinct(StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("address", server);
            writer.WriteNumber("port", 53);
            writer.WriteStartArray("domains");
            foreach (var domain in domesticDomains)
            {
                writer.WriteStringValue(domain);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("skipFallback", true);
            writer.WriteString("tag", DomesticDnsTag);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("queryStrategy", (dns.ResolveIpv4, dns.ResolveIpv6) switch
        {
            (true, false) => "UseIPv4",
            (false, true) => "UseIPv6",
            _ => "UseIP",
        });
        writer.WriteEndObject();
    }

    private static void WriteDnsRules(
        Utf8JsonWriter writer,
        XrayDnsPolicy dns,
        IReadOnlyList<string> userInboundTags)
    {
        // Resolver traffic must be pinned before user rules: a custom direct IP or
        // regional rule matching a resolver address would otherwise leak queries.
        if (dns.DomesticServers.Count > 0)
        {
            WriteRule(writer, "inboundTag", [DomesticDnsTag], "direct");
        }

        WriteRule(writer, "inboundTag", [RemoteDnsTag], "paqet");

        // Captured lookups (Windows DNS client, LAN and hotspot devices) are answered
        // by Xray instead of reaching their original, typically ISP, resolver.
        writer.WriteStartObject();
        writer.WriteString("type", "field");
        writer.WriteStartArray("inboundTag");
        foreach (var tag in userInboundTags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        writer.WriteString("port", "53");
        writer.WriteString("outboundTag", DnsOutboundTag);
        writer.WriteEndObject();
    }

    private static void WriteFakeDnsOverride(Utf8JsonWriter writer, XrayDnsPolicy? dns)
    {
        if (dns?.UseFakeDns == true)
        {
            writer.WriteStringValue("fakedns");
        }
    }

    private static void WritePasswordAuthentication(Utf8JsonWriter writer, string username, string password)
    {
        writer.WriteString("auth", "password");
        writer.WriteStartArray("accounts");
        writer.WriteStartObject();
        writer.WriteString("user", username);
        writer.WriteString("pass", password);
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private static void WritePaqetOutbound(Utf8JsonWriter writer, EngineSocksCredentials? credentials)
    {
        writer.WriteStartObject();
        writer.WriteString("tag", "paqet");
        writer.WriteString("protocol", "socks");
        writer.WriteStartObject("settings");
        writer.WriteStartArray("servers");
        writer.WriteStartObject();
        writer.WriteString("address", "127.0.0.1");
        writer.WriteNumber("port", PaqetPort);
        if (credentials is not null)
        {
            writer.WriteStartArray("users");
            writer.WriteStartObject();
            writer.WriteString("user", credentials.Username);
            writer.WriteString("pass", credentials.Password);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteSimpleOutbound(Utf8JsonWriter writer, string tag, string protocol)
    {
        writer.WriteStartObject();
        writer.WriteString("tag", tag);
        writer.WriteString("protocol", protocol);
        writer.WriteEndObject();
    }

    private static void WriteRule(
        Utf8JsonWriter writer,
        string property,
        IReadOnlyList<string> values,
        string outboundTag)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "field");
        writer.WriteStartArray(property);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
        writer.WriteString("outboundTag", outboundTag);
        writer.WriteEndObject();
    }
}
