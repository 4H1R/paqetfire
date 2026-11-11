using PaqetFire.Desktop.Presentation;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class LanShareStatusTextTests
{
    [Fact]
    public void UnsavedShareAsksForSave() =>
        Assert.Equal("On — save to apply", LanShareStatusText.OnContent(false, null, 1082, connected: true, "192.168.1.20"));

    [Fact]
    public void ChangedPortAsksForSave() =>
        Assert.Equal("On — save to apply", LanShareStatusText.OnContent(true, 1082, 1090, connected: true, "192.168.1.20"));

    [Fact]
    public void SavedShareWaitsForTheConnection() =>
        Assert.Equal("On — starts when you connect", LanShareStatusText.OnContent(true, 1082, 1082, connected: false, "192.168.1.20"));

    [Theory]
    [InlineData("192.168.1.20", "On — listening on 192.168.1.20:1082")]
    [InlineData("", "On — listening on port 1082")]
    public void ConnectedShareNamesTheEndpoint(string address, string expected) =>
        Assert.Equal(expected, LanShareStatusText.OnContent(true, 1082, 1082, connected: true, address));

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void UntrustedOrUnknownNetworkIsWarned(bool? trusted) =>
        Assert.Equal("Anyone on Home Wi-Fi can reach this proxy with the password.",
            LanShareStatusText.ExposureWarning(true, trusted, "Home Wi-Fi"));

    [Fact]
    public void UnnamedNetworkIsStillWarned() =>
        Assert.Equal("Anyone on this network can reach this proxy with the password.",
            LanShareStatusText.ExposureWarning(true, null, " "));

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, true)]
    public void NoWarningWhenOffOrTrusted(bool shareOn, bool? trusted) =>
        Assert.Null(LanShareStatusText.ExposureWarning(shareOn, trusted, "Cafe"));
}
