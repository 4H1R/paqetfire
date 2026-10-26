using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Desktop;

/// <summary>
/// Exit proxy card: the provider choice, each provider's own options, and getting the exit
/// engines, which are not part of the installer.
/// </summary>
public sealed partial class MainWindow
{
    private const string SavedSecretPlaceholder = "Saved securely · leave blank to keep it";

    // A newly imported .ovpn, sent with the next save; like the transport key it is never
    // read back from the broker, which only reports that one is saved.
    private string? pendingOpenVpnConfiguration;
    private string? pendingOpenVpnFileName;
    private OpenVpnExitView savedOpenVpn = new();
    private bool exitEngineRequestInFlight;

    private bool HasPendingOpenVpnSecrets =>
        pendingOpenVpnConfiguration is not null || OpenVpnPasswordBox.Password.Length > 0;

    private void InitializeExitProxyOptions()
    {
        PsiphonRegionBox.Items.Clear();
        PsiphonRegionBox.Items.Add(new ComboBoxItem
        {
            Content = "Best available — the fastest server in any country",
            Tag = string.Empty,
        });
        foreach (var region in PsiphonExitOptions.KnownRegions)
        {
            AddPsiphonRegion(region);
        }

        PsiphonRegionBox.SelectedIndex = 0;
        ApplyOpenVpnConfigurationText();
    }

