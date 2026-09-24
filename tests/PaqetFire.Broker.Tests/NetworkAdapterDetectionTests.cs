using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using PaqetFire.Broker.Network;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Network;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class NetworkAdapterDetectionTests
{
    private static readonly NetworkAdapterDetails Ethernet = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "Ethernet",
        IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.1"), NetworkInterfaceType.Ethernet);
    private static readonly NetworkAdapterDetails Wifi = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Wi-Fi",
        IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.1"), NetworkInterfaceType.Wireless80211);

    [Fact]
    public void SwitchingFromHotspotToEthernetClearsOldMacWhenLookupFails()
    {
        IReadOnlyList<NetworkAdapterDetails> adapters = [Wifi];
        string? mac = "02:11:22:33:44:55";
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(() => adapters, (_, _) => mac));
        preview.Refresh(null);
        Assert.Equal(mac, preview.Detected!.RouterMac);

        adapters = [Ethernet];
        mac = null;
        preview.Refresh(null);
        Assert.Equal(Ethernet, preview.Detected!.Adapter);
        Assert.Null(preview.Detected.RouterMac);

        mac = "02:AA:BB:CC:DD:EE";
        preview.Refresh(null);
        Assert.Equal(mac, preview.Detected.RouterMac);
    }

    [Fact]
    public void MissingSelectedInterfaceClearsPreviewAndDoesNotFallBack()
    {
        IReadOnlyList<NetworkAdapterDetails> adapters = [Wifi, Ethernet];
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(() => adapters, (_, _) => "02:11:22:33:44:55"));
        preview.Refresh(Wifi.InterfaceGuid);
        adapters = [Ethernet];
        Assert.Throws<InvalidOperationException>(() => preview.Refresh(Wifi.InterfaceGuid));
        Assert.Null(preview.Detected);
        Assert.Equal(Wifi.InterfaceGuid, preview.SelectedInterfaceGuid);
        Assert.Contains(preview.Options, option => option.InterfaceGuid == Wifi.InterfaceGuid &&
            option.DisplayName.StartsWith("Unavailable interface", StringComparison.Ordinal));
        preview.Refresh(null);
        Assert.Equal(Ethernet, preview.Detected!.Adapter);
    }

    [Fact]
    public void EnumerationFailureClearsPreviousPreview()
    {
        var fail = false;
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(
            () => fail ? throw new NetworkInformationException() : [Wifi], (_, _) => "02:11:22:33:44:55"));
        preview.Refresh(null);
        fail = true;
        Assert.Throws<NetworkInformationException>(() => preview.Refresh(null));
        Assert.Null(preview.Detected);
        Assert.Null(Assert.Single(preview.Options).InterfaceGuid);
    }

    [Fact]
    public void PickerAndPreviewUseOneEnumerationPerRefresh()
    {
        var reads = 0;
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(
            () => ++reads == 1 ? [Wifi, Ethernet] : [Wifi], (_, _) => "02:11:22:33:44:55"));
        preview.Refresh(null);
        Assert.Equal(1, reads);
        Assert.Equal(Ethernet, preview.Detected!.Adapter);
        Assert.Equal(new Guid?[] { null, Ethernet.InterfaceGuid, Wifi.InterfaceGuid },
            preview.Options.Select(option => option.InterfaceGuid));
    }

    [Fact]
    public void LoadingAProfileListsInterfacesWithoutResolvingMacOrKeepingPreviousDetails()
    {
        var lookups = 0;
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(() => [Wifi, Ethernet], (_, _) =>
        {
            lookups++;
            return "02:11:22:33:44:55";
        }));
        preview.Refresh(Wifi.InterfaceGuid);
        preview.Refresh(Ethernet.InterfaceGuid, resolveRouterMac: false);
        Assert.Equal(1, lookups);
        Assert.Null(preview.Detected);
        Assert.Equal(Ethernet.InterfaceGuid, preview.SelectedInterfaceGuid);
        Assert.Equal(3, preview.Options.Count);
    }

    [Fact]
    public void EnumerationFailurePreservesSelectedInterfaceAndOnlyOneAutomaticOption()
    {
        var preview = new NetworkAdapterPreview(new NetworkAdapterDetector(
            () => throw new NetworkInformationException(), (_, _) => throw new InvalidOperationException()));
        Assert.Throws<NetworkInformationException>(() => preview.Refresh(Wifi.InterfaceGuid));
        Assert.Equal(Wifi.InterfaceGuid, preview.SelectedInterfaceGuid);
        Assert.Equal(new Guid?[] { null, Wifi.InterfaceGuid }, preview.Options.Select(option => option.InterfaceGuid));
    }

    [Fact]
    public void LegacyDetectedValuesAreIgnoredAndNotSavedAgain()
    {
        var preferences = JsonSerializer.Deserialize<DesktopPreferences>("""
            {"InterfaceName":"Wi-Fi","InterfaceGuid":"22222222-2222-2222-2222-222222222222",
             "LocalIpv4Address":"192.168.1.20","RouterMac":"02:11:22:33:44:55"}
            """)!;
        Assert.Null(preferences.NetworkInterfaceGuid);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(preferences));
        Assert.False(json.RootElement.TryGetProperty("InterfaceGuid", out _));
        Assert.False(json.RootElement.TryGetProperty("RouterMac", out _));
        Assert.False(json.RootElement.TryGetProperty("LocalIpv4Address", out _));
    }

    [Fact]
    public void AutomaticPrefersEthernetButExplicitSelectionUsesWifiAndItsSourceAddress()
    {
        var lookups = new List<(IPAddress Gateway, IPAddress Source)>();
        var detector = new NetworkAdapterDetector(() => [Wifi, Ethernet], (gateway, source) =>
        {
            lookups.Add((gateway, source));
            return source.Equals(Ethernet.LocalAddress) ? "02:AA:BB:CC:DD:EE" : "02:11:22:33:44:55";
        });
        var broker = new NetworkEnvironmentDetector(detector);
        var automatic = broker.Detect();
        var selected = broker.Detect(Wifi.InterfaceGuid);
        Assert.Equal("Ethernet", automatic.InterfaceName);
        Assert.Equal("02:AA:BB:CC:DD:EE", automatic.GatewayMacAddress);
        Assert.Equal("Wi-Fi", selected.InterfaceName);
        Assert.Equal(Wifi.InterfaceGuid.ToString("D"), selected.InterfaceGuid);
        Assert.Equal("02:11:22:33:44:55", selected.GatewayMacAddress);
        Assert.Equal(new[] { (Ethernet.Gateway, Ethernet.LocalAddress), (Wifi.Gateway, Wifi.LocalAddress) }, lookups);
    }

    [Fact]
    public void BrokerRejectsUnresolvedMacOnSelectedInterface()
    {
        var broker = new NetworkEnvironmentDetector(new NetworkAdapterDetector(() => [Wifi, Ethernet], (_, _) => null));
        var exception = Assert.Throws<InvalidOperationException>(() => broker.Detect(Ethernet.InterfaceGuid));
        Assert.Contains("Ethernet", exception.Message);
    }

    [Fact]
    public void SameInterfaceOnANewNetworkRefreshesBothGatewayAndMac()
    {
        var adapter = Wifi;
        var detector = new NetworkAdapterDetector(() => [adapter], (gateway, _) =>
            gateway.Equals(Wifi.Gateway) ? "02:11:22:33:44:55" : "02:AA:BB:CC:DD:EE");
        var preview = new NetworkAdapterPreview(detector);
        preview.Refresh(Wifi.InterfaceGuid);
        adapter = Wifi with { LocalAddress = IPAddress.Parse("172.20.10.2"), Gateway = IPAddress.Parse("172.20.10.1") };
        preview.Refresh(Wifi.InterfaceGuid);
        Assert.Equal(adapter, preview.Detected!.Adapter);
        Assert.Equal("02:AA:BB:CC:DD:EE", preview.Detected.RouterMac);
    }

    [Fact]
    public void InterfaceChoiceRoundTripsThroughSettingsAndBrokerView()
    {
        var settings = new PaqetFireSettings { NetworkInterfaceGuid = Wifi.InterfaceGuid };
        var saved = JsonSerializer.Deserialize<PaqetFireSettings>(JsonSerializer.Serialize(settings))!;
        var view = PaqetFireSettingsView.FromSettings(saved);
        var received = JsonSerializer.Deserialize<PaqetFireSettingsView>(JsonSerializer.Serialize(view))!;
        Assert.Equal(Wifi.InterfaceGuid, received.NetworkInterfaceGuid);
        Assert.Null(JsonSerializer.Deserialize<PaqetFireSettings>("{}")!.NetworkInterfaceGuid);
    }
}
