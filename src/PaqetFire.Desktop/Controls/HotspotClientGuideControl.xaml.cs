using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaqetFire.Desktop.Services;
using Windows.ApplicationModel.DataTransfer;

namespace PaqetFire.Desktop.Controls;

/// <summary>The phone or PC app a hotspot client guide walks through.</summary>
public enum HotspotClientApp
{
    V2rayNg,
    Happ,
}

/// <summary>
/// Step-by-step setup of a SOCKS client app on a device joined to the hotspot. The steps are
/// the same for every app; only the app's own wording differs.
/// </summary>
public sealed partial class HotspotClientGuideControl : UserControl
{
    private const string HiddenPassword = "••••••••••";

    private HotspotClientApp app;
    private string password = string.Empty;
    private int qrVersion;

    public event EventHandler? BackRequested;

    /// <summary>The saved sharing password cannot be shown; ask the window to generate a new one.</summary>
    public event EventHandler? RotateCredentialsRequested;

    public HotspotClientGuideControl()
    {
        InitializeComponent();
        ApplyAppText();
    }

    public HotspotClientApp App
    {
        get => app;
        set
        {
            app = value;
            ApplyAppText();
        }
    }

    /// <summary>Moves keyboard focus into the guide, so Esc and Alt+Left lead back at once.</summary>
    public void FocusBackButton() => BackButton.Focus(FocusState.Programmatic);

    public void UpdateEndpoint(string address, int port, string username, string password, string socksUri)
    {
        ServerAddressBox.Text = address;
        ServerPortBox.Text = port > 0 ? port.ToString() : "10808";
        UsernameBox.Text = username;
        this.password = password;
        ShowPassword();
        SocksUriBox.Text = socksUri;
        CopyPasswordButton.IsEnabled = ShowPasswordToggle.IsEnabled = !string.IsNullOrEmpty(password);
        CopyUriButton.IsEnabled = !string.IsNullOrEmpty(socksUri);
        QrPlaceholderText.Text = string.IsNullOrEmpty(address)
            ? "Detect the hotspot on the Routing page to show a QR code."
            : string.IsNullOrEmpty(password)
                ? "The saved sharing password is protected and cannot be shown. Rotate credentials to show a QR code, then save on the Routing page."
                : "Preparing the QR code…";
        RotateCredentialsButton.Visibility = !string.IsNullOrEmpty(address) && string.IsNullOrEmpty(password)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _ = RenderQrAsync(socksUri);
    }

