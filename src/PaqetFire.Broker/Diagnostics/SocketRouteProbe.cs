using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PaqetFire.Broker.Diagnostics;

public sealed class SocketRouteProbe : IRouteProbe
{
    private static readonly IPEndPoint XrayEndpoint = new(IPAddress.Loopback, 1081);
    private static readonly Uri Ipv4CheckUri = new("https://api4.ipify.org");
    private static readonly Uri Ipv6CheckUri = new("https://api6.ipify.org");
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    public async ValueTask<RouteProbeResult> CheckLocalRouteAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(XrayEndpoint, deadline.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await NegotiateNoAuthenticationAsync(stream, deadline.Token).ConfigureAwait(false);
            return Success("The local Xray SOCKS5 listener accepted a protocol handshake.", stopwatch);
        }
        catch (Exception exception) when (IsProbeFailure(exception, cancellationToken))
        {
            return Failure("The local Xray SOCKS5 listener did not accept a handshake.", stopwatch, available: true);
        }
    }

    public ValueTask<RouteProbeResult> CheckTcpIpv4Async(CancellationToken cancellationToken) =>
        CheckPublicAddressAsync(Ipv4CheckUri, AddressFamily.InterNetwork, "IPv4", cancellationToken);

    public ValueTask<RouteProbeResult> CheckIpv6Async(CancellationToken cancellationToken) =>
        CheckPublicAddressAsync(Ipv6CheckUri, AddressFamily.InterNetworkV6, "IPv6", cancellationToken);

    public async ValueTask<RouteProbeResult> CheckUdpDnsAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            using var control = new TcpClient(AddressFamily.InterNetwork);
            await control.ConnectAsync(XrayEndpoint, deadline.Token).ConfigureAwait(false);
            var stream = control.GetStream();
            await NegotiateNoAuthenticationAsync(stream, deadline.Token).ConfigureAwait(false);
            var relayEndpoint = await RequestUdpRelayAsync(stream, deadline.Token).ConfigureAwait(false);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(relayEndpoint);
            var transactionId = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            var query = CreateDnsQuery(transactionId);
            var packet = CreateSocksUdpPacket(IPAddress.Parse("1.1.1.1"), 53, query);
            await udp.SendAsync(packet, deadline.Token).ConfigureAwait(false);
            var received = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (!TryReadDnsResponse(received.Buffer, transactionId))
            {
                return Failure("The SOCKS5 UDP relay returned an invalid DNS response.", stopwatch, available: true);
            }

            return Success("A DNS query received a valid response through Xray SOCKS5 UDP associate.", stopwatch);
        }
        catch (Exception exception) when (IsProbeFailure(exception, cancellationToken))
        {
            return Failure(
                "No DNS response arrived through Xray SOCKS5 UDP associate. The probe endpoint or UDP route may be unavailable.",
                stopwatch,
                available: false);
        }
    }

    private static async ValueTask<RouteProbeResult> CheckPublicAddressAsync(
        Uri endpoint,
        AddressFamily expectedFamily,
        string familyName,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var deadline = CreateDeadline(cancellationToken);
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(new Uri($"socks5://{XrayEndpoint.Address}:{XrayEndpoint.Port}")),
                UseProxy = true,
                ConnectTimeout = ProbeTimeout,
            };
            using var client = new HttpClient(handler)
            {
                Timeout = ProbeTimeout,
            };
            using var response = await client.GetAsync(endpoint, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = (await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)).Trim();
            if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != expectedFamily)
            {
                return Failure($"The route check returned an invalid {familyName} address.", stopwatch, available: false);
            }

            return new RouteProbeResult(
                true,
                $"An HTTPS request completed through Xray and returned a public {familyName} address.",
                stopwatch.ElapsedMilliseconds,
                text);
        }
        catch (Exception exception) when (IsProbeFailure(exception, cancellationToken))
        {
            return Failure(
                $"The external {familyName} route check could not complete. The check provider or this address family may be unavailable.",
                stopwatch,
                available: false);
        }
    }

    private static async ValueTask NegotiateNoAuthenticationAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken).ConfigureAwait(false);
        var response = new byte[2];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 0x05 || response[1] != 0x00)
        {
            throw new IOException("The Xray listener rejected the SOCKS5 handshake.");
        }
    }

    private static async ValueTask<IPEndPoint> RequestUdpRelayAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(
            new byte[] { 0x05, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            cancellationToken).ConfigureAwait(false);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0x05 || header[1] != 0x00)
        {
            throw new IOException($"SOCKS5 UDP associate failed with reply {header[1]}.");
        }

        IPAddress address;
        switch (header[3])
        {
            case 0x01:
                var ipv4 = new byte[4];
                await stream.ReadExactlyAsync(ipv4, cancellationToken).ConfigureAwait(false);
                address = new IPAddress(ipv4);
                break;
            case 0x04:
                var ipv6 = new byte[16];
                await stream.ReadExactlyAsync(ipv6, cancellationToken).ConfigureAwait(false);
                address = new IPAddress(ipv6);
                break;
            case 0x03:
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
                var hostBytes = new byte[length[0]];
                await stream.ReadExactlyAsync(hostBytes, cancellationToken).ConfigureAwait(false);
                var host = System.Text.Encoding.ASCII.GetString(hostBytes);
                address = (await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false))
                    .First(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
                break;
            default:
                throw new IOException("The SOCKS5 UDP relay returned an unsupported address type.");
        }

        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes, cancellationToken).ConfigureAwait(false);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            address = IPAddress.Loopback;
        }

        return new IPEndPoint(address, port);
    }

    private static byte[] CreateDnsQuery(ushort transactionId)
    {
        var bytes = new List<byte>(32);
        bytes.Add((byte)(transactionId >> 8));
        bytes.Add((byte)transactionId);
        bytes.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        foreach (var label in new[] { "example", "com" })
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 });
        return bytes.ToArray();
    }

    private static byte[] CreateSocksUdpPacket(IPAddress destination, ushort port, byte[] payload)
    {
        var address = destination.GetAddressBytes();
        var packet = new byte[4 + address.Length + 2 + payload.Length];
        packet[3] = destination.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
        address.CopyTo(packet, 4);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4 + address.Length, 2), port);
        payload.CopyTo(packet, 6 + address.Length);
        return packet;
    }

    private static bool TryReadDnsResponse(byte[] packet, ushort transactionId)
    {
        if (packet.Length < 10 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0)
        {
            return false;
        }

        var addressLength = packet[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 when packet.Length > 4 => 1 + packet[4],
            _ => -1,
        };
        var dnsOffset = 4 + addressLength + 2;
        return addressLength > 0 && packet.Length >= dnsOffset + 4 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(dnsOffset, 2)) == transactionId &&
            (packet[dnsOffset + 2] & 0x80) != 0;
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(ProbeTimeout);
        return source;
    }

    private static bool IsProbeFailure(Exception exception, CancellationToken callerToken) =>
        exception is IOException or SocketException or HttpRequestException or InvalidOperationException ||
        exception is OperationCanceledException && !callerToken.IsCancellationRequested;

    private static RouteProbeResult Success(string detail, Stopwatch stopwatch) =>
        new(true, detail, stopwatch.ElapsedMilliseconds);

    private static RouteProbeResult Failure(
        string detail,
        Stopwatch stopwatch,
        bool available) => new(false, detail, stopwatch.ElapsedMilliseconds, IsAvailable: available);
}
