using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;

namespace PaqetFire.Desktop.ViewModels;

/// <summary>
/// The editable connection and routing form, independent of controls. The window populates
/// controls from one of these and compares the current form with the last applied one to
/// decide whether there are pending changes. Secrets are deliberately not part of it.
/// </summary>
public sealed record ConnectionFormState
{
    public string ProfileName { get; init; } = "Default";
    public string ServerEndpoint { get; init; } = string.Empty;
    public Guid? NetworkInterfaceGuid { get; init; }
    public string KcpMode { get; init; } = "fast";
    public string LocalTcpFlags { get; init; } = "PA";
    public string RemoteTcpFlags { get; init; } = "PA";
    public PaqetAdvancedOptions Advanced { get; init; } = new();
    public bool RouteAllApplications { get; init; } = true;
    public string SelectedApplications { get; init; } = string.Empty;
    public string UserExclusions { get; init; } = string.Empty;
    public string DirectRouteDestinations { get; init; } = string.Empty;
    public bool BypassLan { get; init; } = true;
    public RegionalRoutingPreset RegionalPreset { get; init; } = RegionalRoutingPreset.IranDirect;
    public XrayDomainStrategy DomainStrategy { get; init; } = XrayDomainStrategy.IPIfNonMatch;
    public XrayDnsResolver DnsResolver { get; init; } = XrayDnsResolver.Cloudflare;
    public bool FakeDns { get; init; } = true;
    public bool BlockAds { get; init; } = true;
    public bool BlockMalware { get; init; } = true;
    public bool BlockQuic { get; init; } = true;
    public bool DirectBitTorrent { get; init; } = true;
    public ExitProxyKind ExitProxy { get; init; } = ExitProxyKind.None;
    /// <summary>Psiphon's exit country, or empty for the best available server.</summary>
    public string PsiphonRegion { get; init; } = string.Empty;
    /// <summary>The VPN account name; the imported .ovpn and the password are secrets, kept outside the form.</summary>
    public string OpenVpnUsername { get; init; } = string.Empty;
    public bool KillSwitch { get; init; }
    public bool ShareWithLan { get; init; }
    public int LanSocksPort { get; init; } = 1082;
    public string LanSocksUsername { get; init; } = "paqetfire";
    public bool ShareViaHotspot { get; init; }
    public int HotspotSocksPort { get; init; } = 10808;
    public bool RouteTcp { get; init; } = true;
    public bool RouteUdp { get; init; } = true;
    public bool RouteIpv4 { get; init; } = true;
    public bool RouteIpv6 { get; init; } = true;

    public static ConnectionFormState FromPreferences(DesktopPreferences source) => new ConnectionFormState
    {
        ProfileName = source.ProfileName,
        ServerEndpoint = source.ServerEndpoint,
        NetworkInterfaceGuid = source.NetworkInterfaceGuid,
        KcpMode = source.KcpMode,
        LocalTcpFlags = source.LocalTcpFlags,
        RemoteTcpFlags = source.RemoteTcpFlags,
        Advanced = source.Advanced ?? new(),
        RouteAllApplications = source.RouteAllApplications,
        SelectedApplications = source.SelectedApplications,
        UserExclusions = source.UserExclusions,
        DirectRouteDestinations = source.DirectRouteDestinations,
        BypassLan = source.BypassLan,
        RegionalPreset = source.RegionalPreset,
        DomainStrategy = source.DomainStrategy,
        DnsResolver = source.DnsResolver,
        FakeDns = source.FakeDns,
        BlockAds = source.BlockAds,
        BlockMalware = source.BlockMalware,
        BlockQuic = source.BlockQuic,
        DirectBitTorrent = source.DirectBitTorrent,
        ExitProxy = source.ExitProxy,
        PsiphonRegion = source.PsiphonRegion ?? string.Empty,
        KillSwitch = source.KillSwitch,
        ShareWithLan = source.ShareWithLan,
        LanSocksPort = source.LanSocksPort,
        LanSocksUsername = source.LanSocksUsername,
        ShareViaHotspot = source.ShareViaHotspot,
        HotspotSocksPort = source.HotspotSocksPort,
        RouteTcp = source.RouteTcp,
        RouteUdp = source.RouteUdp,
        RouteIpv4 = source.RouteIpv4,
        RouteIpv6 = source.RouteIpv6,
    }.Normalize();

