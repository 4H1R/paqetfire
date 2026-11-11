using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;
using PaqetFire.Desktop.Presentation;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class EffectiveRoutingSummaryTests
{
    private static string Summary(PaqetFireSettings settings) =>
        EffectiveRoutingSummary.Create(PaqetFireSettingsView.FromSettings(settings));

    private static PaqetFireSettings Configured() => new()
    {
        TransportKey = "configured",
        RoutingMode = RoutingMode.AllApplications,
        RegionalPreset = RegionalRoutingPreset.IranDirect,
        BypassLan = true,
        DirectBitTorrent = true,
    };

    [Fact]
    public void DefaultProfileListsRouteWithoutExitOrSharing() =>
        Assert.Equal(
            "All supported apps · Iran direct · LAN bypass on · BitTorrent direct · no custom direct destinations · Kill switch off",
            Summary(Configured()));

    [Fact]
    public void SharingIsListed() =>
        Assert.EndsWith(
            "Kill switch on · LAN share on · Hotspot share on",
            Summary(Configured() with { KillSwitchEnabled = true, ShareWithLan = true, ShareViaHotspot = true }));

    [Theory]
    [InlineData("DE", "Exit: Psiphon (DE)")]
    [InlineData("", "Exit: Psiphon")]
    public void PsiphonExitNamesItsCountry(string region, string expected) =>
        Assert.Contains(
            $"Iran direct · {expected} · LAN bypass on",
            Summary(Configured() with { ExitProxy = ExitProxyKind.Psiphon, Psiphon = new PsiphonExitOptions(region) }));

    [Fact]
    public void OpenVpnExitIsListed() =>
        Assert.Contains("· Exit: OpenVPN ·", Summary(Configured() with { ExitProxy = ExitProxyKind.OpenVpn }));

    [Fact]
    public void MissingSettingsAskForSetup() =>
        Assert.Equal("Set up a profile to preview which traffic will use PaqetFire.", EffectiveRoutingSummary.Create(null));
}
