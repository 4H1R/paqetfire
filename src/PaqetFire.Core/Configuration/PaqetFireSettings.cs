using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Configuration;

public sealed record PaqetFireSettings
{
    public string ProfileName { get; init; } = "Default";

    public string ServerEndpoint { get; init; } = string.Empty;

    public string TransportKey { get; init; } = string.Empty;

    public RoutingMode RoutingMode { get; init; } = RoutingMode.AllApplications;

    public IReadOnlyList<string> SelectedApplications { get; init; } = [];

    public IReadOnlyList<string> UserExclusions { get; init; } = [];

    public bool BypassLan { get; init; }

    public bool RouteTcp { get; init; } = true;

    public bool RouteUdp { get; init; } = true;

    public bool RouteIpv4 { get; init; } = true;

    public bool RouteIpv6 { get; init; } = true;

    public RegionalRoutingPreset RegionalPreset { get; init; } = RegionalRoutingPreset.IranDirect;

    public XrayDomainStrategy DomainStrategy { get; init; } = XrayDomainStrategy.IPIfNonMatch;

    public bool BlockAds { get; init; } = true;

    public bool BlockQuic { get; init; }

    public bool DirectBitTorrent { get; init; } = true;

    public bool KillSwitchEnabled { get; init; }

    public bool ShareWithLan { get; init; }

    public int LanSocksPort { get; init; } = 1082;

    public string LanSocksUsername { get; init; } = "paqetfire";

    public string LanSocksPassword { get; init; } = string.Empty;

    public bool ShareViaHotspot { get; init; }

    public int HotspotSocksPort { get; init; } = 10808;

    public string KcpMode { get; init; } = "fast";

    public IReadOnlyList<string> LocalTcpFlags { get; init; } = ["PA"];

    public IReadOnlyList<string> RemoteTcpFlags { get; init; } = ["PA"];
}

public sealed record PaqetFireSettingsView(
    string ProfileName,
    string ServerEndpoint,
    bool HasTransportKey,
    RoutingMode RoutingMode,
    IReadOnlyList<string> SelectedApplications,
    IReadOnlyList<string> UserExclusions,
    bool BypassLan,
    bool RouteTcp,
    bool RouteUdp,
    bool RouteIpv4,
    bool RouteIpv6,
    RegionalRoutingPreset RegionalPreset,
    XrayDomainStrategy DomainStrategy,
    bool BlockAds,
    bool BlockQuic,
    bool DirectBitTorrent,
    bool KillSwitchEnabled,
    bool ShareWithLan,
    int LanSocksPort,
    string LanSocksUsername,
    bool HasLanSocksPassword,
    bool ShareViaHotspot,
    int HotspotSocksPort,
    string KcpMode,
    IReadOnlyList<string> LocalTcpFlags,
    IReadOnlyList<string> RemoteTcpFlags)
{
    public static PaqetFireSettingsView FromSettings(PaqetFireSettings settings) => new(
        settings.ProfileName,
        settings.ServerEndpoint,
        !string.IsNullOrEmpty(settings.TransportKey),
        settings.RoutingMode,
        settings.SelectedApplications,
        settings.UserExclusions,
        settings.BypassLan,
        settings.RouteTcp,
        settings.RouteUdp,
        settings.RouteIpv4,
        settings.RouteIpv6,
        settings.RegionalPreset,
        settings.DomainStrategy,
        settings.BlockAds,
        settings.BlockQuic,
        settings.DirectBitTorrent,
        settings.KillSwitchEnabled,
        settings.ShareWithLan,
        settings.LanSocksPort,
        settings.LanSocksUsername,
        !string.IsNullOrEmpty(settings.LanSocksPassword),
        settings.ShareViaHotspot,
        settings.HotspotSocksPort,
        settings.KcpMode,
        settings.LocalTcpFlags,
        settings.RemoteTcpFlags);
}

public enum RegionalRoutingPreset
{
    None,
    IranDirect,
}

public enum XrayDomainStrategy
{
    AsIs,
    IPIfNonMatch,
    IPOnDemand,
}

public sealed record XrayRoutingPolicy(
    RegionalRoutingPreset RegionalPreset,
    XrayDomainStrategy DomainStrategy,
    bool BypassLan,
    bool BlockAds,
    bool BlockQuic,
    bool DirectBitTorrent,
    LanSocksShare? LanShare = null,
    LanSocksShare? HotspotShare = null);

public sealed record LanSocksShare(
    string ListenAddress,
    int Port,
    string Username,
    string Password);
