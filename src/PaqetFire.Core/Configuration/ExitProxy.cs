using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Configuration;

/// <summary>
/// A second hop after Paqet: routed traffic leaves the Paqet server through this network,
/// so destinations see its exit address instead of the Paqet server's.
/// </summary>
public enum ExitProxyKind
{
    None,
    Psiphon,
    OpenVpn,
}

/// <param name="EgressRegion">
/// ISO 3166-1 alpha-2 country Psiphon exits from, or empty for the best performing server in any country.
/// </param>
public sealed record PsiphonExitOptions(string EgressRegion = "")
{
    /// <summary>
    /// Regions the Psiphon network has served. Psiphon reports the ones available right now
    /// once connected; any other two-letter code is still accepted, as the list changes.
    /// </summary>
    public static IReadOnlyList<string> KnownRegions { get; } =
    [
        "AT", "AU", "BE", "BG", "CA", "CH", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GB", "HR", "HU",
        "IE", "IN", "IT", "JP", "LV", "NL", "NO", "PL", "PT", "RO", "RS", "SE", "SG", "SK", "US",
    ];

    public static bool IsValidRegion(string? region) =>
        region is not null &&
        (region.Length == 0 || region.Length == 2 && region.All(character => character is >= 'A' and <= 'Z'));
}

/// <param name="Configuration">
/// The provider's .ovpn file as imported. Only the options <see cref="OpenVpnConfiguration"/>
/// allows reach OpenVPN; a broker keeps it, like the transport key, encrypted at rest.
/// </param>
/// <param name="Username">The VPN account name, for configurations that ask for one.</param>
/// <param name="Password">The VPN account password, for configurations that ask for one.</param>
public sealed record OpenVpnExitOptions(string Configuration = "", string Username = "", string Password = "");

/// <summary>What the desktop sees of the OpenVPN exit: never the configuration or password.</summary>
/// <param name="Server">The first server the configuration names, as <c>host:port</c>.</param>
public sealed record OpenVpnExitView(
    string Username = "",
    bool HasConfiguration = false,
    bool HasPassword = false,
    string? Server = null)
{
    public static OpenVpnExitView FromOptions(OpenVpnExitOptions? options)
    {
        if (options is null)
        {
            return new();
        }

        var parsed = OpenVpnConfiguration.Parse(options.Configuration);
        return new(
            options.Username,
            !string.IsNullOrWhiteSpace(options.Configuration),
            !string.IsNullOrEmpty(options.Password),
            parsed.Remote?.ToString());
    }
}

/// <summary>How Xray's exit outbound reaches the exit engine.</summary>
public abstract record ExitProxyOutbound
{
    /// <summary>Whether routed UDP leaves through the exit; otherwise it keeps leaving from the Paqet server.</summary>
    public abstract bool CarriesUdp { get; }

    /// <summary>The outbound each exit engine serves.</summary>
    public static ExitProxyOutbound? For(ExitProxyKind kind) => kind switch
    {
        ExitProxyKind.Psiphon => new SocksExitOutbound(PsiphonJsonConfigurationWriter.LocalSocksPort),
        ExitProxyKind.OpenVpn => new InterfaceExitOutbound(OpenVpnConfigurationWriter.AdapterName),
        _ => null,
    };

    public static ExitProxyOutbound? For(EngineKind engine) => engine switch
    {
        EngineKind.Psiphon => For(ExitProxyKind.Psiphon),
        EngineKind.OpenVpn => For(ExitProxyKind.OpenVpn),
        _ => null,
    };
}

/// <summary>The exit engine's local SOCKS5 listener. Psiphon's relays TCP only.</summary>
public sealed record SocksExitOutbound(int Port) : ExitProxyOutbound
{
    public override bool CarriesUdp => false;
}

/// <summary>
/// A network adapter the exit engine owns. Xray's sockets are bound to it, so they fail
/// instead of leaving through another route while the exit's tunnel is down.
/// </summary>
public sealed record InterfaceExitOutbound(string InterfaceName) : ExitProxyOutbound
{
    public override bool CarriesUdp => true;
}

/// <param name="UpstreamCredentials">Paqet's loopback SOCKS5 credentials; Psiphon dials its servers only through Paqet.</param>
public sealed record PsiphonClientProfile(
    int LocalSocksPort,
    int UpstreamSocksPort,
    EngineSocksCredentials UpstreamCredentials,
    string EgressRegion);
