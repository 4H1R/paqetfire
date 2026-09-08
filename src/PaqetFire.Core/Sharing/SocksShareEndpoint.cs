using System.Net;
using System.Net.Sockets;

namespace PaqetFire.Core.Sharing;

public sealed record SocksShareEndpoint
{
    public SocksShareEndpoint(string address, int port)
    {
        if (!IPAddress.TryParse(address, out var parsedAddress) ||
            parsedAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("A LAN share endpoint requires a valid IPv4 address.", nameof(address));
        }

        if (port is < 1024 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "A LAN share port must be between 1024 and 65535.");
        }

        Address = parsedAddress.ToString();
        Port = port;
    }

    public string Address { get; }

    public int Port { get; }

    public override string ToString() => $"{Address}:{Port}";
}
