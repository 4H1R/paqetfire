using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Configuration;

public sealed record PaqetFireSettings
{
    public PaqetAdvancedOptions Advanced { get; init; } = new();

    public string ProfileName { get; init; } = "Default";

    public string ServerEndpoint { get; init; } = string.Empty;

    public Guid? NetworkInterfaceGuid { get; init; }

    public string TransportKey { get; init; } = string.Empty;

    public RoutingMode RoutingMode { get; init; } = RoutingMode.AllApplications;

    public IReadOnlyList<string> SelectedApplications { get; init; } = [];

    public IReadOnlyList<string> UserExclusions { get; init; } = [];

    public IReadOnlyList<string> DirectRouteDestinations { get; init; } = [];

    public bool BypassLan { get; init; }

    public bool RouteTcp { get; init; } = true;

    public bool RouteUdp { get; init; } = true;

    public bool RouteIpv4 { get; init; } = true;

    public bool RouteIpv6 { get; init; } = true;

    public RegionalRoutingPreset RegionalPreset { get; init; } = RegionalRoutingPreset.IranDirect;

    public XrayDomainStrategy DomainStrategy { get; init; } = XrayDomainStrategy.IPIfNonMatch;

    public XrayDnsResolver DnsResolver { get; init; } = XrayDnsResolver.Cloudflare;

    // On for new profiles: skips one tunnel round trip per new domain.
    public bool FakeDns { get; init; } = true;

    public bool BlockAds { get; init; } = true;

    // Takes effect only with updated routing data, which carries these categories.
    public bool BlockMalware { get; init; } = true;

    // On for new profiles: QUIC over KCP stacks two retransmission loops, and browsers
    // fall back to TCP at once. Only tunneled QUIC is blocked.
    public bool BlockQuic { get; init; } = true;

    public bool DirectBitTorrent { get; init; } = true;

    public ExitProxyKind ExitProxy { get; init; } = ExitProxyKind.None;

    public PsiphonExitOptions Psiphon { get; init; } = new();

    public OpenVpnExitOptions OpenVpn { get; init; } = new();

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
    IReadOnlyList<string> DirectRouteDestinations,
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
    public PaqetAdvancedOptions Advanced { get; init; } = new();

    public Guid? NetworkInterfaceGuid { get; init; }

    public XrayDnsResolver DnsResolver { get; init; } = XrayDnsResolver.Cloudflare;

    public bool FakeDns { get; init; }

    public bool BlockMalware { get; init; }

    public ExitProxyKind ExitProxy { get; init; }

    public PsiphonExitOptions Psiphon { get; init; } = new();

    public OpenVpnExitView OpenVpn { get; init; } = new();

    public static PaqetFireSettingsView FromSettings(PaqetFireSettings settings) => new(
        settings.ProfileName,
        settings.ServerEndpoint,
        !string.IsNullOrEmpty(settings.TransportKey),
        settings.RoutingMode,
        settings.SelectedApplications,
        settings.UserExclusions,
        settings.DirectRouteDestinations,
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
        settings.RemoteTcpFlags)
    {
        NetworkInterfaceGuid = settings.NetworkInterfaceGuid,
        Advanced = settings.Advanced,
        DnsResolver = settings.DnsResolver,
        FakeDns = settings.FakeDns,
        BlockMalware = settings.BlockMalware,
        ExitProxy = settings.ExitProxy,
        Psiphon = settings.Psiphon ?? new(),
        OpenVpn = OpenVpnExitView.FromOptions(settings.OpenVpn),
    };
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

/// <summary>System leaves lookups to the Windows resolver; the others answer captured DNS inside Xray.</summary>
public enum XrayDnsResolver
{
    System,
    Cloudflare,
    Google,
    Quad9,
}

public sealed record XrayRoutingPolicy(
    RegionalRoutingPreset RegionalPreset,
    XrayDomainStrategy DomainStrategy,
    bool BypassLan,
    bool BlockAds,
    bool BlockQuic,
    bool DirectBitTorrent,
    LanSocksShare? LanShare = null,
    LanSocksShare? HotspotShare = null,
    IReadOnlyList<string>? DirectRouteDestinations = null,
    XrayDnsPolicy? Dns = null,
    EngineSocksCredentials? EngineCredentials = null,
    bool BlockMalware = false,
    ExitProxyOutbound? Exit = null);

/// <param name="DomesticServers">IPv4 resolvers of the active network, used directly for local and direct-routed domains.</param>
public sealed record XrayDnsPolicy(
    XrayDnsResolver Resolver,
    IReadOnlyList<string> DomesticServers,
    bool ResolveIpv4 = true,
    bool ResolveIpv6 = true,
    bool UseFakeDns = false);

public sealed record LanSocksShare(
    string ListenAddress,
    int Port,
    string Username,
    string Password);
