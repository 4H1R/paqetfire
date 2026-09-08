using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Configuration;

public static class PaqetFireSettingsValidator
{
    private const int MaxRoutingEntries = 128;
    private const int MaxRoutingCharacters = 8 * 1024;
    private static readonly HashSet<string> KcpModes = new(
        ["normal", "fast", "fast2", "fast3"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(PaqetFireSettings? settings)
    {
        if (settings is null)
        {
            return ["Connection settings are required."];
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.ProfileName) ||
            settings.ProfileName.Length > 80 ||
            settings.ProfileName.Any(char.IsControl))
        {
            errors.Add("The profile name must contain 1 to 80 printable characters.");
        }

        if (!ConfigurationValuePolicy.TryNormalizeEndpoint(settings.ServerEndpoint, out _))
        {
            errors.Add("The server must be a hostname or IP address followed by a port.");
        }

        if (string.IsNullOrEmpty(settings.TransportKey) ||
            settings.TransportKey.Length > 1024 ||
            settings.TransportKey.Any(char.IsControl))
        {
            errors.Add("The transport key is required and must contain no control characters.");
        }

        if (!KcpModes.Contains(settings.KcpMode ?? string.Empty))
        {
            errors.Add("KCP mode must be normal, fast, fast2, or fast3.");
        }

        if (!Enum.IsDefined(settings.RoutingMode))
        {
            errors.Add("The routing mode is invalid.");
        }

        if (!Enum.IsDefined(settings.RegionalPreset))
        {
            errors.Add("The regional routing preset is invalid.");
        }

        if (!Enum.IsDefined(settings.DomainStrategy))
        {
            errors.Add("The Xray domain strategy is invalid.");
        }

        if (!settings.RouteTcp && !settings.RouteUdp)
        {
            errors.Add("Select at least one routed protocol: TCP or UDP.");
        }

        if (!settings.RouteIpv4 && !settings.RouteIpv6)
        {
            errors.Add("Select at least one routed address family: IPv4 or IPv6.");
        }

        if (settings.ShareWithLan)
        {
            if (settings.LanSocksPort is < 1024 or > 65535 ||
                settings.LanSocksPort is XrayJsonConfigurationWriter.PaqetPort or XrayJsonConfigurationWriter.InboundPort)
            {
                errors.Add("The shared SOCKS5 port must be between 1024 and 65535 and cannot be 1080 or 1081.");
            }

            if (string.IsNullOrWhiteSpace(settings.LanSocksUsername) ||
                settings.LanSocksUsername.Length > 64 ||
                settings.LanSocksUsername.Any(char.IsControl))
            {
                errors.Add("The shared SOCKS5 username must contain 1 to 64 printable characters.");
            }

            if (string.IsNullOrEmpty(settings.LanSocksPassword) ||
                settings.LanSocksPassword.Length is < 8 or > 128 ||
                settings.LanSocksPassword.Any(char.IsControl))
            {
                errors.Add("The shared SOCKS5 password must contain 8 to 128 characters with no control characters.");
            }
        }

        if (settings.ShareViaHotspot)
        {
            if (!settings.BypassLan)
            {
                errors.Add("Hotspot sharing requires direct access for local network devices.");
            }
            if (settings.HotspotSocksPort is < 1024 or > 65535 ||
                settings.HotspotSocksPort is XrayJsonConfigurationWriter.PaqetPort or XrayJsonConfigurationWriter.InboundPort)
                errors.Add("The hotspot SOCKS port must be between 1024 and 65535 and cannot be 1080 or 1081.");
            if (settings.ShareWithLan && settings.HotspotSocksPort == settings.LanSocksPort)
                errors.Add("The hotspot port must differ from the LAN share port.");
            if (string.IsNullOrWhiteSpace(settings.LanSocksUsername) ||
                settings.LanSocksUsername.Length > 64 ||
                settings.LanSocksUsername.Any(char.IsControl) ||
                string.IsNullOrEmpty(settings.LanSocksPassword) ||
                settings.LanSocksPassword.Length is < 8 or > 128 ||
                settings.LanSocksPassword.Any(char.IsControl))
                errors.Add("Hotspot sharing requires a valid proxy username (1 to 64 characters) and password (8 to 128 characters).");
        }

        if (settings.RoutingMode == RoutingMode.SelectedApplications &&
            (settings.SelectedApplications is null || settings.SelectedApplications.Count == 0))
        {
            errors.Add("Selected-applications mode requires at least one application.");
        }

        ValidateEntries(settings.SelectedApplications, "selected application", errors);
        ValidateEntries(settings.UserExclusions, "exclusion", errors);
        ValidateDirectRouteDestinations(settings.DirectRouteDestinations, errors);
        ValidateFlags(settings.LocalTcpFlags, "local", errors);
        ValidateFlags(settings.RemoteTcpFlags, "remote", errors);
        return errors;
    }

    private static void ValidateDirectRouteDestinations(
        IReadOnlyList<string>? entries,
        ICollection<string> errors)
    {
        ValidateEntries(entries, "direct-route destination", errors);
        if (entries is not null && entries.Any(entry => !DirectRouteDestination.TryParse(entry, out _)))
        {
            errors.Add("Every direct-route destination must be a domain, *.domain wildcard, IP address, or CIDR range.");
        }
    }

    private static void ValidateEntries(
        IReadOnlyList<string>? entries,
        string label,
        ICollection<string> errors)
    {
        if (entries is null || entries.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 1024 ||
                value.Any(char.IsControl)))
        {
            errors.Add($"Every {label} must contain 1 to 1024 printable characters.");
        }

        if (entries is { } values &&
            (values.Count > MaxRoutingEntries || values.Sum(value => value?.Length ?? 0) > MaxRoutingCharacters))
        {
            errors.Add($"The {label} list is too large. Use at most {MaxRoutingEntries} entries and {MaxRoutingCharacters} characters.");
        }
    }

    private static void ValidateFlags(
        IReadOnlyList<string>? flags,
        string label,
        ICollection<string> errors)
    {
        const string valid = "FSRPAUEC";
        if (flags is null || flags.Count == 0 || flags.Any(flag =>
                string.IsNullOrWhiteSpace(flag) || flag.Any(character => !valid.Contains(character))))
        {
            errors.Add($"At least one valid {label} TCP flag is required.");
        }
    }
}