    private ExitProxyKind SelectedExitProxy() => ((ExitProxyBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "psiphon" => ExitProxyKind.Psiphon,
        "openvpn" => ExitProxyKind.OpenVpn,
        _ => ExitProxyKind.None,
    };

    private void SelectExitProxy(ExitProxyKind kind)
    {
        ExitProxyBox.SelectedIndex = kind switch
        {
            ExitProxyKind.Psiphon => 1,
            ExitProxyKind.OpenVpn => 2,
            _ => 0,
        };
        UpdateExitProxyOptions();
    }

    private string SelectedPsiphonRegion() =>
        (PsiphonRegionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private void SelectPsiphonRegion(string? region)
    {
        region = (region ?? string.Empty).Trim().ToUpperInvariant();
        PsiphonRegionBox.SelectedItem = FindPsiphonRegion(region) ??
                                        (PsiphonExitOptions.IsValidRegion(region) ? AddPsiphonRegion(region) : null) ??
                                        PsiphonRegionBox.Items[0];
    }

    private void ExitProxyBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateExitProxyOptions();

    private void UpdateExitProxyOptions()
    {
        if (PsiphonOptionsPanel is null || OpenVpnOptionsPanel is null) return;
        var selected = SelectedExitProxy();
        PsiphonOptionsPanel.Visibility = selected == ExitProxyKind.Psiphon ? Visibility.Visible : Visibility.Collapsed;
        OpenVpnOptionsPanel.Visibility = selected == ExitProxyKind.OpenVpn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows what the running exit reports, and whether each exit engine is on this computer.</summary>
    private void UpdateExitProxyStatus(BrokerSnapshot snapshot)
    {
        UpdateExitEngines(snapshot.ExitEngines);
        UpdatePsiphonStatus(snapshot);
        if (snapshot.ExitProxy is { Kind: ExitProxyKind.OpenVpn } openVpn)
        {
            OpenVpnStatusText.Text = openVpn.TunnelActive
                ? $"Connected through {openVpn.Server ?? "your VPN provider"}."
                : "OpenVPN lost its tunnel and is reconnecting through Paqet.";
            OpenVpnStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            OpenVpnStatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateExitEngines(IReadOnlyList<ExitEngineAvailability> engines)
    {
        if (engines.FirstOrDefault(engine => engine.Kind == ExitProxyKind.Psiphon) is { } psiphon)
        {
            PsiphonEngineText.Text = psiphon switch
            {
                { IsDownloading: true } => "Downloading Psiphon and checking it against the digest PaqetFire pins…",
                { IsInstalled: true, Detail: { } problem } => $"Psiphon {psiphon.Version} is ready. {problem}",
                { IsInstalled: true } => $"Psiphon {psiphon.Version} is downloaded and verified.",
                _ => (psiphon.Detail ?? "Psiphon is not downloaded yet.") +
                     " It is not part of the installer; PaqetFire downloads it through Paqet while connected, or directly otherwise.",
            };
            PsiphonDownloadProgress.Visibility = psiphon.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
            PsiphonDownloadButton.Visibility = psiphon.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
            PsiphonDownloadButton.IsEnabled = !psiphon.IsDownloading && !exitEngineRequestInFlight;
            PsiphonImportButton.Visibility = psiphon.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
            PsiphonImportButton.IsEnabled = !psiphon.IsDownloading && !exitEngineRequestInFlight;
            PsiphonRemoveButton.Visibility = psiphon.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
            PsiphonRemoveButton.IsEnabled = !exitEngineRequestInFlight;
            PsiphonFileLink.Visibility = psiphon.IsInstalled || psiphon.DownloadUrl is null ? Visibility.Collapsed : Visibility.Visible;
            if (psiphon.DownloadUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                PsiphonFileLink.NavigateUri = uri;
            }
        }

        if (engines.FirstOrDefault(engine => engine.Kind == ExitProxyKind.OpenVpn) is { } openVpn)
        {
            OpenVpnEngineText.Text = openVpn.IsInstalled
                ? $"Using your OpenVPN {openVpn.Version} installation."
                : openVpn.Detail ?? "OpenVPN is not installed.";
            OpenVpnDownloadLink.Visibility = openVpn.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdatePsiphonStatus(BrokerSnapshot snapshot)
    {
        if (snapshot.ExitProxy is not { Kind: ExitProxyKind.Psiphon } status)
        {
            PsiphonStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var region in status.AvailableRegions)
        {
            if (FindPsiphonRegion(region) is null) AddPsiphonRegion(region);
        }

        var saved = snapshot.Settings?.Psiphon?.EgressRegion ?? string.Empty;
        PsiphonStatusText.Text = status switch
        {
            { TunnelActive: false } => "Psiphon lost its tunnel and is reconnecting through Paqet.",
            { ConnectedRegion: { Length: > 0 } region } =>
                $"Connected through a Psiphon server in {RegionName(region)}." +
                (status.AvailableRegions.Count > 0 ? $" {status.AvailableRegions.Count} countries are available right now." : string.Empty),
            _ => "Psiphon is connected.",
        };
        if (saved.Length > 0 && status.AvailableRegions.Count > 0 && !status.AvailableRegions.Contains(saved))
        {
            PsiphonStatusText.Text += $" {RegionName(saved)} is not offered at the moment.";
        }

        PsiphonStatusText.Visibility = Visibility.Visible;
    }

    private async void PsiphonDownloadButton_Click(object sender, RoutedEventArgs e) =>
        await SendExitEngineActionAsync(
            new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Download),
            "Psiphon download could not start",
            "Psiphon download requested.");

    private async void PsiphonImportButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "Psiphon console client (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        await SendExitEngineActionAsync(
            new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Import, dialog.FileName),
            "This file cannot be used",
            "Psiphon installed from a downloaded file.");
    }

    private async void PsiphonRemoveButton_Click(object sender, RoutedEventArgs e) =>
        await SendExitEngineActionAsync(
            new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Remove),
            "Psiphon could not be removed",
            "Psiphon removed.");

    private async Task SendExitEngineActionAsync(ExitEngineAction action, string failureTitle, string activity)
    {
        RoutingInfoBar.IsOpen = false;
        exitEngineRequestInFlight = true;
        UpdateExitEngines(ViewModel.LastSnapshot?.ExitEngines ?? []);
        try
        {
            if (await ViewModel.ManageExitEngineAsync(action) is { } error)
            {
                ShowInfo(RoutingInfoBar, InfoBarSeverity.Error, failureTitle, error);
                return;
            }

            AddActivity(activity);
        }
        finally
        {
            exitEngineRequestInFlight = false;
            UpdateExitEngines(ViewModel.LastSnapshot?.ExitEngines ?? []);
        }
    }

    private async void OpenVpnImportButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "OpenVPN configuration (*.ovpn;*.conf)|*.ovpn;*.conf|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string text;
        try
        {
            // UTF-8 can take up to four bytes per character.
            if (new FileInfo(dialog.FileName).Length > OpenVpnConfiguration.MaximumLength * 4L)
            {
                ShowInfo(RoutingInfoBar, InfoBarSeverity.Error, "This OpenVPN configuration cannot be used",
                    "The file is far larger than an OpenVPN configuration.");
                return;
            }

            text = await File.ReadAllTextAsync(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Error, "The file could not be read", exception.Message);
            return;
        }

        var configuration = OpenVpnConfiguration.Parse(text);
        if (!configuration.IsValid)
        {
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Error, "This OpenVPN configuration cannot be used",
                string.Join(" ", configuration.Errors));
            return;
        }

        RoutingInfoBar.IsOpen = false;
        pendingOpenVpnConfiguration = text;
        pendingOpenVpnFileName = Path.GetFileName(dialog.FileName);
        ApplyOpenVpnConfigurationText();
        AddActivity($"OpenVPN configuration {pendingOpenVpnFileName} imported; save to apply it.");
        ConfigurationEdited();
    }

