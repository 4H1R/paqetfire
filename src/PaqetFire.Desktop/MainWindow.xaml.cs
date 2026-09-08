using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Applications;
using PaqetFire.Core.Connections.Automation;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Network;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.Routing;
using PaqetFire.Core.Sharing;
using PaqetFire.Desktop.Ipc;
using PaqetFire.Desktop.Services;
using PaqetFire.Desktop.Services.ApplicationDiscovery;
using PaqetFire.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace PaqetFire.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopPreferencesStore preferencesStore = new();
    private bool initialized;
    private bool brokerSettingsApplied;
    private bool hasSavedTransportKey;
    private bool hasSavedLanSocksPassword;
    private DesktopPreferences preferences = new();
    private readonly HashSet<string> brokerLogLines = new(StringComparer.Ordinal);
    private readonly Queue<string> brokerLogLineOrder = new();
    private readonly Dictionary<string, PrerequisiteStatus> prerequisites = new(StringComparer.OrdinalIgnoreCase);
    private readonly PrerequisiteInstallerService prerequisiteInstaller = new();
    private System.Windows.Forms.NotifyIcon? trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? trayStatusMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? trayConnectMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? trayDisconnectMenuItem;
    private bool exitRequested;
    private bool trayNoticeShown;
    private bool prerequisiteActionInProgress;
    private bool isUpdatingHotspotInterlock;
    private bool configurationSaveInProgress;
    private bool trackingConfigurationChanges;
    private bool updatingProfileSelection;
    private bool hasPendingConfigurationChanges;
    private readonly NetworkAutomationPolicy networkAutomationPolicy = new();
    private BrokerSnapshot? lastSnapshot;
    private NetworkContext? currentNetworkContext;
    private string unidentifiedNetworkNonce = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? networkAutomationCancellation;
    private bool networkAutomationInProgress;
    private NetworkAutomationTrigger? pendingNetworkAutomationTrigger;
    private readonly IApplicationDiscoveryService applicationDiscovery = new WindowsApplicationDiscoveryService();
    private readonly ISharedProxyCredentialGenerator sharingCredentialGenerator = new SharedProxyCredentialGenerator();
    private CancellationTokenSource? applicationDiscoveryCancellation;
    private bool updatingApplicationPicker;

    public ConnectionViewModel ViewModel { get; }

    public ObservableCollection<ActivityLogEntry> ActivityEntries { get; } = [];

    public ObservableCollection<VerificationDisplayItem> VerificationEntries { get; } = [];

    public ObservableCollection<ProfileDisplayItem> ProfileEntries { get; } = [];

    public ObservableCollection<ApplicationPickerItem> ApplicationEntries { get; } = [];

    public MainWindow()
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The UI dispatcher is unavailable.");
        ViewModel = new ConnectionViewModel(new NamedPipeBrokerClient(), new WinUiDispatcher(dispatcherQueue));
        ViewModel.ActivityOccurred += OnActivityOccurred;
        ViewModel.SnapshotReceived += OnSnapshotReceived;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        InitializeComponent();
        UpdateConnectionVisuals();

        Title = "PaqetFire";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1180, 780));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PaqetFire.ico");
        AppWindow.SetIcon(iconPath);
        InitializeTrayIcon(iconPath);

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // The default theme background remains usable when Mica is unavailable.
        }

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        V2rayNgGuideView.BackRequested += OnGuideBackRequested;
        HappGuideView.BackRequested += OnGuideBackRequested;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        AddActivity("PaqetFire opened.");
        preferences = await preferencesStore.LoadAsync();
        ApplyPreferences(preferences);
        await ViewModel.RefreshAsync();
        TrackConfigurationChanges();

        if (preferences.EnableNetworkAutomation)
        {
            ScheduleNetworkAutomation(NetworkAutomationTrigger.Startup);
        }
        else if (preferences.ConnectOnLaunch && ViewModel.CanConnect)
        {
            AddActivity("Automatic connection requested from settings.");
            await ViewModel.ConnectAsync();
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ConnectAsync();

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.DisconnectAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshAsync();

    private async void VerifyConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        VerificationInfoBar.IsOpen = false;
        VerificationSummaryText.Text = "Testing the active route… External checks can take several seconds.";
        var report = await ViewModel.VerifyConnectionAsync();
        if (report is null)
        {
            ShowInfo(
                VerificationInfoBar,
                InfoBarSeverity.Error,
                "Verification could not run",
                ViewModel.ErrorMessage ?? "The broker did not return a verification report.");
            return;
        }

        ApplyVerification(report);
        ShowInfo(
            VerificationInfoBar,
            report.FailedCount > 0 ? InfoBarSeverity.Error :
                report.Checks.Any(check => check.Status == VerificationCheckStatus.Warning)
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success,
            report.FailedCount > 0 ? "Route needs attention" : "Verification complete",
            report.Summary + ". Results describe PaqetFire's route; browser-only WebRTC behavior is outside this native check.");
    }

    private async void PrimaryConnectionActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ConnectionState == BrokerConnectionState.NotReady)
        {
            NavigateTo(ViewModel.StateLabel == "PROFILE REQUIRED" ? "connection" : "diagnostics");
            return;
        }

        if (ViewModel.ConnectionState == BrokerConnectionState.Faulted)
        {
            NavigateTo("diagnostics");
            return;
        }

        if (ViewModel.CanDisconnect)
        {
            await ViewModel.DisconnectAsync();
        }
        else if (ViewModel.CanConnect)
        {
            await ViewModel.ConnectAsync();
        }
    }

    private async void PrerequisiteActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (prerequisiteActionInProgress || sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        if (!prerequisites.TryGetValue(id, out var prerequisite) || prerequisite.IsInstalled)
        {
            return;
        }

        if (!PrerequisiteInstallerService.CanInstallAutomatically(id))
        {
            if (prerequisite.HelpUri is null)
            {
                ShowInfo(PrerequisitesInfoBar, InfoBarSeverity.Warning, "Download unavailable", "No official download page was reported for this component.");
                return;
            }

            try
            {
                PrerequisiteInstallerService.OpenOfficialPage(prerequisite.HelpUri);
                ShowInfo(
                    PrerequisitesInfoBar,
                    InfoBarSeverity.Informational,
                    $"Install {prerequisite.DisplayName}",
                    id == "npcap"
                        ? "The official Npcap page is open. Install Npcap with WinPcap API-compatible mode enabled, then return here and run checks."
                        : "The official Microsoft download page is open. Finish the installer, then return here and run checks.");
                AddActivity($"Opened the official {prerequisite.DisplayName} download page.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                ShowInfo(PrerequisitesInfoBar, InfoBarSeverity.Error, "Could not open the download page", exception.Message);
            }

            return;
        }

        prerequisiteActionInProgress = true;
        UpdatePrerequisiteCards();
        try
        {
            var progress = new Progress<string>(message =>
                ShowInfo(PrerequisitesInfoBar, InfoBarSeverity.Informational, "Installing required software", message));
            var result = await prerequisiteInstaller.InstallAsync(id, progress);
            ShowInfo(
                PrerequisitesInfoBar,
                result.RestartRequired ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                result.RestartRequired ? "Installation complete — restart required" : "Installation complete",
                result.RestartRequired
                    ? "Restart Windows before connecting with PaqetFire."
                    : "PaqetFire is checking the component again now.");
            AddActivity($"Installed {prerequisite.DisplayName}.");
            await ViewModel.RefreshAsync();
        }
        catch (OperationCanceledException exception)
        {
            ShowInfo(PrerequisitesInfoBar, InfoBarSeverity.Warning, "Installation cancelled", exception.Message);
            AddActivity($"Installation cancelled for {prerequisite.DisplayName}.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowInfo(PrerequisitesInfoBar, InfoBarSeverity.Error, "Installation failed", exception.Message);
            AddActivity($"Could not install {prerequisite.DisplayName}.", "ERROR");
        }
        finally
        {
            prerequisiteActionInProgress = false;
            UpdatePrerequisiteCards();
        }
    }

    private void NavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var destination = args.IsSettingsSelected
            ? "settings"
            : args.SelectedItemContainer?.Tag?.ToString() ?? "overview";
        ShowPage(destination);
    }

    private void NavigateLink_Click(object sender, RoutedEventArgs e)
    {
        var destination = (sender as FrameworkElement)?.Tag?.ToString() ?? "overview";
        NavigateTo(destination);
    }

    private void NavigateTo(string destination)
    {
        var item = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                destination,
                StringComparison.Ordinal));
        if (item is not null)
        {
            Navigation.SelectedItem = item;
        }
    }

    private void ShowPage(string destination)
    {
        OverviewPage.Visibility = destination == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPage.Visibility = destination == "connection" ? Visibility.Visible : Visibility.Collapsed;
        RoutingPage.Visibility = destination == "routing" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = destination == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = destination == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (V2rayNgGuideView is not null)
        {
            V2rayNgGuideView.Visibility = destination == "guide-v2rayng" ? Visibility.Visible : Visibility.Collapsed;
        }
        if (HappGuideView is not null)
        {
            HappGuideView.Visibility = destination == "guide-happ" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnGuideBackRequested(object? sender, EventArgs e) => ShowPage("routing");

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

    private void SyncGuideEndpoints()
    {
        var address = HotspotIpBox?.Text.Trim() ?? string.Empty;
        var port = HotspotPortBox is not null && !double.IsNaN(HotspotPortBox.Value) ? (int)HotspotPortBox.Value : 10808;
        var username = ShareUsernameBox is not null && !string.IsNullOrWhiteSpace(ShareUsernameBox.Text)
            ? ShareUsernameBox.Text.Trim()
            : "paqetfire";
        var password = SharePasswordBox is not null && !string.IsNullOrEmpty(SharePasswordBox.Password)
            ? SharePasswordBox.Password
            : string.Empty;
        var uri = HotspotUriBox?.Text.Trim() ?? string.Empty;

        V2rayNgGuideView?.UpdateEndpoint(address, port, username, password, uri);
        HappGuideView?.UpdateEndpoint(address, port, username, password, uri);
    }

    private void RoutingModeButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfigurationEdited();
        var selectedOnly = RoutingModeButtons.SelectedIndex == 1;
        SelectedAppsCard.Visibility = selectedOnly ? Visibility.Visible : Visibility.Collapsed;
        RoutingModeHelpText.Text = selectedOnly
            ? "Only the listed executables are proxied. Other applications connect directly."
            : "All application traffic enters the PaqetFire route. Paqet, Xray, ProxiFyre, and the broker are excluded to prevent a loop.";
    }

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
            if (Content?.XamlRoot is not null)
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

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    isUpdatingHotspotInterlock = true;
                    BypassLanSwitch.IsOn = true;
                    isUpdatingHotspotInterlock = false;
                }
                else
                {
                    isUpdatingHotspotInterlock = true;
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
            DetectHotspot(notifyOnSuccess: false, notifyOnFailure: false);
        }
        else
        {
            UpdateHotspotEndpointText();
            UpdateHotspotUri();
        }
    }

    private async void BypassLanSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (isUpdatingHotspotInterlock || BypassLanSwitch is null)
        {
            return;
        }

        if (!BypassLanSwitch.IsOn && ShareViaHotspotSwitch is { IsOn: true })
        {
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

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    isUpdatingHotspotInterlock = true;
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
                    isUpdatingHotspotInterlock = true;
                    BypassLanSwitch.IsOn = true;
                    isUpdatingHotspotInterlock = false;
                }
            }
            else
            {
                isUpdatingHotspotInterlock = true;
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
        }
    }

    private void ShareUsernameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHotspotUri();
    }

    private void SharePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateHotspotUri();
    }

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
        DetectHotspot(notifyOnSuccess: true, notifyOnFailure: true);

    private void TrackConfigurationChanges()
    {
        trackingConfigurationChanges = true;
        foreach (var control in ConfigurationControls())
        {
            switch (control)
            {
                case TextBox box when !box.IsReadOnly:
                    box.TextChanged += (_, _) => ConfigurationEdited(); break;
                case PasswordBox box:
                    box.PasswordChanged += (_, _) => ConfigurationEdited(); break;
                case NumberBox box:
                    box.ValueChanged += (_, _) => ConfigurationEdited(); break;
                case ComboBox box:
                    box.SelectionChanged += (_, _) => ConfigurationEdited(); break;
                case ListView list:
                    list.SelectionChanged += (_, _) => ConfigurationEdited(); break;
                case ToggleSwitch toggle:
                    toggle.Toggled += (_, _) => ConfigurationEdited(); break;
                case CheckBox check:
                    check.Checked += (_, _) => ConfigurationEdited();
                    check.Unchecked += (_, _) => ConfigurationEdited(); break;
            }
        }
    }

    private void ConfigurationEdited()
    {
        if (!trackingConfigurationChanges || configurationSaveInProgress) return;
        SetPendingChanges(true);
        ConnectionInfoBar.IsOpen = RoutingInfoBar.IsOpen = false;
        ClearFieldErrors();
    }

    private void SetPendingChanges(bool pending)
    {
        hasPendingConfigurationChanges = pending;
        ConnectionPendingText.Text = RoutingPendingText.Text = pending
            ? "Pending changes � save to apply the connection profile and routing policy."
            : "No pending changes.";
    }

    private void ApplicationPicker_Expanding(Expander sender, ExpanderExpandingEventArgs args) =>
        ScheduleApplicationDiscovery();

    private void RefreshApplicationsButton_Click(object sender, RoutedEventArgs e) =>
        ScheduleApplicationDiscovery(immediate: true);

    private void ApplicationSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ScheduleApplicationDiscovery();

    private void ScheduleApplicationDiscovery(bool immediate = false)
    {
        applicationDiscoveryCancellation?.Cancel();
        applicationDiscoveryCancellation?.Dispose();
        applicationDiscoveryCancellation = new CancellationTokenSource();
        var token = applicationDiscoveryCancellation.Token;
        _ = LoadApplicationsAsync(immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250), token);
    }

    private async Task LoadApplicationsAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            ApplicationDiscoveryProgress.IsActive = true;
            var applications = await applicationDiscovery.DiscoverAsync(ApplicationSearchBox.Text, cancellationToken);
            var selected = SplitLines(SelectedApplicationsBox.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            updatingApplicationPicker = true;
            ApplicationEntries.Clear();
            foreach (var application in applications)
            {
                ApplicationEntries.Add(new ApplicationPickerItem
                {
                    DisplayName = application.DisplayName,
                    ExecutablePath = application.ExecutablePath,
                    StateText = application.IsRunning ? "Running" : application.IsInstalled ? "Installed" : string.Empty,
                    IsSelected = selected.Contains(application.ExecutablePath) ||
                                 selected.Contains(Path.GetFileName(application.ExecutablePath)),
                });
            }

            foreach (var selectedPath in selected.Where(selectedEntry =>
                         !applications.Any(application =>
                             string.Equals(application.ExecutablePath, selectedEntry, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Path.GetFileName(application.ExecutablePath), selectedEntry, StringComparison.OrdinalIgnoreCase))))
            {
                var expandedPath = Environment.ExpandEnvironmentVariables(selectedPath);
                ApplicationEntries.Add(new ApplicationPickerItem
                {
                    DisplayName = Path.GetFileNameWithoutExtension(selectedPath),
                    ExecutablePath = selectedPath,
                    StateText = Path.IsPathFullyQualified(expandedPath) && !File.Exists(expandedPath)
                        ? "Missing or moved"
                        : "Not currently discovered",
                    IsSelected = true,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Search text changed before discovery completed.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Warning, "Applications could not be listed", exception.Message);
        }
        finally
        {
            updatingApplicationPicker = false;
            ApplicationDiscoveryProgress.IsActive = false;
        }
    }

    private void ApplicationPickerCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (updatingApplicationPicker || sender is not CheckBox { DataContext: ApplicationPickerItem item } checkBox)
        {
            return;
        }

        var entries = SplitLines(SelectedApplicationsBox.Text).ToList();
        entries.RemoveAll(value =>
            string.Equals(value, item.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, Path.GetFileName(item.ExecutablePath), StringComparison.OrdinalIgnoreCase));
        if (checkBox.IsChecked == true)
        {
            entries.Add(item.ExecutablePath);
        }
        SelectedApplicationsBox.Text = string.Join(Environment.NewLine, entries.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void BrowseApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var entries = SplitLines(SelectedApplicationsBox.Text).Append(dialog.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        SelectedApplicationsBox.Text = string.Join(Environment.NewLine, entries);
        ScheduleApplicationDiscovery(immediate: true);
    }

    private async void ProfilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingProfileSelection || ProfilePicker.SelectedItem is not ProfileDisplayItem selected || selected.IsActive)
        {
            return;
        }

        if (hasPendingConfigurationChanges && Content?.XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                Title = "Discard pending changes?",
                Content = "Switching profiles replaces the unsaved fields currently shown.",
                PrimaryButtonText = "Discard and switch",
                CloseButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                SelectActiveProfile();
                return;
            }
        }

        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Activate, selected.Id),
            reloadSettings: true,
            $"Switched to profile '{selected.Name}'.");
    }

    private async void DuplicateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not ProfileDisplayItem selected) return;
        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Duplicate, selected.Id),
            reloadSettings: true,
            $"Duplicated profile '{selected.Name}'.");
    }

    private async void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not ProfileDisplayItem selected || Content?.XamlRoot is null) return;
        var nameBox = new TextBox
        {
            Header = "Profile name",
            Text = selected.Name,
            MaxLength = 80,
        };
        var dialog = new ContentDialog
        {
            Title = "Rename profile",
            Content = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Rename, selected.Id, nameBox.Text),
            reloadSettings: selected.IsActive,
            $"Renamed profile to '{nameBox.Text.Trim()}'.");
    }

    private async void MakeDefaultProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not ProfileDisplayItem selected) return;
        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.MakeDefault, selected.Id),
            reloadSettings: false,
            $"'{selected.Name}' is now the default profile.");
    }

    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not ProfileDisplayItem selected || Content?.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = $"Delete '{selected.Name}'?",
            Content = "This removes the profile and its protected secrets. The last remaining profile cannot be deleted.",
            PrimaryButtonText = "Delete profile",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Delete, selected.Id),
            reloadSettings: selected.IsActive,
            $"Deleted profile '{selected.Name}'.");
    }

    private async void ExportProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var json = await ViewModel.ExportProfilesAsync();
        if (json is null)
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Error, "Profiles could not be exported",
                ViewModel.ErrorMessage ?? "The broker did not provide an export.");
            return;
        }

        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Filter = "PaqetFire profile export (*.json)|*.json",
            FileName = "paqetfire-profiles.json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        await File.WriteAllTextAsync(dialog.FileName, json);
        ShowInfo(ConnectionInfoBar, InfoBarSeverity.Success, "Profiles exported",
            "The export was saved without transport keys or sharing passwords.");
        AddActivity("Redacted profiles exported.");
    }

    private async void ImportProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "PaqetFire profile export (*.json)|*.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length > ProfileCatalogJson.MaximumDocumentBytes)
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Error, "Import is too large",
                $"Choose a PaqetFire profile export smaller than {ProfileCatalogJson.MaximumDocumentBytes / (1024 * 1024)} MB.");
            return;
        }

        if (Content?.XamlRoot is null) return;
        var confirmation = new ContentDialog
        {
            Title = "Replace all saved profiles?",
            Content = "Import replaces the entire saved profile catalog. The route must be disconnected, and imported profiles will need their transport keys and sharing passwords entered again.",
            PrimaryButtonText = "Replace profiles",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        var json = await File.ReadAllTextAsync(dialog.FileName);
        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.ImportRedacted, Payload: json),
            reloadSettings: true,
            "Imported profiles. Re-enter their transport keys and sharing passwords before connecting.");
    }

    private async Task RunProfileActionAsync(ProfileAction action, bool reloadSettings, string successMessage)
    {
        if (reloadSettings)
        {
            brokerSettingsApplied = false;
            SetPendingChanges(false);
        }

        if (await ViewModel.ManageProfileAsync(action))
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Success, "Profiles updated", successMessage);
            AddActivity(successMessage);
        }
        else
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Error, "Profiles could not be updated",
                ViewModel.ErrorMessage ?? "The broker rejected the profile action.");
            if (reloadSettings) brokerSettingsApplied = true;
            SelectActiveProfile();
        }
    }

    private void UpdateProfileEntries(ProfileCatalogView? catalog)
    {
        if (catalog is null) return;
        updatingProfileSelection = true;
        try
        {
            ProfileEntries.Clear();
            foreach (var profile in catalog.Profiles)
            {
                ProfileEntries.Add(new ProfileDisplayItem
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    DisplayName = profile.Name + (profile.IsDefault ? " · default" : string.Empty),
                    IsActive = profile.IsActive,
                    IsDefault = profile.IsDefault,
                });
            }
            var validIds = ProfileEntries.Select(profile => profile.Id).ToHashSet();
            foreach (var rule in preferences.NetworkRules.Where(rule =>
                         rule.ProfileId is { } profileId && !validIds.Contains(profileId)))
            {
                rule.ProfileId = null;
            }
            ProfilePicker.SelectedItem = ProfileEntries.FirstOrDefault(profile => profile.IsActive);
        }
        finally
        {
            updatingProfileSelection = false;
        }
    }

    private void SelectActiveProfile()
    {
        updatingProfileSelection = true;
        ProfilePicker.SelectedItem = ProfileEntries.FirstOrDefault(profile => profile.IsActive);
        updatingProfileSelection = false;
    }

    private IEnumerable<Control> ConfigurationControls() =>
        new Control[] { ProfileNameBox, ServerEndpointBox, TransportKeyBox, KcpModeBox,
            LocalFlagsBox, RemoteFlagsBox, RoutingModeButtons, SelectedApplicationsBox,
            UserExclusionsBox, BypassLanSwitch, RegionalPresetBox, DomainStrategyBox,
            DirectRouteDestinationsBox,
            BlockAdsSwitch, BlockQuicSwitch, DirectBitTorrentSwitch, KillSwitchToggle,
            ShareWithLanSwitch, LanSharePortBox,
            ShareViaHotspotSwitch, HotspotPortBox,
            ShareUsernameBox, SharePasswordBox,
            RouteTcpCheckBox, RouteUdpCheckBox, RouteIpv4CheckBox, RouteIpv6CheckBox };

    private void SetSaveActionsEnabled(bool enabled)
    {
        if (ConnectionSaveActions is null || RoutingSaveActions is null) return;
        foreach (var button in ConnectionSaveActions.Children.Concat(RoutingSaveActions.Children).OfType<Button>())
            button.IsEnabled = enabled;
    }

    private void SetConfigurationEditingEnabled(bool enabled)
    {
        foreach (var control in ConfigurationControls()) control.IsEnabled = enabled;
    }

    private void FormGrid_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid grid || grid.ColumnDefinitions.Count < 2) return;
        var scale = new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
        var narrow = args.NewSize.Width < grid.ColumnDefinitions.Count * 220 * scale;
        var children = grid.Children.OfType<FrameworkElement>().ToArray();
        if (grid.RowDefinitions.Count != children.Length)
        {
            grid.RowDefinitions.Clear();
            foreach (var child in children) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.RowSpacing = narrow ? 14 : 0;
        for (var index = 0; index < children.Length; index++)
        {
            Grid.SetRow(children[index], narrow ? index : 0);
            Grid.SetColumn(children[index], narrow ? 0 : index);
            Grid.SetColumnSpan(children[index], narrow ? grid.ColumnDefinitions.Count : 1);
        }
        if (ConnectionSaveActions is null || RoutingSaveActions is null) return;
        ConnectionSaveActions.Orientation = RoutingSaveActions.Orientation =
            (RoutingPage.Visibility == Visibility.Visible ? RoutingPage.ActualWidth : ConnectionPage.ActualWidth) < 520 * scale ? Orientation.Vertical : Orientation.Horizontal;
    }

    private readonly List<(Panel Parent, TextBlock Error)> fieldErrors = [];

    private void ClearFieldErrors()
    {
        foreach (var (parent, error) in fieldErrors) parent.Children.Remove(error);
        fieldErrors.Clear();
        foreach (var control in ConfigurationControls())
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(control, string.Empty);
    }

    private void ShowFieldErrors(IReadOnlyList<string> errors)
    {
        ClearFieldErrors();
        AdvancedTransportExpander.IsExpanded |= errors.Any(message => message.Contains("TCP flag") || message.Contains("KCP mode"));
        Control? first = null;
        foreach (var message in errors)
        {
            var control = message switch
            {
                var m when m.Contains("profile name") => (Control)ProfileNameBox,
                var m when m.Contains("server must") => ServerEndpointBox,
                var m when m.Contains("transport key") => TransportKeyBox,
                var m when m.Contains("KCP mode") => KcpModeBox,
                var m when m.Contains("local TCP") => LocalFlagsBox,
                var m when m.Contains("remote TCP") => RemoteFlagsBox,
                var m when m.Contains("hotspot") && m.Contains("port") => HotspotPortBox,
                var m when m.Contains("SOCKS5 port") => LanSharePortBox,
                var m when m.Contains("username") => ShareUsernameBox,
                var m when m.Contains("password") => SharePasswordBox,
                var m when m.Contains("direct-route destination") => DirectRouteDestinationsBox,
                var m when m.Contains("exclusion") => UserExclusionsBox,
                var m when m.Contains("application", StringComparison.OrdinalIgnoreCase) => SelectedApplicationsBox,
                var m when m.Contains("protocol") => RouteTcpCheckBox,
                var m when m.Contains("address family") => RouteIpv4CheckBox,
                var m when m.Contains("regional") => RegionalPresetBox,
                var m when m.Contains("domain strategy") => DomainStrategyBox,
                _ => (Control)RoutingModeButtons,
            };
            first ??= control;
            // Place the message immediately after the field's row without changing its header.
            DependencyObject row = control;
            while (VisualTreeHelper.GetParent(row) is DependencyObject parent && parent is not StackPanel)
                row = parent;
            if (VisualTreeHelper.GetParent(row) is StackPanel panel && row is UIElement rowElement)
            {
                var error = new TextBlock
                {
                    Text = $"Error: {message}",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
                panel.Children.Insert(panel.Children.IndexOf(rowElement) + 1, error);
                fieldErrors.Add((panel, error));
            }
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(control, message);
        }
        if (first is null) return;
        NavigateTo(new Control[] { ProfileNameBox, ServerEndpointBox, TransportKeyBox, KcpModeBox, LocalFlagsBox, RemoteFlagsBox }.Contains(first) ? "connection" : "routing");
        for (DependencyObject? ancestor = first; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is Expander expander) expander.IsExpanded = true;
        var invalidControl = first;
        DispatcherQueue.TryEnqueue(() =>
        {
            invalidControl.Focus(FocusState.Programmatic);
            invalidControl.StartBringIntoView();
        });
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e) =>
        await SaveConfigurationAsync(ConnectionInfoBar, false);

    private async void SaveAndConnectButton_Click(object sender, RoutedEventArgs e) =>
        await SaveConfigurationAsync(ConnectionInfoBar, true);

    private async void SaveRoutingButton_Click(object sender, RoutedEventArgs e) =>
        await SaveConfigurationAsync(RoutingInfoBar, false);

    private async void SaveRoutingAndConnectButton_Click(object sender, RoutedEventArgs e) =>
        await SaveConfigurationAsync(RoutingInfoBar, true);

    private async Task SaveConfigurationAsync(InfoBar feedback, bool connectAfterSave)
    {
        if (configurationSaveInProgress || ViewModel.IsBusy) return;
        configurationSaveInProgress = true;
        SetSaveActionsEnabled(false);
        ConnectionInfoBar.IsOpen = RoutingInfoBar.IsOpen = false;
        try
        {
            if (!TryCreateBrokerSettings(out var settings, out var errors))
            {
                ShowInfo(feedback, InfoBarSeverity.Error, "Configuration needs attention", errors);
                return;
            }
            SetConfigurationEditingEnabled(false);
            ShowInfo(feedback, InfoBarSeverity.Informational, "Applying configuration",
                "Saving both the connection profile and routing policy. An active connection will restart.");
            CapturePreferences();
            if (!await TrySavePreferencesAsync(feedback)) return;
            if (!await ViewModel.SaveSettingsAsync(settings, connectAfterSave))
            {
                ShowInfo(feedback, InfoBarSeverity.Error, "Configuration was not applied",
                    ViewModel.ErrorMessage ?? "The broker rejected the configuration. Try saving again.");
                return;
            }
            MarkSecretsSaved(settings);
            SetPendingChanges(false);
            ShowInfo(feedback, ViewModel.HasError ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                ViewModel.HasError ? "Saved � connection needs attention" : "Configuration applied",
                ViewModel.ErrorMessage ?? "The connection profile and routing policy were saved together.");
            AddActivity("Connection profile and routing policy saved.");
        }
        finally
        {
            configurationSaveInProgress = false;
            SetConfigurationEditingEnabled(true);
            SetSaveActionsEnabled(!ViewModel.IsBusy);
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CapturePreferences();
        try
        {
            ApplyStartupRegistration(preferences.StartWithWindows);
            if (trayIcon is not null)
            {
                trayIcon.Visible = preferences.MinimizeToTray;
            }
            await preferencesStore.SaveAsync(preferences);
            ShowInfo(SettingsInfoBar, InfoBarSeverity.Success, "Settings saved", "Your launch and background preferences have been updated.");
            AddActivity("Application settings saved.");
            ScheduleNetworkAutomation(NetworkAutomationTrigger.StatusPoll);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowInfo(SettingsInfoBar, InfoBarSeverity.Error, "Settings could not be saved", exception.Message);
            AddActivity("Application settings could not be saved.", "ERROR");
        }
    }

    private void DetectAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .Where(candidate => candidate.OperationalStatus == OperationalStatus.Up)
                .Select(candidate => new
                {
                    Adapter = candidate,
                    Properties = candidate.GetIPProperties(),
                })
                .FirstOrDefault(candidate =>
                    candidate.Adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
                    candidate.Properties.GatewayAddresses.Count > 0 &&
                    candidate.Properties.UnicastAddresses.Any(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork));

            if (adapter is null)
            {
                ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "No active adapter found", "Connect Ethernet or Wi-Fi, then try again.");
                return;
            }

            InterfaceNameBox.Text = adapter.Adapter.Name;
            InterfaceGuidBox.Text = adapter.Adapter.Id.Trim('{', '}');
            LocalIpv4Box.Text = adapter.Properties.UnicastAddresses
                .First(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Address.ToString();
            UpdateLanShareEndpointText();

            var gateway = adapter.Properties.GatewayAddresses
                .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            RouterMacBox.Text = TryResolveMacAddress(gateway) ?? RouterMacBox.Text;

            var message = string.IsNullOrWhiteSpace(RouterMacBox.Text)
                ? "Adapter details were detected. Save the profile so the broker can resolve the router MAC. If saving fails, check that Ethernet or Wi-Fi has a working default gateway, then retry."
                : "Adapter and router details were detected.";
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Success, "Network detected", message);
            AddActivity($"Detected active adapter '{adapter.Adapter.Name}'.");
            if (ShareViaHotspotSwitch?.IsOn == true)
            {
                DetectHotspot(notifyOnSuccess: false, notifyOnFailure: false);
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException or InvalidOperationException)
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "Detection was incomplete", exception.Message);
        }
    }

    private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, ActivityEntries.Select(entry =>
            $"{entry.Time} [{entry.Level}] {entry.Message}"));
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        AddActivity("Activity log copied to the clipboard.");
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        ActivityEntries.Clear();
        AddActivity("Activity log cleared.");
    }

    private void CopyHotspotUriButton_Click(object sender, RoutedEventArgs e)
    {
        var uri = HotspotUriBox.Text.Trim();
        if (string.IsNullOrEmpty(uri))
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "No URI to copy", "Hotspot is not active or IP not detected.");
            return;
        }

        var package = new DataPackage();
        package.SetText(uri);
        Clipboard.SetContent(package);
        AddActivity("Hotspot SOCKS URI copied to the clipboard.");
    }

    private void RotateSharingPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var credentials = sharingCredentialGenerator.Rotate(
                string.IsNullOrWhiteSpace(ShareUsernameBox.Text) ? "paqetfire" : ShareUsernameBox.Text.Trim());
            ShareUsernameBox.Text = credentials.Username;
            SharePasswordBox.Password = credentials.Password;
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Warning, "New sharing password generated",
                "Save the routing policy to apply it, then update every LAN and hotspot client.");
            AddActivity("A new sharing password was generated; the value was not logged.");
        }
        catch (ArgumentException exception)
        {
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Error, "Password could not be generated", exception.Message);
        }
    }

    private async void TestShareListenerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetShareEndpoint((sender as FrameworkElement)?.Tag?.ToString(), out var endpoint, out var error))
        {
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Warning, "Listener cannot be tested", error);
            return;
        }

        var result = await new TcpShareReachabilityProbe().CheckAsync(endpoint, TimeSpan.FromSeconds(3));
        ShowInfo(
            RoutingInfoBar,
            result.Status == ShareReachabilityStatus.Reachable ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            result.Status == ShareReachabilityStatus.Reachable ? "Listener reachable" : "Listener unavailable",
            result.Summary);
    }

    private void CopyShareSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetShareEndpoint((sender as FrameworkElement)?.Tag?.ToString(), out var endpoint, out var error))
        {
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Warning, "Setup cannot be copied", error);
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
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Success, "Redacted setup copied",
                "The address, port, and username were copied. The sharing password was not included.");
        }
        catch (ArgumentException exception)
        {
            ShowInfo(RoutingInfoBar, InfoBarSeverity.Error, "Setup cannot be copied", exception.Message);
        }
    }

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
                : "Detect the active network adapter first.";
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

    private bool TryCreateBrokerSettings(out PaqetFireSettings settings, out string error)
    {
        settings = new PaqetFireSettings
        {
            ProfileName = ProfileNameBox.Text.Trim(),
            ServerEndpoint = ServerEndpointBox.Text.Trim(),
            TransportKey = TransportKeyBox.Password,
            RoutingMode = RoutingModeButtons.SelectedIndex == 1
                ? RoutingMode.SelectedApplications
                : RoutingMode.AllApplications,
            SelectedApplications = SplitLines(SelectedApplicationsBox.Text),
            UserExclusions = SplitLines(UserExclusionsBox.Text),
            DirectRouteDestinations = SplitLines(DirectRouteDestinationsBox.Text),
            BypassLan = BypassLanSwitch.IsOn,
            RouteTcp = RouteTcpCheckBox.IsChecked == true,
            RouteUdp = RouteUdpCheckBox.IsChecked == true,
            RouteIpv4 = RouteIpv4CheckBox.IsChecked == true,
            RouteIpv6 = RouteIpv6CheckBox.IsChecked == true,
            KillSwitchEnabled = KillSwitchToggle.IsOn,
            ShareWithLan = ShareWithLanSwitch.IsOn,
            LanSocksPort = double.IsNaN(LanSharePortBox.Value) ? 0 : (int)LanSharePortBox.Value,
            LanSocksUsername = ShareUsernameBox.Text.Trim(),
            LanSocksPassword = SharePasswordBox.Password,
            ShareViaHotspot = ShareViaHotspotSwitch.IsOn,
            HotspotSocksPort = double.IsNaN(HotspotPortBox.Value) ? 10808 : (int)HotspotPortBox.Value,
            RegionalPreset = RegionalPresetBox.SelectedIndex == 1
                ? RegionalRoutingPreset.None
                : RegionalRoutingPreset.IranDirect,
            DomainStrategy = DomainStrategyBox.SelectedIndex switch
            {
                0 => XrayDomainStrategy.AsIs,
                2 => XrayDomainStrategy.IPOnDemand,
                _ => XrayDomainStrategy.IPIfNonMatch,
            },
            BlockAds = BlockAdsSwitch.IsOn,
            BlockQuic = BlockQuicSwitch.IsOn,
            DirectBitTorrent = DirectBitTorrentSwitch.IsOn,
            KcpMode = (KcpModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "fast",
            LocalTcpFlags = SplitEntries(LocalFlagsBox.Text),
            RemoteTcpFlags = SplitEntries(RemoteFlagsBox.Text),
        };

        var validationCandidate = settings;
        if (hasSavedTransportKey && validationCandidate.TransportKey.Length == 0)
        {
            validationCandidate = validationCandidate with { TransportKey = "saved-by-broker" };
        }

        if (hasSavedLanSocksPassword && validationCandidate.LanSocksPassword.Length == 0)
        {
            validationCandidate = validationCandidate with { LanSocksPassword = "saved-by-broker" };
        }
        var errors = PaqetFireSettingsValidator.Validate(validationCandidate);
        ShowFieldErrors(errors);
        error = string.Join(" ", errors);
        return errors.Count == 0;
    }

    private void ApplyPreferences(DesktopPreferences source)
    {
        ProfileNameBox.Text = source.ProfileName;
        ServerEndpointBox.Text = source.ServerEndpoint;
        SocksEndpointBox.Text = source.LocalSocksEndpoint;
        InterfaceNameBox.Text = source.InterfaceName;
        InterfaceGuidBox.Text = source.InterfaceGuid;
        LocalIpv4Box.Text = source.LocalIpv4Address;
        RouterMacBox.Text = source.RouterMac;
        LocalFlagsBox.Text = source.LocalTcpFlags;
        RemoteFlagsBox.Text = source.RemoteTcpFlags;
        KcpModeBox.SelectedIndex = source.KcpMode.ToLowerInvariant() switch
        {
            "normal" => 0,
            "fast2" => 2,
            "fast3" => 3,
            _ => 1,
        };

        RoutingModeButtons.SelectedIndex = source.RouteAllApplications ? 0 : 1;
        SelectedApplicationsBox.Text = source.SelectedApplications;
        UserExclusionsBox.Text = source.UserExclusions;
        DirectRouteDestinationsBox.Text = source.DirectRouteDestinations;
        isUpdatingHotspotInterlock = true;
        try
        {
            BypassLanSwitch.IsOn = source.BypassLan;
            ShareViaHotspotSwitch.IsOn = source.ShareViaHotspot;
        }
        finally
        {
            isUpdatingHotspotInterlock = false;
        }
        RegionalPresetBox.SelectedIndex = source.RegionalPreset == RegionalRoutingPreset.None ? 1 : 0;
        DomainStrategyBox.SelectedIndex = source.DomainStrategy switch
        {
            XrayDomainStrategy.AsIs => 0,
            XrayDomainStrategy.IPOnDemand => 2,
            _ => 1,
        };
        BlockAdsSwitch.IsOn = source.BlockAds;
        BlockQuicSwitch.IsOn = source.BlockQuic;
        DirectBitTorrentSwitch.IsOn = source.DirectBitTorrent;
        KillSwitchToggle.IsOn = source.KillSwitch;
        ShareWithLanSwitch.IsOn = source.ShareWithLan;
        HotspotPortBox.Value = source.HotspotSocksPort;
        HotspotOptions.Visibility = source.ShareViaHotspot ? Visibility.Visible : Visibility.Collapsed;
        LanSharePortBox.Value = source.LanSocksPort;
        ShareUsernameBox.Text = source.LanSocksUsername;
        UpdateSharedCredentialsVisibility();
        UpdateLanShareEndpointText();
        if (source.ShareViaHotspot)
        {
            DetectHotspot(notifyOnSuccess: false, notifyOnFailure: false);
        }
        else
        {
            UpdateHotspotEndpointText();
            UpdateHotspotUri();
        }
        RouteTcpCheckBox.IsChecked = source.RouteTcp;
        RouteUdpCheckBox.IsChecked = source.RouteUdp;
        RouteIpv4CheckBox.IsChecked = source.RouteIpv4;
        RouteIpv6CheckBox.IsChecked = source.RouteIpv6;

        StartWithWindowsSwitch.IsOn = source.StartWithWindows;
        ConnectOnLaunchSwitch.IsOn = source.ConnectOnLaunch;
        CloseToTraySwitch.IsOn = source.MinimizeToTray;
        NetworkAutomationSwitch.IsOn = source.EnableNetworkAutomation;
        ConnectOnTrustedNetworksSwitch.IsOn = source.ConnectOnTrustedNetworks;
        UpdateCurrentNetworkText();
        if (trayIcon is not null)
        {
            trayIcon.Visible = source.MinimizeToTray;
        }
    }

    private void CapturePreferences()
    {
        preferences.ProfileName = ProfileNameBox.Text.Trim();
        preferences.ServerEndpoint = ServerEndpointBox.Text.Trim();
        preferences.LocalSocksEndpoint = SocksEndpointBox.Text.Trim();
        preferences.InterfaceName = InterfaceNameBox.Text.Trim();
        preferences.InterfaceGuid = InterfaceGuidBox.Text.Trim();
        preferences.LocalIpv4Address = LocalIpv4Box.Text.Trim();
        preferences.RouterMac = RouterMacBox.Text.Trim();
        preferences.LocalTcpFlags = LocalFlagsBox.Text.Trim();
        preferences.RemoteTcpFlags = RemoteFlagsBox.Text.Trim();
        preferences.KcpMode = (KcpModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "fast";

        preferences.RouteAllApplications = RoutingModeButtons.SelectedIndex != 1;
        preferences.SelectedApplications = SelectedApplicationsBox.Text.Trim();
        preferences.UserExclusions = UserExclusionsBox.Text.Trim();
        preferences.DirectRouteDestinations = DirectRouteDestinationsBox.Text.Trim();
        preferences.BypassLan = BypassLanSwitch.IsOn;
        preferences.RegionalPreset = RegionalPresetBox.SelectedIndex == 1
            ? RegionalRoutingPreset.None
            : RegionalRoutingPreset.IranDirect;
        preferences.DomainStrategy = DomainStrategyBox.SelectedIndex switch
        {
            0 => XrayDomainStrategy.AsIs,
            2 => XrayDomainStrategy.IPOnDemand,
            _ => XrayDomainStrategy.IPIfNonMatch,
        };
        preferences.BlockAds = BlockAdsSwitch.IsOn;
        preferences.BlockQuic = BlockQuicSwitch.IsOn;
        preferences.DirectBitTorrent = DirectBitTorrentSwitch.IsOn;
        preferences.KillSwitch = KillSwitchToggle.IsOn;
        preferences.ShareWithLan = ShareWithLanSwitch.IsOn;
        preferences.ShareViaHotspot = ShareViaHotspotSwitch.IsOn;
        preferences.HotspotSocksPort = double.IsNaN(HotspotPortBox.Value) ? 10808 : (int)HotspotPortBox.Value;
        preferences.LanSocksPort = double.IsNaN(LanSharePortBox.Value) ? 1082 : (int)LanSharePortBox.Value;
        preferences.LanSocksUsername = ShareUsernameBox.Text.Trim();
        preferences.RouteTcp = RouteTcpCheckBox.IsChecked == true;
        preferences.RouteUdp = RouteUdpCheckBox.IsChecked == true;
        preferences.RouteIpv4 = RouteIpv4CheckBox.IsChecked == true;
        preferences.RouteIpv6 = RouteIpv6CheckBox.IsChecked == true;

        preferences.StartWithWindows = StartWithWindowsSwitch.IsOn;
        preferences.ConnectOnLaunch = ConnectOnLaunchSwitch.IsOn;
        preferences.MinimizeToTray = CloseToTraySwitch.IsOn;
        preferences.EnableNetworkAutomation = NetworkAutomationSwitch.IsOn;
        preferences.ConnectOnTrustedNetworks = ConnectOnTrustedNetworksSwitch.IsOn;
    }

    private async Task<bool> TrySavePreferencesAsync(InfoBar feedback)
    {
        try
        {
            await preferencesStore.SaveAsync(preferences);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddActivity("Settings could not be written to disk.", "ERROR");
            ShowInfo(feedback, InfoBarSeverity.Error, "Could not save", exception.Message);
            return false;
        }
    }

    private static void ApplyStartupRegistration(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        if (key is null)
        {
            throw new UnauthorizedAccessException("Windows startup settings are unavailable.");
        }

        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The PaqetFire executable path is unavailable.");
            key.SetValue("PaqetFire", $"\"{executablePath}\"");
        }
        else
        {
            key.DeleteValue("PaqetFire", throwOnMissingValue: false);
        }
    }

    private static string? TryResolveMacAddress(System.Net.IPAddress? gateway)
    {
        if (gateway is null)
        {
            return null;
        }

        try
        {
            using var ping = new Ping();
            _ = ping.Send(gateway, 300);
            var output = new System.Text.StringBuilder(256);
            var length = output.Capacity;
            if (GetIpNetTable2Mac(gateway.ToString(), output, ref length) == 0)
            {
                return output.ToString();
            }
        }
        catch
        {
            // Adapter detection still provides useful values when ARP lookup fails.
        }

        return null;
    }

    private static int GetIpNetTable2Mac(string address, System.Text.StringBuilder output, ref int length)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "arp.exe",
                Arguments = $"-a {address}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(500);
        var line = text.Split('\n').FirstOrDefault(candidate => candidate.Contains(address, StringComparison.Ordinal));
        var candidateMac = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.Count(character => character == '-') == 5);
        if (candidateMac is null)
        {
            return -1;
        }

        var normalized = candidateMac.Replace('-', ':').ToUpperInvariant();
        output.Append(normalized);
        length = normalized.Length;
        return 0;
    }

    private void OnSnapshotReceived(BrokerSnapshot snapshot)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => OnSnapshotReceived(snapshot));
            return;
        }

        var previousSnapshot = lastSnapshot;
        lastSnapshot = snapshot;

        UpdateProfileEntries(snapshot.ProfileCatalog);

        if (!brokerSettingsApplied && snapshot.Settings is { } settings)
        {
            brokerSettingsApplied = true;
            TransportKeyBox.Password = string.Empty;
            SharePasswordBox.Password = string.Empty;
            hasSavedTransportKey = settings.HasTransportKey;
            hasSavedLanSocksPassword = settings.HasLanSocksPassword;
            ProfileNameBox.Text = settings.ProfileName;
            ServerEndpointBox.Text = settings.ServerEndpoint;
            RoutingModeButtons.SelectedIndex = settings.RoutingMode == RoutingMode.SelectedApplications ? 1 : 0;
            SelectedApplicationsBox.Text = string.Join(Environment.NewLine, settings.SelectedApplications);
            UserExclusionsBox.Text = string.Join(Environment.NewLine, settings.UserExclusions);
            DirectRouteDestinationsBox.Text = string.Join(Environment.NewLine, settings.DirectRouteDestinations);
            isUpdatingHotspotInterlock = true;
            try
            {
                BypassLanSwitch.IsOn = settings.BypassLan;
                ShareViaHotspotSwitch.IsOn = settings.ShareViaHotspot;
            }
            finally
            {
                isUpdatingHotspotInterlock = false;
            }
            RouteTcpCheckBox.IsChecked = settings.RouteTcp;
            RouteUdpCheckBox.IsChecked = settings.RouteUdp;
            RouteIpv4CheckBox.IsChecked = settings.RouteIpv4;
            RouteIpv6CheckBox.IsChecked = settings.RouteIpv6;
            KillSwitchToggle.IsOn = settings.KillSwitchEnabled;
            ShareWithLanSwitch.IsOn = settings.ShareWithLan;
            HotspotPortBox.Value = settings.HotspotSocksPort;
            HotspotOptions.Visibility = settings.ShareViaHotspot ? Visibility.Visible : Visibility.Collapsed;
            LanSharePortBox.Value = settings.LanSocksPort;
            ShareUsernameBox.Text = settings.LanSocksUsername;
            UpdateSharedCredentialsVisibility();
            UpdateLanShareEndpointText();
            if (settings.ShareViaHotspot)
            {
                DetectHotspot(notifyOnSuccess: false, notifyOnFailure: false);
            }
            else
            {
                UpdateHotspotEndpointText();
                UpdateHotspotUri();
            }
            RegionalPresetBox.SelectedIndex = settings.RegionalPreset == RegionalRoutingPreset.None ? 1 : 0;
            DomainStrategyBox.SelectedIndex = settings.DomainStrategy switch
            {
                XrayDomainStrategy.AsIs => 0,
                XrayDomainStrategy.IPOnDemand => 2,
                _ => 1,
            };
            BlockAdsSwitch.IsOn = settings.BlockAds;
            BlockQuicSwitch.IsOn = settings.BlockQuic;
            DirectBitTorrentSwitch.IsOn = settings.DirectBitTorrent;
            LocalFlagsBox.Text = string.Join(", ", settings.LocalTcpFlags);
            RemoteFlagsBox.Text = string.Join(", ", settings.RemoteTcpFlags);
            KcpModeBox.SelectedIndex = settings.KcpMode.ToLowerInvariant() switch
            {
                "normal" => 0,
                "fast2" => 2,
                "fast3" => 3,
                _ => 1,
            };

            TransportKeyBox.PlaceholderText = hasSavedTransportKey
                ? "Saved securely · leave blank to keep it"
                : "Enter the shared Paqet key";
            SharePasswordBox.PlaceholderText = hasSavedLanSocksPassword
                ? "Saved securely · leave blank to keep it"
                : "At least 8 characters";
        }

        if (snapshot.Verification is { } verification)
        {
            ApplyVerification(verification);
        }

        if (snapshot.Prerequisites is { Count: > 0 } prerequisites)
        {
            this.prerequisites.Clear();
            foreach (var prerequisite in prerequisites)
            {
                this.prerequisites[prerequisite.Id] = prerequisite;
            }

            var missing = prerequisites.Where(item => !item.IsInstalled).ToArray();
            PrerequisiteSummaryText.Text = missing.Length == 0
                ? "Prerequisites · Ready"
                : $"Prerequisites · {missing.Length} missing";
            UpdatePrerequisiteCards();
        }
        else
        {
            this.prerequisites.Clear();
            PrerequisiteSummaryText.Text = "Prerequisites · No report";
            UpdatePrerequisiteCards();
        }

        foreach (var line in snapshot.RecentLogs ?? [])
        {
            if (brokerLogLines.Add(line))
            {
                brokerLogLineOrder.Enqueue(line);
                while (brokerLogLineOrder.Count > 400)
                {
                    brokerLogLines.Remove(brokerLogLineOrder.Dequeue());
                }

                AddActivity(line, line.Contains(" ERR ", StringComparison.Ordinal) ? "ERROR" : "ENGINE");
            }
        }

        if (preferences.EnableNetworkAutomation &&
            previousSnapshot is not null &&
            HasAutomationRelevantStateChanged(previousSnapshot, snapshot))
        {
            ScheduleNetworkAutomation(NetworkAutomationTrigger.StatusPoll, TimeSpan.FromMilliseconds(100));
        }
    }

    private static bool HasAutomationRelevantStateChanged(BrokerSnapshot previous, BrokerSnapshot current) =>
        previous.ConnectionState != current.ConnectionState ||
        previous.IsKillSwitchEnabled != current.IsKillSwitchEnabled ||
        previous.Engines.Count != current.Engines.Count ||
        previous.Engines.Any(oldEngine =>
            current.Engines.FirstOrDefault(candidate => candidate.Engine == oldEngine.Engine)?.State != oldEngine.State);

    private void ApplyVerification(ConnectionVerificationReport report)
    {
        VerificationEntries.Clear();
        foreach (var check in report.Checks)
        {
            var (glyph, status) = check.Status switch
            {
                VerificationCheckStatus.Passed => ("✓", "Passed"),
                VerificationCheckStatus.Warning => ("!", "Warning"),
                VerificationCheckStatus.Failed => ("×", "Failed"),
                _ => ("–", "Skipped"),
            };
            var detail = string.IsNullOrWhiteSpace(check.ObservedValue)
                ? check.Detail
                : $"{check.Detail} Observed: {check.ObservedValue}";
            VerificationEntries.Add(new VerificationDisplayItem(
                glyph,
                check.Name,
                status,
                detail,
                check.DurationMilliseconds > 0 ? $"{check.DurationMilliseconds} ms" : string.Empty));
        }

        VerificationSummaryText.Text = $"{report.Summary}. Completed {report.CompletedAt.ToLocalTime():g}.";
    }

    private void OnActivityOccurred(string message) => AddActivity(message);

    private void UpdatePrerequisiteCards()
    {
        UpdatePrerequisiteCard(
            "npcap",
            NpcapPrerequisiteStatusText,
            NpcapPrerequisiteDetailText,
            NpcapPrerequisiteButton,
            "Open official download");
        UpdatePrerequisiteCard(
            "winpkfilter",
            WinpkFilterPrerequisiteStatusText,
            WinpkFilterPrerequisiteDetailText,
            WinpkFilterPrerequisiteButton,
            "Download and install");
        UpdatePrerequisiteCard(
            "vcredist-x64",
            VisualCppPrerequisiteStatusText,
            VisualCppPrerequisiteDetailText,
            VisualCppPrerequisiteButton,
            "Download and install");
        UpdatePrerequisiteCard(
            "dotnet-framework",
            DotNetPrerequisiteStatusText,
            DotNetPrerequisiteDetailText,
            DotNetPrerequisiteButton,
            "Open Microsoft download");
    }

    private void UpdatePrerequisiteCard(
        string id,
        TextBlock statusText,
        TextBlock detailText,
        Button actionButton,
        string actionLabel)
    {
        if (!prerequisites.TryGetValue(id, out var prerequisite))
        {
            statusText.Text = "Not checked";
            actionButton.IsEnabled = false;
            return;
        }

        statusText.Text = prerequisite.IsInstalled ? "Installed" : "Missing";
        detailText.Text = prerequisite.Detail;
        actionButton.Content = prerequisite.IsInstalled ? "Installed" : actionLabel;
        actionButton.IsEnabled = !prerequisite.IsInstalled && !prerequisiteActionInProgress;
    }

    private void AddActivity(string message, string level = "INFO")
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => AddActivity(message, level));
            return;
        }

        ActivityEntries.Insert(0, new ActivityLogEntry(DateTimeOffset.Now.ToString("HH:mm:ss"), message, level));
        while (ActivityEntries.Count > 200)
        {
            ActivityEntries.RemoveAt(ActivityEntries.Count - 1);
        }
    }

    private static IReadOnlyList<string> SplitEntries(string value) => value
        .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> SplitLines(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static void ShowInfo(
        InfoBar infoBar,
        InfoBarSeverity severity,
        string title,
        string message)
    {
        infoBar.Severity = severity;
        infoBar.Title = title;
        infoBar.Message = message;
        infoBar.IsOpen = true;
    }

    private void MarkSecretsSaved(PaqetFireSettings settings)
    {
        hasSavedTransportKey = true;
        TransportKeyBox.Password = string.Empty;
        TransportKeyBox.PlaceholderText = "Saved securely · leave blank to keep it";

        if (settings.ShareWithLan || settings.ShareViaHotspot)
        {
            hasSavedLanSocksPassword = true;
            SharePasswordBox.Password = string.Empty;
            SharePasswordBox.PlaceholderText = "Saved securely · leave blank to keep it";
        }

        UpdateLanShareEndpointText();
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
        if (HotspotUriBox is null || HotspotIpBox is null || HotspotPortBox is null)
        {
            return;
        }

        var address = HotspotIpBox.Text.Trim();
        if (!ShareViaHotspotSwitch.IsOn || string.IsNullOrEmpty(address))
        {
            HotspotUriBox.Text = string.Empty;
            SyncGuideEndpoints();
            return;
        }

        var port = double.IsNaN(HotspotPortBox.Value) ? 10808 : (int)HotspotPortBox.Value;
        var username = ShareUsernameBox is not null && !string.IsNullOrWhiteSpace(ShareUsernameBox.Text)
            ? ShareUsernameBox.Text.Trim()
            : "paqetfire";

        var password = SharePasswordBox is not null && !string.IsNullOrEmpty(SharePasswordBox.Password)
            ? SharePasswordBox.Password
            : string.Empty;

        HotspotUriBox.Text = string.IsNullOrEmpty(password)
            ? string.Empty
            : SocksShareUri.CreateQrPayload(
                new SocksShareEndpoint(address, port),
                new SharedProxyCredentials(username, password));
        SyncGuideEndpoints();
    }

    private void DetectHotspot(bool notifyOnSuccess = false, bool notifyOnFailure = false)
    {
        if (HotspotIpBox is null || HotspotEndpointText is null || HotspotUriBox is null || HotspotPortBox is null)
        {
            return;
        }

        (string Address, string Name)? detected;
        try
        {
            detected = new HotspotNetworkDetector().TryDetect();
        }
        catch (NetworkInformationException)
        {
            // Adapters may disappear while Windows changes hotspot state.
            detected = null;
        }

        if (detected is not null)
        {
            HotspotIpBox.Text = detected.Value.Address;
            UpdateHotspotEndpointText();
            UpdateHotspotUri();

            if (notifyOnSuccess)
            {
                ShowInfo(ConnectionInfoBar, InfoBarSeverity.Success, "Hotspot detected",
                    $"Detected active mobile hotspot at {detected.Value.Address} ({detected.Value.Name}).");
                AddActivity($"Detected active mobile hotspot at {detected.Value.Address} ({detected.Value.Name}).");
            }
        }
        else
        {
            HotspotIpBox.Text = string.Empty;
            UpdateHotspotEndpointText();
            UpdateHotspotUri();

            if (notifyOnFailure)
            {
                ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "Hotspot not detected",
                    "Turn on Mobile hotspot in Windows Settings first, then click Detect hotspot.");
            }
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            unidentifiedNetworkNonce = Guid.NewGuid().ToString("N");
            if (ShareViaHotspotSwitch?.IsOn == true)
            {
                DetectHotspot(notifyOnSuccess: false, notifyOnFailure: false);
            }
            UpdateCurrentNetworkText();
            ScheduleNetworkAutomation(NetworkAutomationTrigger.NetworkChanged);
        });
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            DispatcherQueue.TryEnqueue(() =>
                ScheduleNetworkAutomation(NetworkAutomationTrigger.Resumed));
        }
    }

    private async void TrustCurrentNetworkButton_Click(object sender, RoutedEventArgs e) =>
        await SaveCurrentNetworkRuleAsync(isTrusted: true);

    private async void RequireRouteCurrentNetworkButton_Click(object sender, RoutedEventArgs e) =>
        await SaveCurrentNetworkRuleAsync(isTrusted: false);

    private async Task SaveCurrentNetworkRuleAsync(bool isTrusted)
    {
        var network = DetectNetworkContext();
        if (network is null)
        {
            ShowInfo(SettingsInfoBar, InfoBarSeverity.Warning, "Network unavailable",
                "Connect Ethernet or Wi-Fi before creating a network rule.");
            return;
        }

        var activeProfile = ProfileEntries.FirstOrDefault(profile => profile.IsActive);
        preferences.NetworkRules.RemoveAll(rule => string.Equals(rule.NetworkId, network.NetworkId, StringComparison.Ordinal));
        preferences.NetworkRules.Add(new NetworkRulePreference
        {
            NetworkId = network.NetworkId,
            DisplayName = GetCurrentNetworkDisplayName(),
            IsTrusted = isTrusted,
            ProfileId = activeProfile?.Id,
        });
        await preferencesStore.SaveAsync(preferences);
        currentNetworkContext = network;
        UpdateCurrentNetworkText(redetect: false);
        ShowInfo(SettingsInfoBar, InfoBarSeverity.Success, "Network rule saved",
            isTrusted
                ? "This network is trusted and may stay direct unless trusted-network routing is enabled."
                : "PaqetFire will require the active route on this network when automation is enabled.");
        ScheduleNetworkAutomation(NetworkAutomationTrigger.StatusPoll);
    }

    private void PauseNetworkAutomationButton_Click(object sender, RoutedEventArgs e)
    {
        networkAutomationPolicy.PauseAutoConnect(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));
        ShowInfo(SettingsInfoBar, InfoBarSeverity.Informational, "Automatic connection paused",
            "Established routing remains unchanged. New automatic connection attempts resume in 15 minutes.");
    }

    private void ScheduleNetworkAutomation(NetworkAutomationTrigger trigger, TimeSpan? delay = null)
    {
        networkAutomationCancellation?.Cancel();
        networkAutomationCancellation?.Dispose();
        networkAutomationCancellation = new CancellationTokenSource();
        var token = networkAutomationCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? TimeSpan.FromMilliseconds(350), token).ConfigureAwait(false);
                DispatcherQueue.TryEnqueue(() => _ = EvaluateNetworkAutomationAsync(trigger));
            }
            catch (OperationCanceledException)
            {
                // A newer network observation superseded this one.
            }
        }, token);
    }

    private async Task EvaluateNetworkAutomationAsync(NetworkAutomationTrigger trigger)
    {
        if (lastSnapshot is null)
        {
            return;
        }

        if (networkAutomationInProgress)
        {
            pendingNetworkAutomationTrigger = trigger;
            return;
        }

        networkAutomationInProgress = true;
        try
        {
            var network = DetectNetworkContext();
            currentNetworkContext = network;
            UpdateCurrentNetworkText(redetect: false);
            var defaultProfile = ProfileEntries.FirstOrDefault(profile => profile.IsDefault)?.Id;
            var settings = new NetworkAutomationSettings
            {
                IsEnabled = preferences.EnableNetworkAutomation,
                ConnectOnTrustedNetworks = preferences.ConnectOnTrustedNetworks,
                DefaultProfileId = defaultProfile,
                NetworkRules = preferences.NetworkRules
                    .Where(rule => !string.IsNullOrWhiteSpace(rule.NetworkId))
                    .Select(rule => new NetworkAutomationRule(rule.NetworkId, rule.IsTrusted, rule.ProfileId))
                    .ToArray(),
            };
            var decision = networkAutomationPolicy.Evaluate(new NetworkAutomationObservation(
                DateTimeOffset.UtcNow,
                trigger,
                lastSnapshot.ConnectionState,
                lastSnapshot.IsKillSwitchEnabled,
                lastSnapshot.Engines,
                network,
                lastSnapshot.Settings?.ShareViaHotspot == true,
                settings));

            if (decision.RetryAt is { } retryAt)
            {
                var wait = retryAt - DateTimeOffset.UtcNow;
                ScheduleNetworkAutomation(NetworkAutomationTrigger.StatusPoll,
                    wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50));
            }

            if (decision.Action == NetworkAutomationAction.Disconnect)
            {
                AddActivity($"Network automation is restarting the route: {decision.Reason}.");
                await ViewModel.DisconnectAsync();
                ScheduleNetworkAutomation(NetworkAutomationTrigger.StatusPoll, TimeSpan.FromMilliseconds(100));
            }
            else if (decision.Action == NetworkAutomationAction.Connect)
            {
                if (decision.ProfileId is { } profileId &&
                    ProfileEntries.FirstOrDefault(profile => profile.IsActive)?.Id != profileId)
                {
                    brokerSettingsApplied = false;
                    if (!await ViewModel.ManageProfileAsync(new ProfileAction(ProfileActionKind.Activate, profileId)))
                    {
                        ScheduleNetworkAutomation(NetworkAutomationTrigger.ConnectionAttemptFailed);
                        return;
                    }
                }

                AddActivity($"Network automation requested a connection: {decision.Reason}.");
                await ViewModel.ConnectAsync();
                if (!ViewModel.IsConnected)
                {
                    ScheduleNetworkAutomation(NetworkAutomationTrigger.ConnectionAttemptFailed);
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NetworkInformationException)
        {
            AddActivity($"Network automation could not evaluate the current network: {exception.Message}", "ERROR");
        }
        finally
        {
            networkAutomationInProgress = false;
            if (pendingNetworkAutomationTrigger is { } pendingTrigger)
            {
                pendingNetworkAutomationTrigger = null;
                ScheduleNetworkAutomation(pendingTrigger, TimeSpan.FromMilliseconds(50));
            }
        }
    }

    private NetworkContext? DetectNetworkContext()
    {
        Windows.Networking.Connectivity.ConnectionProfile? windowsProfile = null;
        try
        {
            windowsProfile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
        }
        catch
        {
            // Adapter inspection below still supports a single unambiguous route.
        }

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(candidate => candidate.OperationalStatus == OperationalStatus.Up)
            .Select(candidate => new { Adapter = candidate, Properties = candidate.GetIPProperties() })
            .Where(candidate =>
                candidate.Adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
                candidate.Properties.GatewayAddresses.Any(address => address.Address.AddressFamily == AddressFamily.InterNetwork))
            .ToArray();
        var activeAdapterId = windowsProfile?.NetworkAdapter?.NetworkAdapterId;
        var adapter = activeAdapterId is { } windowsAdapterId
            ? adapters.FirstOrDefault(candidate =>
                Guid.TryParse(candidate.Adapter.Id.Trim('{', '}'), out var candidateId) &&
                candidateId == windowsAdapterId)
            : adapters.Length == 1 ? adapters[0] : null;
        if (adapter is null)
        {
            // Never apply a trusted-network rule to an arbitrary interface on a
            // multihomed host when Windows cannot identify the active route.
            return null;
        }

        var gateway = adapter.Properties.GatewayAddresses
            .First(address => address.Address.AddressFamily == AddressFamily.InterNetwork).Address;
        var adapterId = adapter.Adapter.Id.Trim('{', '}');
        var windowsProfileName = windowsProfile?.ProfileName?.Trim();
        var gatewayMac = TryResolveMacAddress(gateway);
        var networkSignature = string.Join('|', new[] { windowsProfileName, gatewayMac }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (networkSignature.Length == 0)
        {
            // An ephemeral identity keeps an unidentified network in the safe "unknown"
            // bucket instead of accidentally matching a trusted rule from another LAN.
            networkSignature = $"unidentified:{unidentifiedNetworkNonce}";
        }
        var identity = Encoding.UTF8.GetBytes($"{adapterId}|{gateway}|{networkSignature}");
        var networkId = Convert.ToHexString(SHA256.HashData(identity))[..32];
        var connectivity = NetworkInterface.GetIsNetworkAvailable()
            ? NetworkConnectivity.Internet
            : NetworkConnectivity.Offline;
        try
        {
            connectivity = windowsProfile?.GetNetworkConnectivityLevel() switch
            {
                Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess => NetworkConnectivity.Internet,
                Windows.Networking.Connectivity.NetworkConnectivityLevel.ConstrainedInternetAccess => NetworkConnectivity.CaptivePortal,
                Windows.Networking.Connectivity.NetworkConnectivityLevel.LocalAccess => NetworkConnectivity.LocalOnly,
                _ => NetworkConnectivity.Offline,
            };
        }
        catch
        {
            // The adapter-derived fallback remains useful if Windows connectivity APIs are unavailable.
        }

        var kind = adapter.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
            ? NetworkKind.WiFi
            : adapter.Adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                ? NetworkKind.Ethernet
                : NetworkKind.Unknown;
        var isHostingHotspot = false;
        try
        {
            isHostingHotspot = new HotspotNetworkDetector().TryDetect() is not null;
        }
        catch (NetworkInformationException)
        {
            // Hotspot detection is advisory; routing decisions still use the upstream network.
        }

        return new NetworkContext(
            networkId,
            kind,
            connectivity,
            IsHostingHotspot: isHostingHotspot);
    }

    private string GetCurrentNetworkDisplayName()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate =>
                candidate.OperationalStatus == OperationalStatus.Up &&
                candidate.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
                candidate.GetIPProperties().GatewayAddresses.Count > 0);
            return adapter?.Name ?? "Current network";
        }
        catch (NetworkInformationException)
        {
            return "Current network";
        }
    }

    private void UpdateCurrentNetworkText(bool redetect = true)
    {
        if (redetect)
        {
            try
            {
                currentNetworkContext = DetectNetworkContext();
            }
            catch (NetworkInformationException)
            {
                currentNetworkContext = null;
            }
        }
        if (CurrentNetworkText is null) return;
        if (currentNetworkContext is null)
        {
            CurrentNetworkText.Text = "No upstream Ethernet or Wi-Fi network is currently available.";
            return;
        }

        var rule = preferences.NetworkRules.FirstOrDefault(candidate =>
            string.Equals(candidate.NetworkId, currentNetworkContext.NetworkId, StringComparison.Ordinal));
        var policy = rule is null ? "unknown" : rule.IsTrusted ? "trusted" : "route required";
        CurrentNetworkText.Text = $"Current: {GetCurrentNetworkDisplayName()} · {currentNetworkContext.Kind} · {currentNetworkContext.Connectivity} · {policy}.";
    }

    private void InitializeTrayIcon(string iconPath)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        trayStatusMenuItem = new System.Windows.Forms.ToolStripMenuItem("Status: Checking…")
        {
            Enabled = false,
        };
        trayConnectMenuItem = new System.Windows.Forms.ToolStripMenuItem("Connect", null, (_, _) => ConnectFromTray());
        trayDisconnectMenuItem = new System.Windows.Forms.ToolStripMenuItem("Disconnect", null, (_, _) => DisconnectFromTray());
        menu.Items.Add(trayStatusMenuItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(trayConnectMenuItem);
        menu.Items.Add(trayDisconnectMenuItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Open PaqetFire", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit PaqetFire", null, (_, _) => ExitFromTray());
        menu.Opening += (_, _) => UpdateTrayState();

        trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconPath),
            Text = "PaqetFire",
            ContextMenuStrip = menu,
            Visible = true,
            BalloonTipTitle = "PaqetFire is still running",
            BalloonTipText = "Right-click the icon to connect, disconnect, reopen, or exit PaqetFire.",
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        UpdateTrayState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ConnectionViewModel.ConnectionState) or
            nameof(ConnectionViewModel.CanConnect) or
            nameof(ConnectionViewModel.CanDisconnect) or
            nameof(ConnectionViewModel.IsBusy))
        {
            SetSaveActionsEnabled(!configurationSaveInProgress && !ViewModel.IsBusy);
            UpdateTrayState();
            UpdateConnectionVisuals();
        }
    }

    private void UpdateConnectionVisuals()
    {
        if (ConnectionStateIndicator is null || ConnectionStateIcon is null || ConnectionStateIconSurface is null)
        {
            return;
        }

        var resourceKey = ViewModel.ConnectionState switch
        {
            BrokerConnectionState.Connected => "PaqetFireSuccessBrush",
            BrokerConnectionState.Connecting => "PaqetFireAccentBrush",
            BrokerConnectionState.Guarded or BrokerConnectionState.NotReady or BrokerConnectionState.Degraded => "PaqetFireWarningBrush",
            BrokerConnectionState.Faulted => "PaqetFireErrorBrush",
            _ => "PaqetFireNeutralBrush",
        };
        var brush = Application.Current.Resources[resourceKey] as SolidColorBrush
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 138, 143, 152));
        ConnectionStateIndicator.Fill = brush;
        ConnectionStateIcon.Foreground = brush;
        ConnectionStateIconSurface.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
            24,
            brush.Color.R,
            brush.Color.G,
            brush.Color.B));
    }

    private void UpdateTrayState()
    {
        if (trayIcon is null || trayStatusMenuItem is null || trayConnectMenuItem is null || trayDisconnectMenuItem is null)
        {
            return;
        }

        var stateText = ViewModel.ConnectionState switch
        {
            BrokerConnectionState.Connected => "Connected",
            BrokerConnectionState.Connecting => "Connecting…",
            BrokerConnectionState.Disconnecting => "Disconnecting…",
            BrokerConnectionState.Guarded => "Routed apps blocked",
            BrokerConnectionState.NotReady => "Setup required",
            BrokerConnectionState.Degraded => "Connection degraded",
            BrokerConnectionState.Faulted => "Connection fault",
            _ => "Disconnected",
        };
        trayStatusMenuItem.Text = $"Status: {stateText}";
        trayConnectMenuItem.Enabled = ViewModel.CanConnect;
        trayDisconnectMenuItem.Enabled = ViewModel.CanDisconnect;
        trayIcon.Text = $"PaqetFire — {stateText}";
    }

    private void ConnectFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await ViewModel.ConnectAsync();
            UpdateTrayState();
        });
    }

    private void DisconnectFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await ViewModel.DisconnectAsync();
            UpdateTrayState();
        });
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (exitRequested || !preferences.MinimizeToTray)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
        if (trayIcon is not null)
        {
            trayIcon.Visible = true;
            if (!trayNoticeShown)
            {
                trayNoticeShown = true;
                trayIcon.ShowBalloonTip(2500);
            }
        }

        AddActivity("Window hidden to the notification area.");
    }

    private void RestoreFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show();
            Activate();
        });
    }

    private void ExitFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            exitRequested = true;
            if (trayIcon is not null)
            {
                trayIcon.Visible = false;
            }

            Close();
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        networkAutomationCancellation?.Cancel();
        networkAutomationCancellation?.Dispose();
        if (V2rayNgGuideView is not null)
        {
            V2rayNgGuideView.BackRequested -= OnGuideBackRequested;
        }
        if (HappGuideView is not null)
        {
            HappGuideView.BackRequested -= OnGuideBackRequested;
        }
        AppWindow.Closing -= OnAppWindowClosing;
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.ContextMenuStrip?.Dispose();
            trayIcon.Dispose();
            trayIcon = null;
        }

        ViewModel.ActivityOccurred -= OnActivityOccurred;
        ViewModel.SnapshotReceived -= OnSnapshotReceived;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        prerequisiteInstaller.Dispose();
        _ = ViewModel.DisposeAsync().AsTask();
    }
}
