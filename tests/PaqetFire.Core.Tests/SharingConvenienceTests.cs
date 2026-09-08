using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PaqetFire.Core.Sharing;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class SharingConvenienceTests
{
    [Fact]
    public void QrPayloadIsAnEscapedImportableSocksUri()
    {
        var endpoint = new SocksShareEndpoint("192.168.137.1", 10808);
        var credentials = new SharedProxyCredentials("user:name@host", " pass/#?:@% ");

        var payload = SocksShareUri.CreateQrPayload(endpoint, credentials);
        var uri = new Uri(payload);
        var userInfo = uri.UserInfo.Split(':');

        Assert.Equal("socks5", uri.Scheme);
        Assert.Equal("192.168.137.1", uri.Host);
        Assert.Equal(10808, uri.Port);
        Assert.Equal("user:name@host", Uri.UnescapeDataString(userInfo[0]));
        Assert.Equal(" pass/#?:@% ", Uri.UnescapeDataString(userInfo[1]));
        Assert.Empty(uri.Query);
        Assert.Empty(uri.Fragment);
    }

    [Fact]
    public void CredentialRotationUsesAValidPasswordAndNeverPrintsIt()
    {
        var credentials = new SharedProxyCredentialGenerator().Rotate("paqetfire", 32);

        Assert.Equal(32, credentials.Password.Length);
        Assert.DoesNotContain(credentials.Password, credentials.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", credentials.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(129)]
    public void CredentialRotationRejectsUnsafeLengths(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SharedProxyCredentialGenerator().Rotate("paqetfire", length));
    }

    [Fact]
    public void RedactedBundleContainsSetupDataButNeverThePassword()
    {
        const string secret = "correct-horse-battery";
        var endpoint = new SocksShareEndpoint("192.168.137.1", 10808);
        var credentials = new SharedProxyCredentials("paqetfire", secret);

        var json = RedactedShareSetupBundle.Create(endpoint, credentials, "Phone hotspot").ToJson();

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("socks5", root.GetProperty("protocol").GetString());
        Assert.Equal("192.168.137.1", root.GetProperty("address").GetString());
        Assert.Equal(10808, root.GetProperty("port").GetInt32());
        Assert.Equal("paqetfire", root.GetProperty("username").GetString());
        Assert.True(root.GetProperty("requiresPassword").GetBoolean());
        Assert.False(root.TryGetProperty("password", out _));
        Assert.DoesNotContain('@', root.GetProperty("endpointUri").GetString()!);
    }

    [Fact]
    public async Task ReachabilityProbeReportsListeningTcpEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = new SocksShareEndpoint(IPAddress.Loopback.ToString(), port);

        var result = await new TcpShareReachabilityProbe()
            .CheckAsync(endpoint, TimeSpan.FromSeconds(2));

        Assert.Equal(ShareReachabilityStatus.Reachable, result.Status);
        Assert.Equal(endpoint, result.Endpoint);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Contains("Authentication was not tested", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReachabilityProbePropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TcpShareReachabilityProbe().CheckAsync(
                new SocksShareEndpoint("192.0.2.1", 10808),
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }
}