    /// <summary>
    /// The desktop never receives the saved .ovpn, so it cannot check what it requires. When no
    /// new file was imported, configuration and account errors are left to the broker, which
    /// has the saved file. The saved password counts only for the same file and account.
    /// </summary>
    private bool IsSatisfiedBySavedOpenVpn(string message, OpenVpnExitOptions? submitted)
    {
        var configurationKept = string.IsNullOrEmpty(submitted?.Configuration) && savedOpenVpn.HasConfiguration;
        var passwordKept = configurationKept && string.IsNullOrEmpty(submitted?.Password) && savedOpenVpn.HasPassword &&
                           string.Equals(submitted?.Username, savedOpenVpn.Username, StringComparison.Ordinal);
        return configurationKept && (message.StartsWith("Import an OpenVPN configuration", StringComparison.Ordinal) ||
                                     message.StartsWith("OpenVPN configuration:", StringComparison.Ordinal) ||
                                     message.Contains("OpenVPN username", StringComparison.Ordinal) ||
                                     message.Contains("OpenVPN password", StringComparison.Ordinal)) ||
               passwordKept && message.Contains("OpenVPN password", StringComparison.Ordinal);
    }

    /// <summary>Applies what the broker reports about the saved OpenVPN exit of a profile just loaded.</summary>
    private void ApplySavedOpenVpn(OpenVpnExitView saved)
    {
        savedOpenVpn = saved;
        pendingOpenVpnConfiguration = null;
        pendingOpenVpnFileName = null;
        OpenVpnPasswordBox.Password = string.Empty;
        ApplyOpenVpnConfigurationText();
    }

    private void MarkOpenVpnSecretsSaved(OpenVpnExitOptions? submitted)
    {
        var configuration = OpenVpnConfiguration.Parse(pendingOpenVpnConfiguration);
        savedOpenVpn = savedOpenVpn with
        {
            Username = submitted?.Username ?? savedOpenVpn.Username,
            HasConfiguration = savedOpenVpn.HasConfiguration || pendingOpenVpnConfiguration is not null,
            HasPassword = savedOpenVpn.HasPassword || !string.IsNullOrEmpty(submitted?.Password),
            Server = pendingOpenVpnConfiguration is null ? savedOpenVpn.Server : configuration.Remote?.ToString(),
        };
        pendingOpenVpnConfiguration = null;
        pendingOpenVpnFileName = null;
        OpenVpnPasswordBox.Password = string.Empty;
        ApplyOpenVpnConfigurationText();
    }

    private void ApplyOpenVpnConfigurationText()
    {
        if (OpenVpnConfigurationText is null) return;
        if (pendingOpenVpnConfiguration is not null)
        {
            var configuration = OpenVpnConfiguration.Parse(pendingOpenVpnConfiguration);
            OpenVpnConfigurationText.Text =
                $"{pendingOpenVpnFileName}: {configuration.Remote} over TCP · save to apply it";
            OpenVpnIgnoredText.Text = configuration.IgnoredOptions.Count == 0
                ? string.Empty
                : $"Removed from the file: {string.Join(", ", configuration.IgnoredOptions)}.";
            OpenVpnIgnoredText.Visibility = configuration.IgnoredOptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            OpenVpnConfigurationText.Text = savedOpenVpn.HasConfiguration
                ? $"Saved configuration: {savedOpenVpn.Server ?? "imported"}"
                : "No configuration imported yet.";
            OpenVpnIgnoredText.Visibility = Visibility.Collapsed;
        }

        OpenVpnPasswordBox.PlaceholderText = savedOpenVpn.HasPassword && pendingOpenVpnConfiguration is null
            ? SavedSecretPlaceholder
            : "The OpenVPN password from your VPN account";
    }

    private ComboBoxItem? FindPsiphonRegion(string region) =>
        PsiphonRegionBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, region, StringComparison.Ordinal));

    private ComboBoxItem AddPsiphonRegion(string region)
    {
        var item = new ComboBoxItem { Content = $"{RegionName(region)} ({region})", Tag = region };
        // Keep the countries sorted by name after the "best available" entry.
        var index = 1;
        while (index < PsiphonRegionBox.Items.Count &&
               PsiphonRegionBox.Items[index] is ComboBoxItem existing &&
               string.Compare(existing.Content as string, item.Content as string, StringComparison.CurrentCulture) < 0)
        {
            index++;
        }

        PsiphonRegionBox.Items.Insert(index, item);
        return item;
    }

    private static string RegionName(string region)
    {
        try
        {
            return new RegionInfo(region).EnglishName;
        }
        catch (ArgumentException)
        {
            return region;
        }
    }
}