    public static ConnectionFormState FromSettings(PaqetFireSettingsView settings) => new ConnectionFormState
    {
        ProfileName = settings.ProfileName,
        ServerEndpoint = settings.ServerEndpoint,
        NetworkInterfaceGuid = settings.NetworkInterfaceGuid,
        KcpMode = settings.KcpMode,
        LocalTcpFlags = string.Join(", ", settings.LocalTcpFlags),
        RemoteTcpFlags = string.Join(", ", settings.RemoteTcpFlags),
        Advanced = settings.Advanced,
        RouteAllApplications = settings.RoutingMode != RoutingMode.SelectedApplications,
        SelectedApplications = string.Join('\n', settings.SelectedApplications),
        UserExclusions = string.Join('\n', settings.UserExclusions),
        DirectRouteDestinations = string.Join('\n', settings.DirectRouteDestinations),
        BypassLan = settings.BypassLan,
        RegionalPreset = settings.RegionalPreset,
        DomainStrategy = settings.DomainStrategy,
        DnsResolver = settings.DnsResolver,
        FakeDns = settings.FakeDns,
        BlockAds = settings.BlockAds,
        BlockMalware = settings.BlockMalware,
        BlockQuic = settings.BlockQuic,
        DirectBitTorrent = settings.DirectBitTorrent,
        ExitProxy = settings.ExitProxy,
        PsiphonRegion = settings.Psiphon?.EgressRegion ?? string.Empty,
        OpenVpnUsername = settings.OpenVpn?.Username ?? string.Empty,
        KillSwitch = settings.KillSwitchEnabled,
        ShareWithLan = settings.ShareWithLan,
        LanSocksPort = settings.LanSocksPort,
        LanSocksUsername = settings.LanSocksUsername,
        ShareViaHotspot = settings.ShareViaHotspot,
        HotspotSocksPort = settings.HotspotSocksPort,
        RouteTcp = settings.RouteTcp,
        RouteUdp = settings.RouteUdp,
        RouteIpv4 = settings.RouteIpv4,
        RouteIpv6 = settings.RouteIpv6,
    }.Normalize();

    /// <summary>Canonical text so cosmetic differences (line endings, spacing, order of duplicates) are not changes.</summary>
    public ConnectionFormState Normalize() => this with
    {
        ProfileName = ProfileName.Trim(),
        ServerEndpoint = ServerEndpoint.Trim(),
        KcpMode = string.IsNullOrWhiteSpace(KcpMode) ? "fast" : KcpMode.Trim().ToLowerInvariant(),
        LocalTcpFlags = string.Join(", ", SplitEntries(LocalTcpFlags)),
        RemoteTcpFlags = string.Join(", ", SplitEntries(RemoteTcpFlags)),
        SelectedApplications = string.Join('\n', SplitLines(SelectedApplications)),
        UserExclusions = string.Join('\n', SplitLines(UserExclusions)),
        DirectRouteDestinations = string.Join('\n', SplitLines(DirectRouteDestinations)),
        LanSocksUsername = LanSocksUsername.Trim(),
        PsiphonRegion = (PsiphonRegion ?? string.Empty).Trim().ToUpperInvariant(),
        OpenVpnUsername = (OpenVpnUsername ?? string.Empty).Trim(),
        Advanced = Advanced ?? new(),
    };

    public bool IsEquivalentTo(ConnectionFormState? other) =>
        other is not null && Normalize().Equals(other.Normalize());

