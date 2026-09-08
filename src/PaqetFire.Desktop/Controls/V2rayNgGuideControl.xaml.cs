using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace PaqetFire.Desktop.Controls;

public sealed partial class V2rayNgGuideControl : UserControl
{
    public event EventHandler? BackRequested;

    public V2rayNgGuideControl()
    {
        InitializeComponent();
    }

    public void UpdateEndpoint(string address, int port, string username, string password, string socksUri)
    {
        ServerAddressBox.Text = address;
        ServerPortBox.Text = port > 0 ? port.ToString() : "10808";
        UsernameBox.Text = username;
        PasswordBox.Text = password;
        SocksUriBox.Text = socksUri;
        PasswordBox.PlaceholderText = "Re-enter your proxy password on the Routing page";
        CopyPasswordButton.IsEnabled = !string.IsNullOrEmpty(password);
        CopyUriButton.IsEnabled = !string.IsNullOrEmpty(socksUri);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CopySocksUriButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(SocksUriBox.Text, "SOCKS URI");
    }

    private void CopyServerAddressButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(ServerAddressBox.Text, "Server address");
    }

    private void CopyServerPortButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(ServerPortBox.Text, "Port");
    }

    private void CopyUsernameButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(UsernameBox.Text, "Username");
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(PasswordBox.Text, "Password");
    }

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
        ShowFeedback(InfoBarSeverity.Success, "Copied", $"{label} copied to the clipboard.");
    }

    private void ShowFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackInfoBar.Severity = severity;
        FeedbackInfoBar.Title = title;
        FeedbackInfoBar.Message = message;
        FeedbackInfoBar.IsOpen = true;
    }
}
