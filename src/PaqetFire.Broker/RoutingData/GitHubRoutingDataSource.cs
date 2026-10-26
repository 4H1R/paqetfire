using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.RoutingData;

namespace PaqetFire.Broker.RoutingData;

public enum RoutingDataNetworkPath
{
    /// <summary>The broker's own connection; the broker is excluded from application capture.</summary>
    Direct,

    /// <summary>Through the local Paqet SOCKS5 hop, for networks where GitHub is filtered.</summary>
    Tunnel,
}

public interface IRoutingDataSource
{
    /// <summary>The tag of the newest published release.</summary>
    Task<string> GetLatestTagAsync(RoutingDataNetworkPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a GitHub asset into <paramref name="destination"/>, failing past <paramref name="maximumBytes"/>.
    /// Also used for the exit engines the broker downloads on request.
    /// </summary>
    Task DownloadAsync(
        RoutingDataNetworkPath path,
        Uri uri,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class GitHubRoutingDataSource : IRoutingDataSource, IDisposable
{
    private const int MaximumApiResponseBytes = 1024 * 1024;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    // A slow tunnel may need minutes for the larger file; a connection that stops
    // delivering anything for this long is abandoned.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(20);

    private readonly HttpClient direct;
    private readonly HttpClient tunnel;

    public GitHubRoutingDataSource(EngineSocksCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        direct = CreateClient(null);
        tunnel = CreateClient(new WebProxy(new Uri($"socks5://127.0.0.1:{XrayJsonConfigurationWriter.PaqetPort}"))
        {
            Credentials = new NetworkCredential(credentials.Username, credentials.Password),
        });
    }

    public async Task<string> GetLatestTagAsync(RoutingDataNetworkPath path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ApiTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RoutingDataRelease.LatestReleaseApiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await Client(path)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var body = new MemoryStream();
            await CopyBoundedAsync(response, body, MaximumApiResponseBytes, ApiTimeout, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body.ToArray());
            return document.RootElement.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
                ? tag.GetString()!
                : throw new InvalidDataException("GitHub's release answer did not name a release.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub did not answer the release check in time.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("GitHub's release answer could not be read.");
        }
    }

    public async Task DownloadAsync(
        RoutingDataNetworkPath path,
        Uri uri,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadDeadline);
        try
        {
            using var response = await Client(path)
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await CopyBoundedAsync(response, destination, maximumBytes, StallTimeout, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The download from GitHub timed out.");
        }
    }

    public void Dispose()
    {
        direct.Dispose();
        tunnel.Dispose();
    }

    private HttpClient Client(RoutingDataNetworkPath path) =>
        path == RoutingDataNetworkPath.Tunnel ? tunnel : direct;

    private static async Task CopyBoundedAsync(
        HttpResponseMessage response,
        Stream destination,
        long maximumBytes,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The downloaded file is larger than PaqetFire accepts.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            stall.CancelAfter(stallTimeout);
            var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("The downloaded file is larger than PaqetFire accepts.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpClient CreateClient(IWebProxy? proxy)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            // Direct means direct: a system proxy would defeat the fallback's purpose.
            Proxy = proxy,
            UseProxy = proxy is not null,
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var version = typeof(GitHubRoutingDataSource).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(
            ProductInfoHeaderValue.TryParse($"PaqetFire/{version}", out var userAgent)
                ? userAgent
                : new ProductInfoHeaderValue("PaqetFire", "1.0"));
        return client;
    }
}