    /// <param name="openVpnConfiguration">A newly imported .ovpn, or empty to keep the saved one.</param>
    /// <param name="openVpnPassword">A newly entered VPN password, or empty to keep the saved one.</param>
    public PaqetFireSettings ToSettings(
        string transportKey,
        string sharingPassword,
        string openVpnConfiguration = "",
        string openVpnPassword = "") => new()
    {
        Advanced = Advanced,
        ProfileName = ProfileName.Trim(),
        ServerEndpoint = ServerEndpoint.Trim(),
        NetworkInterfaceGuid = NetworkInterfaceGuid,
        TransportKey = transportKey,
        RoutingMode = RouteAllApplications ? RoutingMode.AllApplications : RoutingMode.SelectedApplications,
        SelectedApplications = SplitLines(SelectedApplications),
        UserExclusions = SplitLines(UserExclusions),
        DirectRouteDestinations = SplitLines(DirectRouteDestinations),
        BypassLan = BypassLan,
        RouteTcp = RouteTcp,
        RouteUdp = RouteUdp,
        RouteIpv4 = RouteIpv4,
        RouteIpv6 = RouteIpv6,
        KillSwitchEnabled = KillSwitch,
        ShareWithLan = ShareWithLan,
        LanSocksPort = LanSocksPort,
        LanSocksUsername = LanSocksUsername.Trim(),
        LanSocksPassword = sharingPassword,
        ShareViaHotspot = ShareViaHotspot,
        HotspotSocksPort = HotspotSocksPort,
        RegionalPreset = RegionalPreset,
        DomainStrategy = DomainStrategy,
        DnsResolver = DnsResolver,
        FakeDns = FakeDns,
        BlockAds = BlockAds,
        BlockMalware = BlockMalware,
        BlockQuic = BlockQuic,
        DirectBitTorrent = DirectBitTorrent,
        ExitProxy = ExitProxy,
        Psiphon = new PsiphonExitOptions((PsiphonRegion ?? string.Empty).Trim().ToUpperInvariant()),
        OpenVpn = new OpenVpnExitOptions(openVpnConfiguration, (OpenVpnUsername ?? string.Empty).Trim(), openVpnPassword),
        KcpMode = string.IsNullOrWhiteSpace(KcpMode) ? "fast" : KcpMode.Trim(),
        LocalTcpFlags = SplitEntries(LocalTcpFlags),
        RemoteTcpFlags = SplitEntries(RemoteTcpFlags),
    };

    public void ApplyTo(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Advanced = Advanced;
        preferences.ProfileName = ProfileName.Trim();
        preferences.ServerEndpoint = ServerEndpoint.Trim();
        preferences.NetworkInterfaceGuid = NetworkInterfaceGuid;
        preferences.KcpMode = KcpMode;
        preferences.LocalTcpFlags = LocalTcpFlags.Trim();
        preferences.RemoteTcpFlags = RemoteTcpFlags.Trim();
        preferences.RouteAllApplications = RouteAllApplications;
        preferences.SelectedApplications = SelectedApplications.Trim();
        preferences.UserExclusions = UserExclusions.Trim();
        preferences.DirectRouteDestinations = DirectRouteDestinations.Trim();
        preferences.BypassLan = BypassLan;
        preferences.RegionalPreset = RegionalPreset;
        preferences.DomainStrategy = DomainStrategy;
        preferences.DnsResolver = DnsResolver;
        preferences.FakeDns = FakeDns;
        preferences.BlockAds = BlockAds;
        preferences.BlockMalware = BlockMalware;
        preferences.BlockQuic = BlockQuic;
        preferences.DirectBitTorrent = DirectBitTorrent;
        preferences.ExitProxy = ExitProxy;
        preferences.PsiphonRegion = (PsiphonRegion ?? string.Empty).Trim().ToUpperInvariant();
        preferences.KillSwitch = KillSwitch;
        preferences.ShareWithLan = ShareWithLan;
        preferences.ShareViaHotspot = ShareViaHotspot;
        preferences.HotspotSocksPort = HotspotSocksPort;
        preferences.LanSocksPort = LanSocksPort;
        preferences.LanSocksUsername = LanSocksUsername.Trim();
        preferences.RouteTcp = RouteTcp;
        preferences.RouteUdp = RouteUdp;
        preferences.RouteIpv4 = RouteIpv4;
        preferences.RouteIpv6 = RouteIpv6;
    }

    public static IReadOnlyList<string> SplitEntries(string? value) => (value ?? string.Empty)
        .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IReadOnlyList<string> SplitLines(string? value) => (value ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
