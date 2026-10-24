namespace PaqetFire.Core.Configuration;

/// <summary>
/// A second hop after Paqet: routed TCP leaves the Paqet server through this network,
/// so destinations see its exit address instead of the Paqet server's.
/// </summary>
public enum ExitProxyKind
{
    None,
    Psiphon,
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

/// <summary>The local SOCKS5 listener of the exit engine that Xray's exit outbound dials.</summary>
public sealed record ExitProxyOutbound(int Port);

/// <param name="UpstreamCredentials">Paqet's loopback SOCKS5 credentials; Psiphon dials its servers only through Paqet.</param>
public sealed record PsiphonClientProfile(
    int LocalSocksPort,
    int UpstreamSocksPort,
    EngineSocksCredentials UpstreamCredentials,
    string EgressRegion);
