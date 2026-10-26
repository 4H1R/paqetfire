using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Diagnostics;

public interface ITunnelProbe
{
    /// <summary>Round-trip time of a tiny HTTP request over a warm tunnel connection.</summary>
    ValueTask<int?> MeasureLatencyAsync(CancellationToken cancellationToken);

    ValueTask<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken);

    /// <summary>The address destinations see: the exit's when the route uses one, otherwise Paqet's.</summary>
    ValueTask<IPAddress?> GetPublicAddressAsync(ExitProxyOutbound? exit, CancellationToken cancellationToken) =>
        GetPublicAddressAsync(cancellationToken);

    /// <summary>Whether a fresh SOCKS stream through Paqet reaches either connectivity endpoint.</summary>
    ValueTask<bool> CheckReachabilityAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Probes the Paqet SOCKS5 listener directly, bypassing Xray so probe bytes are not
/// counted as user traffic and destination rules cannot send them direct.
/// </summary>
public sealed class PaqetTunnelProbe : ITunnelProbe, IDisposable
{
    private static readonly Uri LatencyUri = new("http://cp.cloudflare.com/generate_204");
    private static readonly Uri PublicAddressUri = new("https://api4.ipify.org");

    // An IP literal: a socket bound to an exit adapter must not need a DNS lookup first.
    private static readonly Uri AdapterAddressUri = new("https://1.1.1.1/cdn-cgi/trace");

    // IP_UNICAST_IF, which Windows' headers define and .NET does not name.
    private const SocketOptionName UnicastInterface = (SocketOptionName)31;
    private static readonly Uri[] ReachabilityUris =
    [
        LatencyUri,
        new("http://connectivitycheck.gstatic.com/generate_204"),
    ];
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    // Short enough that three failed probes fit in about twelve seconds; a busy tunnel
    // that misses it is still recognized by its downlink traffic.
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WarmConnectionLifetime = TimeSpan.FromSeconds(20);

    private readonly HttpClient client;
    private readonly HttpClient reachabilityClient;
    private readonly HttpClient exitClient;
    private readonly ConcurrentDictionary<string, HttpClient> adapterClients = new(StringComparer.OrdinalIgnoreCase);
    private long lastSuccessTimestamp;

    public PaqetTunnelProbe(EngineSocksCredentials credentials)
    {
        client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{XrayJsonConfigurationWriter.PaqetPort}"))
            {
                Credentials = new NetworkCredential(credentials.Username, credentials.Password),
            },
            UseProxy = true,
            ConnectTimeout = ProbeTimeout,
            PooledConnectionIdleTimeout = WarmConnectionLifetime + TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1,
        })
        {
            Timeout = ProbeTimeout,
        };

        // No pooling: a warm stream can outlive a dead carrier for a while, while each
        // new stream must be negotiated through Paqet with the server.
        reachabilityClient = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{XrayJsonConfigurationWriter.PaqetPort}"))
            {
                Credentials = new NetworkCredential(credentials.Username, credentials.Password),
            },
            UseProxy = true,
            ConnectTimeout = ReachabilityTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
        })
        {
            Timeout = ReachabilityTimeout,
        };

        // The exit engine's loopback listener takes no credentials.
        exitClient = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{PsiphonJsonConfigurationWriter.LocalSocksPort}")),
            UseProxy = true,
            ConnectTimeout = ProbeTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
        })
        {
            Timeout = ProbeTimeout,
        };
    }

    public async ValueTask<int?> MeasureLatencyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The first request pays for the SOCKS and TCP setup; only a request on an
            // already pooled connection reflects the tunnel round trip.
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastSuccessTimestamp)) > WarmConnectionLifetime)
            {
                await SendLatencyRequestAsync(cancellationToken).ConfigureAwait(false);
            }

            var started = Stopwatch.GetTimestamp();
            await SendLatencyRequestAsync(cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);
            Interlocked.Exchange(ref lastSuccessTimestamp, Stopwatch.GetTimestamp());
            return (int)Math.Clamp(Math.Round(elapsed.TotalMilliseconds), 1, int.MaxValue);
        }
        catch (Exception exception) when (IsProbeFailure(exception, cancellationToken))
        {
            Interlocked.Exchange(ref lastSuccessTimestamp, 0);
            return null;
        }
    }

    public ValueTask<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken) =>
        GetPublicAddressAsync(exit: null, cancellationToken);

    public async ValueTask<IPAddress?> GetPublicAddressAsync(ExitProxyOutbound? exit, CancellationToken cancellationToken)
    {
        try
        {
            if (exit is InterfaceExitOutbound adapter)
            {
                // Cloudflare's trace answers with lines such as "ip=203.0.113.7".
                var trace = await adapterClients.GetOrAdd(adapter.InterfaceName, CreateAdapterClient)
                    .GetStringAsync(AdapterAddressUri, cancellationToken)
                    .ConfigureAwait(false);
                var line = trace.Split('\n').FirstOrDefault(entry => entry.StartsWith("ip=", StringComparison.Ordinal));
                return IPAddress.TryParse(line?[3..].Trim(), out var traced) ? traced : null;
            }

            var text = (await (exit is SocksExitOutbound ? exitClient : client).GetStringAsync(PublicAddressUri, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out var address) ? address : null;
        }
        catch (Exception exception) when (IsProbeFailure(exception, cancellationToken))
        {
            return null;
        }
    }

    public async ValueTask<bool> CheckReachabilityAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = ReachabilityUris.Select(uri => ReachAsync(uri, linked.Token)).ToList();
        while (attempts.Count > 0)
        {
            var completed = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(completed);
            if (await completed.ConfigureAwait(false))
            {
                await linked.CancelAsync().ConfigureAwait(false);
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public void Dispose()
    {
        client.Dispose();
        reachabilityClient.Dispose();
        exitClient.Dispose();
        foreach (var adapterClient in adapterClients.Values)
        {
            adapterClient.Dispose();
        }
    }

    /// <summary>
    /// Connects the way Xray's exit outbound does: bound to the exit adapter, so the probe
    /// fails while its tunnel is down instead of reporting the address of another route.
    /// </summary>
    private static HttpClient CreateAdapterClient(string adapterName) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = ProbeTimeout,
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var index = NetworkInterface.GetAllNetworkInterfaces()
                            .FirstOrDefault(candidate =>
                                candidate.OperationalStatus == OperationalStatus.Up &&
                                string.Equals(candidate.Name, adapterName, StringComparison.OrdinalIgnoreCase))?
                            .GetIPProperties().GetIPv4Properties()?.Index ??
                        throw new IOException($"The {adapterName} adapter is not connected.");
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // IPv4 takes the interface index in network byte order.
                socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, IPAddress.HostToNetworkOrder(index));
                await socket.ConnectAsync(IPAddress.Parse(context.DnsEndPoint.Host), context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    {
        Timeout = ProbeTimeout,
    };

    private async Task<bool> ReachAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await reachabilityClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is IOException or SocketException or HttpRequestException or
                                              InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SendLatencyRequestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, LatencyUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static bool IsProbeFailure(Exception exception, CancellationToken callerToken) =>
        exception is IOException or SocketException or HttpRequestException or InvalidOperationException ||
        exception is OperationCanceledException && !callerToken.IsCancellationRequested;
}
