using System.Text;

namespace PaqetFire.Core.Configuration;

/// <param name="SocksCredentialsPath">A file holding Paqet's loopback SOCKS5 username and password.</param>
/// <param name="UserCredentialsPath">A file holding the VPN account, for configurations that ask for one.</param>
public sealed record OpenVpnClientProfile(
    OpenVpnConfiguration Configuration,
    int UpstreamSocksPort,
    string SocksCredentialsPath,
    string? UserCredentialsPath);

/// <summary>
/// Writes the configuration the OpenVPN exit runs. OpenVPN dials its server only through
/// Paqet's authenticated loopback SOCKS5 listener and owns a dedicated adapter. It changes
/// no system route or DNS setting: the only route it adds is a default route on its own
/// adapter with the highest metric Windows accepts, which ordinary traffic never prefers
/// and which Xray's exit outbound uses by binding its sockets to that adapter.
/// </summary>
public sealed class OpenVpnConfigurationWriter
{
    /// <summary>The TAP adapter PaqetFire creates for the exit; Xray binds the exit outbound to it.</summary>
    public const string AdapterName = "PaqetFire Exit";

    private const int ExitRouteMetric = 9999;

    // route-nopull already drops pushed routes, redirect-*, DHCP, and DNS options, whatever
    // whitespace the server uses; these filters repeat that and add what it leaves. "route "
    // keeps its space so route-gateway, which vpn_gateway needs, still arrives.
    private static readonly string[] IgnoredPushes =
    [
        "redirect-gateway", "redirect-private", "route ", "route-ipv6", "ifconfig-ipv6",
        "dhcp-option", "dns", "block-outside-dns", "register-dns",
    ];

    public string Write(OpenVpnClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Configuration);
        var configuration = profile.Configuration;
        if (!configuration.IsValid)
        {
            throw new ConfigurationValidationException(configuration.Errors);
        }

        if (profile.UpstreamSocksPort is < 1 or > 65535)
        {
            throw new ConfigurationValidationException(["The OpenVPN upstream SOCKS5 port is invalid."]);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profile.SocksCredentialsPath);
        if (configuration.RequiresCredentials && string.IsNullOrWhiteSpace(profile.UserCredentialsPath))
        {
            throw new ConfigurationValidationException(["This OpenVPN configuration needs a username and password."]);
        }

        var text = new StringBuilder();
        text.Append("# Written by PaqetFire from an imported OpenVPN configuration.\n");
        text.Append("# Only allowlisted client options are kept; see docs/exit-proxy.md.\n");
        text.Append("client\n");
        // SOCKS5 carries TCP only, and it disables OpenVPN's data channel offload driver.
        text.Append("proto tcp-client\n");
        foreach (var remote in configuration.Remotes)
        {
            text.Append($"remote {remote.Host} {remote.Port}\n");
        }

        foreach (var line in configuration.KeptLines)
        {
            text.Append(line).Append('\n');
        }

        text.Append("dev tun\n");
        text.Append($"dev-node {OpenVpnConfiguration.Quote(AdapterName)}\n");
        text.Append("windows-driver tap-windows6\n");
        text.Append($"socks-proxy 127.0.0.1 {profile.UpstreamSocksPort} {QuotePath(profile.SocksCredentialsPath)}\n");
        if (configuration.RequiresCredentials)
        {
            text.Append($"auth-user-pass {QuotePath(profile.UserCredentialsPath!)}\n");
        }

        // A rejected account ends the process instead of prompting, and nothing may run scripts.
        text.Append("auth-retry none\n");
        text.Append("script-security 1\n");
        text.Append("route-nopull\n");
        foreach (var push in IgnoredPushes)
        {
            text.Append($"pull-filter ignore {OpenVpnConfiguration.Quote(push)}\n");
        }

        text.Append($"route 0.0.0.0 0.0.0.0 vpn_gateway {ExitRouteMetric}\n");
        text.Append("verb 3\n");
        foreach (var (name, content) in configuration.Blocks)
        {
            text.Append($"<{name}>\n").Append(content).Append($"</{name}>\n");
        }

        return text.ToString();
    }

    /// <summary>The two-line file OpenVPN reads for a SOCKS5 proxy or account login.</summary>
    public static string WriteCredentials(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) ||
            username.Any(char.IsControl) || password.Any(char.IsControl))
        {
            throw new ConfigurationValidationException(["OpenVPN credentials cannot be empty or contain line breaks."]);
        }

        return $"{username}\n{password}\n";
    }

    // OpenVPN reads forward slashes on Windows, which avoids escaping backslashes in quotes.
    private static string QuotePath(string path) => OpenVpnConfiguration.Quote(path.Replace('\\', '/'));
}
