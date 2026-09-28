using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PaqetFire.Core.Configuration;

/// <summary>Writes deterministic YAML accepted by Paqet v1.0.0-alpha.21.</summary>
public sealed class PaqetYamlConfigurationWriter : IPaqetConfigurationWriter
{
    public string Write(PaqetProfile profile, string transportKey)
    {
        var errors = PaqetConfigurationValidator.Validate(profile, transportKey);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        _ = PaqetConfigurationValidator.TryParseInterfaceGuid(profile.InterfaceGuid, out var guid);
        var builder = new StringBuilder(1_024);

        AppendScalar(builder, 0, "role", "client");
        builder.Append("log:\n");
        AppendScalar(builder, 2, "level", profile.LogLevel.Trim().ToLowerInvariant());

        builder.Append("socks5:\n");
        AppendScalar(builder, 2, "- listen", NormalizeEndpoint(profile.LocalSocksEndpoint));
        if (!string.IsNullOrEmpty(profile.SocksUsername))
        {
            AppendScalar(builder, 4, "username", profile.SocksUsername);
            AppendScalar(builder, 4, "password", profile.SocksPassword!);
        }

        if (profile.ForwardRules.Count > 0)
        {
            builder.Append("forward:\n");
            foreach (var rule in profile.ForwardRules)
            {
                AppendScalar(builder, 2, "- listen", NormalizeEndpoint(rule.ListenEndpoint));
                AppendScalar(builder, 4, "target", NormalizeEndpoint(rule.TargetEndpoint));
                AppendScalar(builder, 4, "protocol", rule.Protocol.Trim().ToLowerInvariant());
            }
        }

        builder.Append("network:\n");
        AppendScalar(builder, 2, "interface", profile.InterfaceName.Trim());
        AppendScalar(builder, 2, "guid", $@"\Device\NPF_{{{guid:D}}}");
        builder.Append("  ipv4:\n");
        AppendScalar(builder, 4, "addr", $"{IPAddress.Parse(profile.LocalIpv4Address.Trim())}:{profile.LocalIpv4Port}");
        AppendScalar(builder, 4, "router_mac", NormalizeMac(profile.RouterMac));

        if (!string.IsNullOrWhiteSpace(profile.LocalIpv6Endpoint))
        {
            builder.Append("  ipv6:\n");
            AppendScalar(builder, 4, "addr", NormalizeNumericEndpoint(profile.LocalIpv6Endpoint));
            AppendScalar(builder, 4, "router_mac", NormalizeMac(profile.Ipv6RouterMac!));
        }

        builder.Append("  tcp:\n");
        AppendSequence(builder, 4, "local_flag", profile.LocalTcpFlags);
        AppendSequence(builder, 4, "remote_flag", profile.RemoteTcpFlags);
        if (profile.PcapSocketBufferBytes is { } pcapBuffer)
        {
            builder.Append("  pcap:\n");
            AppendNumber(builder, 4, "sockbuf", pcapBuffer);
        }

        builder.Append("server:\n");
        AppendScalar(builder, 2, "addr", NormalizeEndpoint(profile.ServerEndpoint));
        builder.Append("transport:\n");
        AppendScalar(builder, 2, "protocol", "kcp");
        AppendNumber(builder, 2, "conn", profile.ConnectionCount);
        builder.Append("  kcp:\n");
        AppendScalar(builder, 4, "mode", profile.KcpMode.Trim().ToLowerInvariant());

        if (string.Equals(profile.KcpMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            AppendNumber(builder, 4, "nodelay", profile.KcpNoDelay!.Value);
            AppendNumber(builder, 4, "interval", profile.KcpIntervalMilliseconds!.Value);
            AppendNumber(builder, 4, "resend", profile.KcpResend!.Value);
            AppendNumber(builder, 4, "nocongestion", profile.KcpNoCongestion!.Value);
        }

        AppendOptionalBoolean(builder, 4, "wdelay", profile.KcpWriteDelay);
        AppendOptionalBoolean(builder, 4, "acknodelay", profile.KcpAckNoDelay);
        AppendOptionalNumber(builder, 4, "mtu", profile.KcpMtu);
        AppendOptionalNumber(builder, 4, "rcvwnd", profile.KcpReceiveWindow);
        AppendOptionalNumber(builder, 4, "sndwnd", profile.KcpSendWindow);
        AppendScalar(builder, 4, "block", profile.KcpBlock.Trim().ToLowerInvariant());
        AppendScalar(builder, 4, "key", transportKey);
        AppendOptionalNumber(builder, 4, "smuxbuf", profile.SmuxBufferBytes);
        AppendOptionalNumber(builder, 4, "streambuf", profile.StreamBufferBytes);
        AppendOptionalNumber(builder, 4, "smuxkalive", profile.SmuxKeepAliveSeconds);
        AppendOptionalNumber(builder, 4, "smuxktimeout", profile.SmuxKeepAliveTimeoutSeconds);
        AppendOptionalNumber(builder, 4, "dshard", profile.FecDataShards);
        AppendOptionalNumber(builder, 4, "pshard", profile.FecParityShards);
        return builder.ToString();
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        _ = ConfigurationValuePolicy.TryNormalizeEndpoint(endpoint, out var normalized);
        return normalized;
    }

    private static string NormalizeNumericEndpoint(string endpoint)
    {
        var candidate = endpoint.Trim();
        var closingBracket = candidate.IndexOf(']');
        var address = IPAddress.Parse(candidate[1..closingBracket]);
        var port = int.Parse(candidate[(closingBracket + 2)..], CultureInfo.InvariantCulture);
        return $"[{address}]:{port}";
    }

    private static string NormalizeMac(string value) =>
        value.Trim().Replace('-', ':').ToLowerInvariant();

    private static void AppendScalar(StringBuilder builder, int indentation, string key, string value)
    {
        builder.Append(' ', indentation).Append(key).Append(": ")
            .Append(JsonSerializer.Serialize(value)).Append('\n');
    }

    private static void AppendNumber(StringBuilder builder, int indentation, string key, int value)
    {
        builder.Append(' ', indentation).Append(key).Append(": ")
            .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void AppendOptionalNumber(
        StringBuilder builder,
        int indentation,
        string key,
        int? value)
    {
        if (value is { } actual)
        {
            AppendNumber(builder, indentation, key, actual);
        }
    }

    private static void AppendOptionalBoolean(
        StringBuilder builder,
        int indentation,
        string key,
        bool? value)
    {
        if (value is { } actual)
        {
            builder.Append(' ', indentation).Append(key).Append(": ")
                .Append(actual ? "true" : "false").Append('\n');
        }
    }

    private static void AppendSequence(
        StringBuilder builder,
        int indentation,
        string key,
        IEnumerable<string> values)
    {
        builder.Append(' ', indentation).Append(key).Append(": [");
        var separator = string.Empty;
        foreach (var value in values.Select(value => value.Trim()))
        {
            builder.Append(separator).Append(JsonSerializer.Serialize(value));
            separator = ", ";
        }

        builder.Append("]\n");
    }
}
