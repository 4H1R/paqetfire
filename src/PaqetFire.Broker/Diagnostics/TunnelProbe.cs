using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Diagnostics;

public interface ITunnelProbe
{
    /// <summary>Round-trip time of a tiny HTTP request over a warm tunnel connection.</summary>
    ValueTask<int?> MeasureLatencyAsync(CancellationToken cancellationToken);

    ValueTask<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken);

    /// <summary>The address destinations see: the exit's when the route uses one, otherwise Paqet's.</summary>
    ValueTask<IPAddress?> GetPublicAddressAsync(bool throughExit, CancellationToken cancellationToken) =>
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
        GetPublicAddressAsync(throughExit: false, cancellationToken);

    public async ValueTask<IPAddress?> GetPublicAddressAsync(bool throughExit, CancellationToken cancellationToken)
    {
        try
        {
            var text = (await (throughExit ? exitClient : client).GetStringAsync(PublicAddressUri, cancellationToken)
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
    }

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
