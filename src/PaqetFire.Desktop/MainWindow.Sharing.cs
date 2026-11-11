using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Network;
using PaqetFire.Core.Sharing;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.Services;
using Windows.ApplicationModel.DataTransfer;

namespace PaqetFire.Desktop;

/// <summary>LAN and hotspot sharing: endpoints, credentials, the share URI and its QR code.</summary>
public sealed partial class MainWindow
{
    // The broker stores the sharing password but never returns it. Keep the value the
    // user entered or generated for this app session so the URI and QR stay available.
    private string sessionSharePassword = string.Empty;
    private int hotspotDetectionVersion;
    private int hotspotQrVersion;

    private void UpdateSharedCredentialsVisibility()
    {
        if (SharedCredentialsCard is null || ShareWithLanSwitch is null || ShareViaHotspotSwitch is null)
        {
            return;
        }

        SharedCredentialsCard.Visibility = (ShareWithLanSwitch.IsOn || ShareViaHotspotSwitch.IsOn)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShareWithLanSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (LanShareOptions is null)
        {
            return;
        }

        LanShareOptions.Visibility = ShareWithLanSwitch.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSharedCredentialsVisibility();
        UpdateLanShareEndpointText();
    }

    private async void ShareViaHotspotSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (isUpdatingHotspotInterlock || HotspotOptions is null)
        {
            return;
        }

        if (ShareViaHotspotSwitch.IsOn && BypassLanSwitch is not null && !BypassLanSwitch.IsOn)
        {
            if (Content?.XamlRoot is not null && !IsPopulatingForm)
            {
                var dialog = new ContentDialog
                {
                    Title = "Enable Direct Access for Local Network?",
                    Content = "Hotspot sharing requires direct access for local network devices so connected phones and tablets can reach this computer without traffic being blocked or routed through PaqetFire.\n\nPaqetFire will enable direct access for local network devices and turn on hotspot sharing.",
                    PrimaryButtonText = "Enable and continue",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };

                var result = await ShowDialogAsync(dialog);
                isUpdatingHotspotInterlock = true;
                if (result == ContentDialogResult.Primary)
                {
                    BypassLanSwitch.IsOn = true;
                    isUpdatingHotspotInterlock = false;
                }
                else
                {
                    ShareViaHotspotSwitch.IsOn = false;
                    isUpdatingHotspotInterlock = false;
                    return;
                }
            }
            else
            {
                isUpdatingHotspotInterlock = true;
                BypassLanSwitch.IsOn = true;
                isUpdatingHotspotInterlock = false;
            }
        }

        HotspotOptions.Visibility = ShareViaHotspotSwitch.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSharedCredentialsVisibility();

