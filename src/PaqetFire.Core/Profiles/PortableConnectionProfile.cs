using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Profiles;

// Portable v2 groups settings by the engine that owns them. It deliberately has
// no adapter identity or application SOCKS authentication fields.
internal sealed record PortableConnectionProfile
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Default";
    public PortablePaqetSettings Paqet { get; init; } = new();
    public PortableXraySettings Xray { get; init; } = new();
    public PortableProxiFyreSettings ProxiFyre { get; init; } = new();
    public PortableSharingSettings Sharing { get; init; } = new();

    public static PortableConnectionProfile FromProfile(ConnectionProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Settings.ProfileName,
        Paqet = new()
        {
            ServerEndpoint = profile.Settings.ServerEndpoint,
            TransportKey = profile.Settings.TransportKey,
            KcpMode = profile.Settings.KcpMode,
            LocalTcpFlags = profile.Settings.LocalTcpFlags,
            RemoteTcpFlags = profile.Settings.RemoteTcpFlags,
            Advanced = profile.Settings.Advanced,
        },
        Xray = new()
        {
            RegionalPreset = profile.Settings.RegionalPreset,
            DomainStrategy = profile.Settings.DomainStrategy,
            DirectRouteDestinations = profile.Settings.DirectRouteDestinations,
            BypassLan = profile.Settings.BypassLan,
            BlockAds = profile.Settings.BlockAds,
            BlockQuic = profile.Settings.BlockQuic,
            DirectBitTorrent = profile.Settings.DirectBitTorrent,
        },
        ProxiFyre = new()
        {
            RoutingMode = profile.Settings.RoutingMode,
            SelectedApplications = profile.Settings.SelectedApplications,
            UserExclusions = profile.Settings.UserExclusions,
            RouteTcp = profile.Settings.RouteTcp,
            RouteUdp = profile.Settings.RouteUdp,
            RouteIpv4 = profile.Settings.RouteIpv4,
            RouteIpv6 = profile.Settings.RouteIpv6,
            KillSwitchEnabled = profile.Settings.KillSwitchEnabled,
        },
        Sharing = new()
        {
            ShareWithLan = profile.Settings.ShareWithLan,
            LanSocksPort = profile.Settings.LanSocksPort,
            ShareViaHotspot = profile.Settings.ShareViaHotspot,
            HotspotSocksPort = profile.Settings.HotspotSocksPort,
        },
    };

    public ConnectionProfile ToProfile()
    {
        ArgumentNullException.ThrowIfNull(Paqet);
        ArgumentNullException.ThrowIfNull(Xray);
        ArgumentNullException.ThrowIfNull(ProxiFyre);
        ArgumentNullException.ThrowIfNull(Sharing);
        return new(Id, new PaqetFireSettings
        {
            ProfileName = Name,
            ServerEndpoint = Paqet.ServerEndpoint,
            TransportKey = Paqet.TransportKey,
            KcpMode = Paqet.KcpMode,
            LocalTcpFlags = Paqet.LocalTcpFlags,
            RemoteTcpFlags = Paqet.RemoteTcpFlags,
            Advanced = Paqet.Advanced,
            RegionalPreset = Xray.RegionalPreset,
            DomainStrategy = Xray.DomainStrategy,
            DirectRouteDestinations = Xray.DirectRouteDestinations,
            BypassLan = Xray.BypassLan,
            BlockAds = Xray.BlockAds,
            BlockQuic = Xray.BlockQuic,
            DirectBitTorrent = Xray.DirectBitTorrent,
            RoutingMode = ProxiFyre.RoutingMode,
            SelectedApplications = ProxiFyre.SelectedApplications,
            UserExclusions = ProxiFyre.UserExclusions,
            RouteTcp = ProxiFyre.RouteTcp,
            RouteUdp = ProxiFyre.RouteUdp,
            RouteIpv4 = ProxiFyre.RouteIpv4,
            RouteIpv6 = ProxiFyre.RouteIpv6,
            KillSwitchEnabled = ProxiFyre.KillSwitchEnabled,
            ShareWithLan = Sharing.ShareWithLan,
            LanSocksPort = Sharing.LanSocksPort,
            ShareViaHotspot = Sharing.ShareViaHotspot,
            HotspotSocksPort = Sharing.HotspotSocksPort,
        });
    }
}

internal sealed record PortablePaqetSettings
{
    public string ServerEndpoint { get; init; } = string.Empty;
    public string TransportKey { get; init; } = string.Empty;
    public string KcpMode { get; init; } = "fast";
    public IReadOnlyList<string> LocalTcpFlags { get; init; } = ["PA"];
    public IReadOnlyList<string> RemoteTcpFlags { get; init; } = ["PA"];
    public PaqetAdvancedOptions Advanced { get; init; } = new();
}

internal sealed record PortableXraySettings
{
    public RegionalRoutingPreset RegionalPreset { get; init; } = RegionalRoutingPreset.IranDirect;
    public XrayDomainStrategy DomainStrategy { get; init; } = XrayDomainStrategy.IPIfNonMatch;
    public IReadOnlyList<string> DirectRouteDestinations { get; init; } = [];
    public bool BypassLan { get; init; } = false;
    public bool BlockAds { get; init; } = true;
    public bool BlockQuic { get; init; } = false;
    public bool DirectBitTorrent { get; init; } = true;
}

internal sealed record PortableProxiFyreSettings
{
    public RoutingMode RoutingMode { get; init; } = RoutingMode.AllApplications;
    public IReadOnlyList<string> SelectedApplications { get; init; } = [];
    public IReadOnlyList<string> UserExclusions { get; init; } = [];
    public bool RouteTcp { get; init; } = true;
    public bool RouteUdp { get; init; } = true;
    public bool RouteIpv4 { get; init; } = true;
    public bool RouteIpv6 { get; init; } = true;
    public bool KillSwitchEnabled { get; init; } = false;
}

internal sealed record PortableSharingSettings
{
    public bool ShareWithLan { get; init; } = false;
    public int LanSocksPort { get; init; } = 1082;
    public bool ShareViaHotspot { get; init; } = false;
    public int HotspotSocksPort { get; init; } = 10808;
}

