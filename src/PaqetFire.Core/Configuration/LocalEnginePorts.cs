using System.Text.Json.Serialization;

namespace PaqetFire.Core.Configuration;

/// <summary>
/// The loopback ports PaqetFire's engines use to reach one another. Only this computer can
/// reach them and each one requires the engine credentials, so nobody configures them: the
/// broker keeps the defaults while they are free and moves a port that another program holds.
/// </summary>
/// <param name="Paqet">Paqet's SOCKS5 listener, which Xray and the exit engines dial.</param>
/// <param name="XrayInbound">Xray's SOCKS5 listener, which ProxiFyre sends captured traffic to.</param>
/// <param name="XrayStats">Xray's statistics API.</param>
/// <param name="PsiphonSocks">The Psiphon exit's SOCKS5 listener.</param>
public sealed record LocalEnginePorts(int Paqet, int XrayInbound, int XrayStats, int PsiphonSocks)
{
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;

    public static LocalEnginePorts Default { get; } = new(
        XrayJsonConfigurationWriter.PaqetPort,
        XrayJsonConfigurationWriter.InboundPort,
        XrayJsonConfigurationWriter.StatsApiPort,
        PsiphonJsonConfigurationWriter.LocalSocksPort);

    [JsonIgnore]
    public IReadOnlyList<int> All => [Paqet, XrayInbound, XrayStats, PsiphonSocks];

    /// <summary>Every port is in the unprivileged range and no two share a number.</summary>
    [JsonIgnore]
    public bool IsValid => All.All(port => port is >= MinimumPort and <= MaximumPort) && All.Distinct().Count() == All.Count;

    public bool Uses(int port) => All.Contains(port);

    public override string ToString() =>
        $"Paqet {Paqet} · Xray {XrayInbound} · Xray statistics {XrayStats} · Psiphon {PsiphonSocks}";
}
