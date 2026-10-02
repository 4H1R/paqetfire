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
}

/// <summary>
/// Probes the Paqet SOCKS5 listener directly, bypassing Xray so probe bytes are not
/// counted as user traffic and destination rules cannot send them direct.
/// </summary>
public sealed class PaqetTunnelProbe : ITunnelProbe, IDisposable
{
    private static readonly Uri LatencyUri = new("http://cp.cloudflare.com/generate_204");
    private static readonly Uri PublicAddressUri = new("https://api4.ipify.org");
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan WarmConnectionLifetime = TimeSpan.FromSeconds(20);

    private readonly HttpClient client;
    private long lastSuccessTimestamp;

    public PaqetTunnelProbe()
    {
        client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{XrayJsonConfigurationWriter.PaqetPort}")),
            UseProxy = true,
            ConnectTimeout = ProbeTimeout,
            PooledConnectionIdleTimeout = WarmConnectionLifetime + TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1,
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

    public async ValueTask<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = (await client.GetStringAsync(PublicAddressUri, cancellationToken).ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out var address) ? address : null;
        }
        catch (Exception exception) when (IsProbeFailure(exception, cancellationToken))
        {
            return null;
        }
    }

    public void Dispose() => client.Dispose();

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