        if (ShareViaHotspotSwitch.IsOn)
        {
            await DetectHotspotAsync(notifyOnSuccess: false, notifyOnFailure: false);
        }
        else
        {
            UpdateHotspotEndpointText();
            UpdateHotspotUri();
        }
    }

    private async void BypassLanSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (isUpdatingHotspotInterlock || BypassLanSwitch is null || IsPopulatingForm)
        {
            return;
        }

        if (!BypassLanSwitch.IsOn && ShareViaHotspotSwitch is { IsOn: true })
        {
            var turnOffHotspot = true;
            if (Content?.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Disable Hotspot Sharing?",
                    Content = "Disabling direct access for local network devices will break Hotspot sharing connectivity.\n\nProceeding will turn off Hotspot sharing.",
                    PrimaryButtonText = "Turn off Hotspot",
                    CloseButtonText = "Keep direct access",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                turnOffHotspot = await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
            }

            isUpdatingHotspotInterlock = true;
            if (turnOffHotspot)
            {
                ShareViaHotspotSwitch.IsOn = false;
                isUpdatingHotspotInterlock = false;
                if (HotspotOptions is not null)
                {
                    HotspotOptions.Visibility = Visibility.Collapsed;
                }

                UpdateSharedCredentialsVisibility();
                UpdateHotspotEndpointText();
                UpdateHotspotUri();
            }
            else
            {
                BypassLanSwitch.IsOn = true;
                isUpdatingHotspotInterlock = false;
            }
        }
    }

    private void ShareUsernameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateHotspotUri();

    private void SharePasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateHotspotUri();

    private void LanSharePortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateLanShareEndpointText();
        UpdateHotspotUri();
    }

    private void HotspotPortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateHotspotEndpointText();
        UpdateHotspotUri();
    }

    private void DetectHotspotButton_Click(object sender, RoutedEventArgs e) =>
        _ = DetectHotspotAsync(notifyOnSuccess: true, notifyOnFailure: true);

    private void OpenV2rayNgGuideButton_Click(object sender, RoutedEventArgs e)
    {
        SyncGuideEndpoints();
        ShowPage("guide-v2rayng");
    }

    private void OpenHappGuideButton_Click(object sender, RoutedEventArgs e)
    {
        SyncGuideEndpoints();
        ShowPage("guide-happ");
    }

    private void OnGuideBackRequested(object? sender, EventArgs e) => NavigateTo("routing");

    private void OnGuideRotateRequested(object? sender, EventArgs e)
    {
        RotateSharingPassword(HotspotInfoBar);
        SyncGuideEndpoints();
    }

    private string CurrentSharePassword() =>
        SharePasswordBox is not null && !string.IsNullOrEmpty(SharePasswordBox.Password)
            ? SharePasswordBox.Password
            : sessionSharePassword;

    private void SyncGuideEndpoints()
    {
        var address = HotspotIpBox?.Text.Trim() ?? string.Empty;
        var port = HotspotPortBox is not null && !double.IsNaN(HotspotPortBox.Value) ? (int)HotspotPortBox.Value : 10808;
        var username = ShareUsernameBox is not null && !string.IsNullOrWhiteSpace(ShareUsernameBox.Text)
            ? ShareUsernameBox.Text.Trim()
            : "paqetfire";
        var password = CurrentSharePassword();
        var uri = HotspotUriBox?.Text.Trim() ?? string.Empty;

        V2rayNgGuideView?.UpdateEndpoint(address, port, username, password, uri);
        HappGuideView?.UpdateEndpoint(address, port, username, password, uri);
    }

    private void CopyHotspotUriButton_Click(object sender, RoutedEventArgs e)
    {
        var uri = HotspotUriBox.Text.Trim();
        if (string.IsNullOrEmpty(uri))
        {
            ShowInfo(HotspotInfoBar, InfoBarSeverity.Warning, "No URI to copy",
                string.IsNullOrWhiteSpace(HotspotIpBox.Text)
                    ? "Turn on Mobile hotspot in Windows Settings, then click Detect hotspot."
                    : "Enter the sharing password or generate a new one to create the URI.");
            return;
        }

        var package = new DataPackage();
        package.SetText(uri);
        Clipboard.SetContent(package);
        ShowInfo(HotspotInfoBar, InfoBarSeverity.Success, "SOCKS URI copied",
            "Paste it into v2rayNG or HAPP/Hiddify, or scan the QR code. It contains the sharing password; send it only to your own devices.");
        AddActivity("Hotspot SOCKS URI copied to the clipboard.");
    }

    private void RotateSharingPasswordButton_Click(object sender, RoutedEventArgs e) =>
        RotateSharingPassword(ReferenceEquals(sender, RotateForQrButton) ? HotspotInfoBar : SharedCredentialsInfoBar);

    private void RotateSharingPassword(InfoBar feedback)
    {
        try
        {
            var credentials = sharingCredentialGenerator.Rotate(
                string.IsNullOrWhiteSpace(ShareUsernameBox.Text) ? "paqetfire" : ShareUsernameBox.Text.Trim());
            ShareUsernameBox.Text = credentials.Username;
            SharePasswordBox.Password = credentials.Password;
            ShowInfo(feedback, InfoBarSeverity.Warning, "New sharing password generated",
                "Save the routing policy to apply it, then update every LAN and hotspot client. The QR code already shows the new password.");
            AddActivity("A new sharing password was generated; the value was not logged.");
            UpdateHotspotUri();
        }
        catch (ArgumentException exception)
        {
            ShowInfo(feedback, InfoBarSeverity.Error, "Password could not be generated", exception.Message);
        }
    }

    private async void TestShareListenerButton_Click(object sender, RoutedEventArgs e)
    {
        var feedback = ShareFeedbackFor(sender);
        if (!TryGetShareEndpoint((sender as FrameworkElement)?.Tag?.ToString(), out var endpoint, out var error))
        {
            ShowInfo(feedback, InfoBarSeverity.Warning, "Listener cannot be tested", error);
            return;
        }

        var result = await new TcpShareReachabilityProbe().CheckAsync(endpoint, TimeSpan.FromSeconds(3));
        ShowInfo(
            feedback,
            result.Status == ShareReachabilityStatus.Reachable ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            result.Status == ShareReachabilityStatus.Reachable ? "Listener reachable" : "Listener unavailable",
            result.Summary);
    }

    private void CopyShareSetupButton_Click(object sender, RoutedEventArgs e)
    {
        var feedback = ShareFeedbackFor(sender);
        if (!TryGetShareEndpoint((sender as FrameworkElement)?.Tag?.ToString(), out var endpoint, out var error))
        {
            ShowInfo(feedback, InfoBarSeverity.Warning, "Setup cannot be copied", error);
            return;
        }

        try
        {
            var username = string.IsNullOrWhiteSpace(ShareUsernameBox.Text) ? "paqetfire" : ShareUsernameBox.Text.Trim();
            var password = string.IsNullOrEmpty(SharePasswordBox.Password) ? "not-exported" : SharePasswordBox.Password;
            var bundle = RedactedShareSetupBundle.Create(
                endpoint,
                new SharedProxyCredentials(username, password),
                (sender as FrameworkElement)?.Tag?.ToString() == "hotspot" ? "PaqetFire hotspot" : "PaqetFire LAN");
            var package = new DataPackage();
            package.SetText(bundle.ToJson());
            Clipboard.SetContent(package);
            ShowInfo(feedback, InfoBarSeverity.Success, "Redacted setup copied",
                "The address, port, and username were copied. The sharing password was not included.");
        }
        catch (ArgumentException exception)
        {
            ShowInfo(feedback, InfoBarSeverity.Error, "Setup cannot be copied", exception.Message);
        }
    }

    /// <summary>The card a LAN or hotspot share button sits in reports its result.</summary>
    private InfoBar ShareFeedbackFor(object sender) =>
        string.Equals((sender as FrameworkElement)?.Tag?.ToString(), "hotspot", StringComparison.Ordinal)
            ? HotspotInfoBar
            : LanShareInfoBar;

    private bool TryGetShareEndpoint(string? kind, out SocksShareEndpoint endpoint, out string error)
    {
        endpoint = null!;
        var hotspot = string.Equals(kind, "hotspot", StringComparison.Ordinal);
        var address = hotspot ? HotspotIpBox.Text.Trim() : LocalIpv4Box.Text.Trim();
        var portValue = hotspot ? HotspotPortBox.Value : LanSharePortBox.Value;
        if (string.IsNullOrWhiteSpace(address) || double.IsNaN(portValue))
        {
            error = hotspot
                ? "Detect the active hotspot first."
                : "No active network adapter was found. Open Paqet connection › Network interface and click Detect adapter details.";
            return false;
        }

        try
        {
            endpoint = new SocksShareEndpoint(address, (int)portValue);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void UpdateLanShareEndpointText()
    {
        if (LanShareEndpointText is null || LanSharePortBox is null || LocalIpv4Box is null)
        {
            return;
        }

        var address = LocalIpv4Box.Text.Trim();
        var port = double.IsNaN(LanSharePortBox.Value) ? 1082 : (int)LanSharePortBox.Value;
        LanShareEndpointText.Text = string.IsNullOrEmpty(address)
            ? $"Configured SOCKS5 endpoint: this computer's LAN IPv4 address:{port}"
            : $"Configured SOCKS5 endpoint: {address}:{port}";
        UpdateLanShareStatus();
    }

    /// <summary>
    /// Says whether the LAN listener runs, and warns while it follows a network the user has
    /// not marked trusted in Settings.
    /// </summary>
    private void UpdateLanShareStatus()
    {
        if (ShareWithLanSwitch is null || LanExposureInfoBar is null || LanSharePortBox is null || LocalIpv4Box is null)
        {
            return;
        }

        var saved = ViewModel.LastSnapshot?.Settings;
        var port = double.IsNaN(LanSharePortBox.Value) ? 1082 : (int)LanSharePortBox.Value;
        ShareWithLanSwitch.OnContent = LanShareStatusText.OnContent(
            saved?.ShareWithLan == true,
            saved?.LanSocksPort,
            port,
            ViewModel.IsConnected,
            LocalIpv4Box.Text);

        var rule = currentNetworkContext is { } network
            ? preferences.NetworkRules.FirstOrDefault(candidate =>
                string.Equals(candidate.NetworkId, network.NetworkId, StringComparison.Ordinal))
            : null;
        var warning = LanShareStatusText.ExposureWarning(
            ShareWithLanSwitch.IsOn,
            rule?.IsTrusted,
            currentNetworkContext is null ? null : currentNetworkDisplayName);
        LanExposureInfoBar.Message = warning is null
            ? string.Empty
            : $"{warning} SOCKS5 does not encrypt this traffic. Share only on a network you trust.";
        LanExposureInfoBar.IsOpen = warning is not null;
    }

    private void UpdateHotspotEndpointText()
    {
        if (HotspotEndpointText is null || HotspotPortBox is null || HotspotIpBox is null)
        {
            return;
        }

        var address = HotspotIpBox.Text.Trim();
        var port = double.IsNaN(HotspotPortBox.Value) ? 10808 : (int)HotspotPortBox.Value;
        HotspotEndpointText.Text = string.IsNullOrEmpty(address)
            ? "Hotspot unavailable — turn on Mobile hotspot in Windows Settings, then click Detect hotspot."
            : $"SOCKS endpoint: {address}:{port}";
    }

    private void UpdateHotspotUri()
    {
        if (HotspotUriBox is null || HotspotIpBox is null || HotspotPortBox is null || ShareViaHotspotSwitch is null)
        {
            return;
        }

        var address = HotspotIpBox.Text.Trim();
        var port = double.IsNaN(HotspotPortBox.Value) ? 10808 : (int)HotspotPortBox.Value;
        var username = ShareUsernameBox is not null && !string.IsNullOrWhiteSpace(ShareUsernameBox.Text)
            ? ShareUsernameBox.Text.Trim()
            : "paqetfire";
        var password = CurrentSharePassword();
        var uri = string.Empty;
        if (ShareViaHotspotSwitch.IsOn && address.Length > 0 && password.Length > 0)
        {
            try
            {
                uri = SocksShareUri.CreateQrPayload(
                    new SocksShareEndpoint(address, port),
                    new SharedProxyCredentials(username, password));
            }
            catch (ArgumentException)
            {
                // An incomplete port or username; the field validation explains it on save.
            }
        }

        HotspotUriBox.Text = uri;
        UpdateHotspotQrPlaceholder(address, password);
        _ = RenderHotspotQrAsync(uri);
        SyncGuideEndpoints();
    }

    private void UpdateHotspotQrPlaceholder(string address, string password)
    {
        if (HotspotQrPlaceholder is null)
        {
            return;
        }

        if (address.Length == 0)
        {
            HotspotQrPlaceholderText.Text = "Detect the hotspot to show a QR code for phones.";
            RotateForQrButton.Visibility = Visibility.Collapsed;
        }
        else if (password.Length == 0)
        {
            HotspotQrPlaceholderText.Text = hasSavedLanSocksPassword
                ? "The saved sharing password is protected and cannot be shown. Rotate credentials to show a QR code, or re-enter the current password above."
                : "Set a sharing password to show a QR code.";
            RotateForQrButton.Visibility = Visibility.Visible;
        }
    }

    private async Task RenderHotspotQrAsync(string uri)
    {
        var version = ++hotspotQrVersion;
        var image = string.IsNullOrEmpty(uri) ? null : await QrCodeImage.CreateAsync(uri);
        if (version != hotspotQrVersion || HotspotQrImage is null)
        {
            return;
        }

        HotspotQrImage.Source = image;
        HotspotQrImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        HotspotQrPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DetectHotspotAsync(bool notifyOnSuccess, bool notifyOnFailure)
    {
        if (HotspotIpBox is null || HotspotEndpointText is null || HotspotUriBox is null || HotspotPortBox is null)
        {
            return;
        }

        var version = ++hotspotDetectionVersion;
        (string Address, string Name)? detected = await Task.Run(() =>
        {
            try
            {
                return new HotspotNetworkDetector().TryDetect();
            }
            catch (NetworkInformationException)
            {
                // Adapters may disappear while Windows changes hotspot state.
                return null;
            }
        });
        if (version != hotspotDetectionVersion)
        {
            return;
        }

        HotspotIpBox.Text = detected?.Address ?? string.Empty;
        UpdateHotspotEndpointText();
        UpdateHotspotUri();
        if (detected is { } hotspot && notifyOnSuccess)
        {
            ShowInfo(HotspotInfoBar, InfoBarSeverity.Success, "Hotspot detected",
                $"Detected active mobile hotspot at {hotspot.Address} ({hotspot.Name}).");
            AddActivity($"Detected active mobile hotspot at {hotspot.Address} ({hotspot.Name}).");
        }
        else if (detected is null && notifyOnFailure)
        {
            ShowInfo(HotspotInfoBar, InfoBarSeverity.Warning, "Hotspot not detected",
                "Turn on Mobile hotspot in Windows Settings first, then click Detect hotspot.");
        }
    }
}
