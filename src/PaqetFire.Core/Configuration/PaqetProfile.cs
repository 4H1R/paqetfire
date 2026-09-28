namespace PaqetFire.Core.Configuration;

/// <summary>
/// Complete Paqet v1 client configuration. Nullable tuning values are omitted
/// from YAML so Paqet can apply the defaults shipped with the bundled version.
/// </summary>
public sealed record PaqetProfile(
    string ServerEndpoint,
    string LocalSocksEndpoint,
    string InterfaceName,
    string InterfaceGuid,
    string LocalIpv4Address,
    string RouterMac,
    IReadOnlyList<string> LocalTcpFlags,
    IReadOnlyList<string> RemoteTcpFlags,
    string KcpMode = "fast")
{
    public string LogLevel { get; init; } = "info";

    public int LocalIpv4Port { get; init; }

    public string? SocksUsername { get; init; }

    public string? SocksPassword { get; init; }

    public IReadOnlyList<PaqetForwardRule> ForwardRules { get; init; } = [];

    /// <summary>An IPv6 endpoint such as [2001:db8::1]:0. Null disables IPv6.</summary>
    public string? LocalIpv6Endpoint { get; init; }

    public string? Ipv6RouterMac { get; init; }

    public int? PcapSocketBufferBytes { get; init; }

    public int ConnectionCount { get; init; } = 1;

    public int? KcpNoDelay { get; init; }

    public int? KcpIntervalMilliseconds { get; init; }

    public int? KcpResend { get; init; }

    public int? KcpNoCongestion { get; init; }

    public bool? KcpWriteDelay { get; init; }

    public bool? KcpAckNoDelay { get; init; }

    public int? KcpMtu { get; init; }

    public int? KcpReceiveWindow { get; init; }

    public int? KcpSendWindow { get; init; }

    public string KcpBlock { get; init; } = "aes";

    public int? SmuxBufferBytes { get; init; }

    public int? StreamBufferBytes { get; init; }

    public int? SmuxKeepAliveSeconds { get; init; }

    public int? SmuxKeepAliveTimeoutSeconds { get; init; }

    public int? FecDataShards { get; init; }

    public int? FecParityShards { get; init; }
}

public sealed record PaqetForwardRule(
    string ListenEndpoint,
    string TargetEndpoint,
    string Protocol = "tcp");
