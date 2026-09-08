using System.Net;
using System.Net.Sockets;

namespace PaqetFire.Desktop.Presentation;

public static class HotspotEndpointText
{
    public static string CreateUri(string address, int port, string username, string password)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            port is < 1024 or > 65535 || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return string.Empty;
        }

        return $"socks5://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@{ip}:{port}";
    }
}