    private void ApplyAppText()
    {
        if (TitleText is null) return;
        var happ = app == HotspotClientApp.Happ;
        V2rayNgIcon.Visibility = happ ? Visibility.Collapsed : Visibility.Visible;
        HappIcon.Visibility = happ ? Visibility.Visible : Visibility.Collapsed;
        EyebrowText.Text = happ ? "HAPP / HIDDIFY SETUP GUIDE" : "V2RAYNG SETUP GUIDE";
        PlatformText.Text = happ ? "iOS / Android / PC" : "Android";
        TitleText.Text = happ ? "Set up HAPP or Hiddify" : "Set up v2rayNG on Android";
        SubtitleText.Text = happ
            ? "Route an iPhone, iPad, Android phone, or another PC through PaqetFire over this laptop's mobile hotspot."
            : "Route an Android phone through PaqetFire over this laptop's mobile hotspot.";
        JoinHotspotText.Text = happ
            ? "Turn on Wi-Fi on the device and join this laptop's mobile hotspot network before you continue."
            : "Open Wi-Fi settings on the phone and join this laptop's mobile hotspot network before you continue.";
        InstallTitleText.Text = happ ? "Install HAPP or Hiddify" : "Install v2rayNG";
        InstallText.Text = happ
            ? "• iPhone and iPad: HAPP from the App Store, or Hiddify.\n• Android: Hiddify from Google Play or GitHub.\n• Windows, macOS, or Linux: the Hiddify desktop app."
            : "Install v2rayNG from Google Play or from its releases on GitHub. Android's own Wi-Fi proxy setting carries only HTTP, so a SOCKS app such as v2rayNG is needed.";
        ScanQrText.Text = happ
            ? "In HAPP or Hiddify, tap + (New profile), choose Scan QR code, and scan the code below. On a PC without a camera, use option B."
            : "In v2rayNG, tap + in the top-right corner, choose Scan QR code, and scan the code below.";
        ManualText.Text = happ
            ? "Add a profile by hand, choose the SOCKS5 (Socks) protocol, and enter these values:"
            : "In v2rayNG, tap + and choose Socks, then enter these values:";
        SettingsTitleText.Text = happ ? "Turn on Remote DNS and keep VPN mode" : "Turn on Remote DNS";
        RemoteDnsText.Text = happ
            ? "Turn on Remote DNS in the app's settings, and keep the service mode on VPN."
            : "In v2rayNG's settings, turn on Remote DNS.";
        RemoteDnsWhyText.Text = happ
            ? "Remote DNS sends domain lookups through PaqetFire's route instead of resolving them on the device, where answers can be poisoned. VPN mode, the default on phones, hands every app's traffic to the proxy; Proxy or System proxy mode reaches only apps that use the system proxy setting, which most phone apps ignore."
            : "Without it, the phone looks up names itself before sending requests, which can return poisoned answers and break regional routing. With Remote DNS, lookups go through PaqetFire's route.";
        ConnectText.Text = happ
            ? "Tap Connect. Allow the VPN configuration prompt when asked: the app uses it on the device to hand every app's traffic to the proxy."
            : "Select the profile you added, then tap the round V button at the bottom to connect. Accept Android's VPN prompt: v2rayNG uses it on the phone to hand every app's traffic to the proxy.";
    }

    private void ShowPassword()
    {
        var reveal = ShowPasswordToggle.IsChecked == true;
        PasswordValueBox.Text = string.IsNullOrEmpty(password) ? string.Empty : reveal ? password : HiddenPassword;
        ShowPasswordToggle.Content = reveal ? "Hide" : "Show";
        AutomationProperties.SetName(PasswordValueBox, reveal ? "Password" : "Password, hidden");
        AutomationProperties.SetName(ShowPasswordToggle, reveal ? "Hide password" : "Show password");
    }

    private void ShowPasswordToggle_Changed(object sender, RoutedEventArgs e) => ShowPassword();

    private async Task RenderQrAsync(string socksUri)
    {
        var version = ++qrVersion;
        var image = await QrCodeImage.CreateAsync(socksUri);
        if (version != qrVersion)
        {
            return;
        }

        QrImage.Source = image;
        QrImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        QrPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RotateCredentialsButton_Click(object sender, RoutedEventArgs e) =>
        RotateCredentialsRequested?.Invoke(this, EventArgs.Empty);

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void BackAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Accelerators are window-wide; act only while this guide is the page on screen.
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        args.Handled = true;
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CopySocksUriButton_Click(object sender, RoutedEventArgs e) => CopyToClipboard(SocksUriBox.Text, "SOCKS URI");

    private void CopyServerAddressButton_Click(object sender, RoutedEventArgs e) => CopyToClipboard(ServerAddressBox.Text, "Server address");

    private void CopyServerPortButton_Click(object sender, RoutedEventArgs e) => CopyToClipboard(ServerPortBox.Text, "Port");

    private void CopyUsernameButton_Click(object sender, RoutedEventArgs e) => CopyToClipboard(UsernameBox.Text, "Username");

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e) => CopyToClipboard(password, "Password");

    private void CopyToClipboard(string text, string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            ShowFeedback(InfoBarSeverity.Warning, "Nothing to copy", $"{label} is not set yet. Detect your hotspot first.");
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowFeedback(InfoBarSeverity.Success, "Copied", $"{label} copied to this PC's clipboard.");
    }

    private void ShowFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackInfoBar.Severity = severity;
        FeedbackInfoBar.Title = title;
        FeedbackInfoBar.Message = message;
        FeedbackInfoBar.IsOpen = true;
        // Scroll only as far as needed once the bar has its size.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            FeedbackInfoBar.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true }));
    }
}
