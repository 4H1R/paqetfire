using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaqetFire.Core.Updates;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Fact]
    public async Task CheckFindsNewerReleaseAndItsInstaller()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(
            ReleaseJson("v0.7.0", includeChecksum: true))));
        var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckAsync(new Version(0, 6, 18, 0));

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(0, 7, 0), result.LatestRelease.Version);
        Assert.EndsWith("PaqetFire-0.7.0-x64.msi", result.LatestRelease.InstallerUri.AbsoluteUri);
    }

    [Fact]
    public async Task CheckTreatsMissingAssemblyRevisionAsSameRelease()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(
            ReleaseJson("v0.6.18", includeChecksum: true))));
        var service = new GitHubUpdateService(httpClient);

        var result = await service.CheckAsync(new Version(0, 6, 18, 0));

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckRejectsReleaseWithoutChecksum()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(
            ReleaseJson("v0.7.0", includeChecksum: false))));
        var service = new GitHubUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CheckAsync(new Version(0, 6, 18)));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckRejectsAssetsOutsidePaqetFireRepository()
    {
        var releaseJson = ReleaseJson("v0.7.0", includeChecksum: true)
            .Replace("github.com/4H1R/paqetfire", "example.com/downloads", StringComparison.Ordinal);
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(releaseJson)));
        var service = new GitHubUpdateService(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CheckAsync(new Version(0, 6, 18)));

        Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAcceptsOnlyInstallerMatchingPublishedChecksum()
    {
        var installerBytes = Encoding.UTF8.GetBytes("test MSI payload");
        var checksum = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        var downloadDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaqetFire.Tests",
            Guid.NewGuid().ToString("N"));
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{checksum}  PaqetFire-0.7.0-x64.msi"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installerBytes),
                }));
        var service = new GitHubUpdateService(httpClient, downloadDirectory);
        var release = new SoftwareRelease(
            new Version(0, 7, 0),
            "v0.7.0",
            new Uri("https://github.com/4H1R/paqetfire/releases/tag/v0.7.0"),
            new Uri("https://github.com/4H1R/paqetfire/releases/download/v0.7.0/PaqetFire-0.7.0-x64.msi"),
            new Uri("https://github.com/4H1R/paqetfire/releases/download/v0.7.0/PaqetFire-0.7.0-x64.msi.sha256"));

        try
        {
            var path = await service.DownloadVerifiedInstallerAsync(release);

            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(downloadDirectory))
            {
                Directory.Delete(downloadDirectory, recursive: true);
            }
        }
    }

    private static string ReleaseJson(string tag, bool includeChecksum)
    {
        var version = tag.TrimStart('v');
        var installerName = $"PaqetFire-{version}-x64.msi";
        var checksumAsset = includeChecksum
            ? $",{{\"name\":\"{installerName}.sha256\",\"browser_download_url\":\"https://github.com/4H1R/paqetfire/releases/download/{tag}/{installerName}.sha256\"}}"
            : string.Empty;
        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/4H1R/paqetfire/releases/tag/{{tag}}",
              "assets": [
                {
                  "name": "{{installerName}}",
                  "browser_download_url": "https://github.com/4H1R/paqetfire/releases/download/{{tag}}/{{installerName}}"
                }{{checksumAsset}}
              ]
            }
            """;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
