namespace PaqetFire.Core.Configuration;

/// <summary>Optional Paqet packet tuning. Null values retain the bundled engine defaults.</summary>
public sealed record PaqetAdvancedOptions
{
    public string LogLevel { get; init; } = "info";

    public int LocalIpv4Port { get; init; } = 0;

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

    public PaqetProfile ApplyTo(PaqetProfile profile) => profile with
    {
        LogLevel = LogLevel,
        LocalIpv4Port = LocalIpv4Port,
        LocalIpv6Endpoint = LocalIpv6Endpoint,
        Ipv6RouterMac = Ipv6RouterMac,
        PcapSocketBufferBytes = PcapSocketBufferBytes,
        ConnectionCount = ConnectionCount,
        KcpNoDelay = KcpNoDelay,
        KcpIntervalMilliseconds = KcpIntervalMilliseconds,
        KcpResend = KcpResend,
        KcpNoCongestion = KcpNoCongestion,
        KcpWriteDelay = KcpWriteDelay,
        KcpAckNoDelay = KcpAckNoDelay,
        KcpMtu = KcpMtu,
        KcpReceiveWindow = KcpReceiveWindow,
        KcpSendWindow = KcpSendWindow,
        KcpBlock = KcpBlock,
        SmuxBufferBytes = SmuxBufferBytes,
        StreamBufferBytes = StreamBufferBytes,
        SmuxKeepAliveSeconds = SmuxKeepAliveSeconds,
        SmuxKeepAliveTimeoutSeconds = SmuxKeepAliveTimeoutSeconds,
        FecDataShards = FecDataShards,
        FecParityShards = FecParityShards,
    };
}

