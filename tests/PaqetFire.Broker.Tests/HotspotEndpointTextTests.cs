using PaqetFire.Desktop.Presentation;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class HotspotEndpointTextTests
{
    [Theory]
    [InlineData("user:name@host", " pass/#?:@% ")]
    [InlineData("paqetfire", "password123")]
    public void UriRoundTripPreservesCredentials(string username, string password)
    {
        var uri = new Uri(HotspotEndpointText.CreateUri("192.168.137.1", 10808, username, password));
        var credentials = uri.UserInfo.Split(':');
        Assert.Equal(2, credentials.Length);
        Assert.Equal(username, Uri.UnescapeDataString(credentials[0]));
        Assert.Equal(password, Uri.UnescapeDataString(credentials[1]));
        Assert.Equal("192.168.137.1", uri.Host);
        Assert.Equal(10808, uri.Port);
        Assert.Empty(uri.Fragment);
        Assert.Empty(uri.Query);
    }

    [Theory]
    [InlineData("192.168.137.1", 10808, "paqetfire", "")]
    [InlineData("", 10808, "paqetfire", "password123")]
    [InlineData("192.168.137.1", 0, "paqetfire", "password123")]
    public void IncompleteEndpointCannotBeImported(string address, int port, string username, string password)
    {
        Assert.Empty(HotspotEndpointText.CreateUri(address, port, username, password));
    }
}
