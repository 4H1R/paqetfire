using System.Text;
using System.Text.Json;

namespace PaqetFire.Core.Configuration;

public sealed class XrayJsonConfigurationWriter : IXrayConfigurationWriter
{
    public const int InboundPort = 1081;
    public const int PaqetPort = 1080;

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

        var directDestinations = ParseDirectDestinations(policy.DirectRouteDestinations);

        if (policy.LanShare is not null && policy.HotspotShare is not null &&
            policy.LanShare.Port == policy.HotspotShare.Port)
        {
            throw new ConfigurationValidationException(["The hotspot port must differ from the LAN share port."]);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("log");
            writer.WriteString("loglevel", "warning");
            writer.WriteEndObject();

            writer.WriteStartArray("inbounds");
            writer.WriteStartObject();
            writer.WriteString("tag", "proxifyre-in");
            writer.WriteString("listen", "127.0.0.1");
            writer.WriteNumber("port", InboundPort);
            writer.WriteString("protocol", "socks");
            writer.WriteStartObject("settings");
            writer.WriteString("auth", "noauth");
            writer.WriteBoolean("udp", true);
            writer.WriteEndObject();
            writer.WriteStartObject("sniffing");
            writer.WriteBoolean("enabled", true);
            writer.WriteStartArray("destOverride");
            writer.WriteStringValue("http");
            writer.WriteStringValue("tls");
            writer.WriteStringValue("quic");
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
                writer.WriteString("auth", "password");
                writer.WriteStartArray("accounts");
                writer.WriteStartObject();
                writer.WriteString("user", lanShare.Username);
                writer.WriteString("pass", lanShare.Password);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteBoolean("udp", true);
                writer.WriteString("ip", lanShare.ListenAddress);
                writer.WriteEndObject();
                writer.WriteStartObject("sniffing");
                writer.WriteBoolean("enabled", true);
                writer.WriteStartArray("destOverride");
                writer.WriteStringValue("http");
                writer.WriteStringValue("tls");
                writer.WriteStringValue("quic");
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
                writer.WriteString("auth", "password");
                writer.WriteStartArray("accounts");
                writer.WriteStartObject();
                writer.WriteString("user", hotspotShare.Username);
                writer.WriteString("pass", hotspotShare.Password);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteBoolean("udp", true);
                writer.WriteString("ip", hotspotShare.ListenAddress);
                writer.WriteEndObject();
                writer.WriteStartObject("sniffing");
                writer.WriteBoolean("enabled", true);
                writer.WriteStartArray("destOverride");
                writer.WriteStringValue("http");
                writer.WriteStringValue("tls");
                writer.WriteStringValue("quic");
                writer.WriteEndArray();
                writer.WriteBoolean("routeOnly", false);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("outbounds");
            WritePaqetOutbound(writer);
            WriteSimpleOutbound(writer, "direct", "freedom");
            WriteSimpleOutbound(writer, "block", "blackhole");
            writer.WriteEndArray();

            writer.WriteStartObject("routing");
            writer.WriteString("domainStrategy", policy.DomainStrategy.ToString());
            writer.WriteStartArray("rules");
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

            if (policy.BlockQuic)
            {
                WriteNetworkRule(writer, "udp", "443", "block");
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

            writer.WriteStartObject();
            writer.WriteString("type", "field");
            writer.WriteString("network", "tcp,udp");
            writer.WriteString("outboundTag", "paqet");
            writer.WriteEndObject();
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

        if (share.Port is < 1024 or > 65535 || share.Port is PaqetPort or InboundPort)
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

    private static void WriteNetworkRule(
        Utf8JsonWriter writer,
        string network,
        string port,
        string outboundTag)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "field");
        writer.WriteString("network", network);
        writer.WriteString("port", port);
        writer.WriteString("outboundTag", outboundTag);
        writer.WriteEndObject();
    }

    private static void WritePaqetOutbound(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("tag", "paqet");
        writer.WriteString("protocol", "socks");
        writer.WriteStartObject("settings");
        writer.WriteStartArray("servers");
        writer.WriteStartObject();
        writer.WriteString("address", "127.0.0.1");
        writer.WriteNumber("port", PaqetPort);
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
