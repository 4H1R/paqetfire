using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
using PaqetFire.Core.Updates;
using PaqetFire.Desktop.Ipc;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.Services;
using PaqetFire.Desktop.Services.ApplicationDiscovery;
using PaqetFire.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace PaqetFire.Desktop;

public sealed partial class MainWindow : Window
{
    private const string BusyMessage = "Another PaqetFire operation is still running. Try again when it finishes.";

    private readonly DesktopPreferencesStore preferencesStore = new();
    private readonly SemaphoreSlim preferencesSaveLock = new(1, 1);
    private readonly SemaphoreSlim appSettingsLock = new(1, 1);
    private readonly ServerReachabilityProbe serverReachabilityProbe = new();
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private bool updatingNetworkInterfaceSelection;
    private bool initialized;
    private bool brokerSettingsApplied;
    private bool hasSavedTransportKey;
    private bool hasSavedLanSocksPassword;
    private bool applyingAppSettings;
    private DesktopPreferences preferences = new();
    private readonly HashSet<string> brokerLogLines = new(StringComparer.Ordinal);
    private readonly Queue<string> brokerLogLineOrder = new();
    private readonly Dictionary<string, PrerequisiteStatus> prerequisites = new(StringComparer.OrdinalIgnoreCase);
    private readonly PrerequisiteInstallerService prerequisiteInstaller = new();
    private bool exitRequested;
    private bool prerequisiteActionInProgress;
    private bool isUpdatingHotspotInterlock;
    private bool configurationSaveInProgress;
    private bool trackingConfigurationChanges;
    private bool updatingProfileSelection;
    private bool hasPendingConfigurationChanges;
    private NetworkAutomationPolicy networkAutomationPolicy = new();
    private bool factoryResetInProgress;
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
    private readonly HttpClient updateHttpClient;
    private readonly GitHubUpdateService updateService;
    private readonly CancellationTokenSource updateCancellation = new();
    private SoftwareRelease? availableUpdate;
    private bool updateActionInProgress;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? updateCheckTimer;
    private Version? announcedUpdateVersion;
    private Version? dismissedUpdateVersion;
    private Task? initialization;
    private InfoBar? saveProgressFeedback;

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);

    private static readonly Version CurrentVersion = typeof(MainWindow).Assembly.GetName().Version
        ?? new Version(0, 0, 0);

    public ConnectionViewModel ViewModel { get; }

    public ObservableCollection<ActivityLogEntry> ActivityEntries { get; } = [];

    public ObservableCollection<VerificationDisplayItem> VerificationEntries { get; } = [];

    public ObservableCollection<ProfileDisplayItem> ProfileEntries { get; } = [];

    public ObservableCollection<ApplicationPickerItem> ApplicationEntries { get; } = [];

    public ObservableCollection<SetupChecklistItem> ChecklistEntries { get; } = [];

    public MainWindow()
    {
        updateHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        updateHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"PaqetFire/{GitHubUpdateService.FormatVersion(CurrentVersion)}");
        updateService = new GitHubUpdateService(updateHttpClient);
        InitializeNetworkServices();

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The UI dispatcher is unavailable.");
        ViewModel = new ConnectionViewModel(new NamedPipeBrokerClient(), new WinUiDispatcher(dispatcherQueue));
        ViewModel.ActivityOccurred += OnActivityOccurred;
        ViewModel.SnapshotReceived += OnSnapshotReceived;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        InitializeComponent();
        InitializeAdvancedOptions();
        InitializeExitProxyOptions();
        var versionText = GitHubUpdateService.FormatVersion(CurrentVersion);
        VersionBadgeText.Text = $"v{versionText}";
        AboutVersionText.Text = $"PaqetFire {versionText}";
        ToolTipService.SetToolTip(VersionBadge, $"Installed version {versionText}");
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
        V2rayNgGuideView.RotateCredentialsRequested += OnGuideRotateRequested;
        HappGuideView.RotateCredentialsRequested += OnGuideRotateRequested;
        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;
        Closed += OnClosed;
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e) => _ = InitializeAsync();

    /// <summary>Runs once, from the first load or from a hidden sign-in start that never shows the window.</summary>
    private Task InitializeAsync() => initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        initialized = true;
        AddActivity("PaqetFire opened.");
        preferences = await preferencesStore.LoadAsync();
        ApplyPreferences(preferences);
        MigrateStartupRegistration();
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

        if (preferences.CheckForUpdatesOnLaunch)
        {
            _ = CheckForUpdatesAsync(showCurrentVersionResult: false);
        }

        updateCheckTimer = DispatcherQueue.CreateTimer();
        updateCheckTimer.Interval = UpdateCheckInterval;
        updateCheckTimer.IsRepeating = true;
        updateCheckTimer.Tick += (_, _) =>
        {
            if (preferences.CheckForUpdatesOnLaunch && !exitRequested)
            {
                _ = CheckForUpdatesAsync(showCurrentVersionResult: false);
            }
        };
        updateCheckTimer.Start();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ConnectAsync();

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.DisconnectAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshAsync();

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showCurrentVersionResult: true);

    private async Task CheckForUpdatesAsync(bool showCurrentVersionResult)
    {
        if (updateActionInProgress)
        {
            return;
        }

        updateActionInProgress = true;
        CheckForUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub Releases…";
        try
        {
            var result = await updateService.CheckAsync(CurrentVersion, updateCancellation.Token);
            availableUpdate = result.IsUpdateAvailable ? result.LatestRelease : null;
            if (availableUpdate is not null)
            {
                var latestVersion = GitHubUpdateService.FormatVersion(availableUpdate.Version);
                UpdateStatusText.Text = $"Version {latestVersion} is ready to install.";
                VersionBadgeText.Text = $"v{GitHubUpdateService.FormatVersion(CurrentVersion)} · update";
                ToolTipService.SetToolTip(VersionBadge, $"PaqetFire {latestVersion} is available in Settings");
                InstallUpdateButton.Content = $"Download and install {latestVersion}";
                InstallUpdateButton.Visibility = Visibility.Visible;
                ViewReleaseButton.Visibility = Visibility.Visible;
                ShowInfo(
                    UpdateInfoBar,
                    InfoBarSeverity.Informational,
                    "Update available",
                    $"PaqetFire {latestVersion} is newer than the installed version. The installer will be verified before it opens.");
                AnnounceUpdate(availableUpdate);
            }
            else
            {
                var versionText = GitHubUpdateService.FormatVersion(CurrentVersion);
                UpdateStatusText.Text = $"No release newer than PaqetFire {versionText} was found.";
                VersionBadgeText.Text = $"v{versionText}";
                ToolTipService.SetToolTip(VersionBadge, $"Installed version {versionText}");
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                ViewReleaseButton.Visibility = Visibility.Collapsed;
                UpdateToastInfoBar.IsOpen = false;
                if (showCurrentVersionResult)
                {
                    ShowInfo(
                        UpdateInfoBar,
                        InfoBarSeverity.Success,
                        "You're up to date",
                        $"No GitHub release newer than the installed version ({versionText}) was found.");
                }
            }
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = "PaqetFire could not check GitHub Releases. You can try again.";
            if (showCurrentVersionResult)
            {
                ShowInfo(UpdateInfoBar, InfoBarSeverity.Warning, "Update check failed", exception.Message);
            }
            AddActivity("The update check could not be completed.", "WARN");
        }
        finally
        {
            updateActionInProgress = false;
            if (!exitRequested)
            {
                CheckForUpdatesButton.IsEnabled = true;
                InstallUpdateButton.IsEnabled = availableUpdate is not null;
            }
        }
    }

    private void AnnounceUpdate(SoftwareRelease release)
    {
        var latestVersion = GitHubUpdateService.FormatVersion(release.Version);
        if (release.Version != dismissedUpdateVersion)
        {
            UpdateToastInfoBar.Title = $"PaqetFire {latestVersion} is available";
            UpdateToastInfoBar.Message =
                $"You have {GitHubUpdateService.FormatVersion(CurrentVersion)}. The installer is checksum-verified before it opens.";
            UpdateToastInstallButton.IsEnabled = true;
            UpdateToast.Visibility = Visibility.Visible;
            UpdateToastInfoBar.IsOpen = true;
        }

        if (release.Version == announcedUpdateVersion)
        {
            return;
        }

        announcedUpdateVersion = release.Version;
        AddActivity($"PaqetFire update {latestVersion} is available.");
        if (!AppWindow.IsVisible)
        {
            ShowTrayBalloon(
                $"PaqetFire {latestVersion} is available",
                "Click to open PaqetFire and install the update.",
                System.Windows.Forms.ToolTipIcon.Info,
                TrayBalloonAction.OpenUpdate);
        }
    }

    private void UpdateToastInstallButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateToastInfoBar.IsOpen = false;
        NavigateTo("settings");
        InstallUpdateButton.StartBringIntoView();
        InstallUpdateButton_Click(sender, e);
    }

    private void UpdateToastLaterButton_Click(object sender, RoutedEventArgs e)
    {
        dismissedUpdateVersion = availableUpdate?.Version;
        UpdateToastInfoBar.IsOpen = false;
    }

    private void UpdateToastInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            dismissedUpdateVersion = availableUpdate?.Version;
        }

        UpdateToast.Visibility = Visibility.Collapsed;
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is null || updateActionInProgress)
        {
            return;
        }

        updateActionInProgress = true;
        CheckForUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateStatusText.Text = "Reading the published checksum…";

        try
        {
            var progress = new Progress<SoftwareUpdateDownloadProgress>(download =>
            {
                if (download.Percentage is double percentage)
                {
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = percentage;
                    UpdateStatusText.Text = $"Downloading update… {percentage:0}%";
                }
                else
                {
                    UpdateStatusText.Text = $"Downloading update… {download.BytesReceived / 1048576d:0.0} MB";
                }
            });
            var installerPath = await updateService.DownloadVerifiedInstallerAsync(
                availableUpdate,
                progress,
                updateCancellation.Token);

            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 100;
            UpdateStatusText.Text = "Download verified. Opening Windows Installer…";
            AddActivity("The PaqetFire update was downloaded and checksum-verified.");

            SoftwareUpdateInstaller.Start(installerPath);

            exitRequested = true;
            if (trayIcon is not null)
            {
                trayIcon.Visible = false;
            }
            Close();
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "The update was not installed. You can try again.";
            ShowInfo(UpdateInfoBar, InfoBarSeverity.Error, "Update failed", exception.Message);
            AddActivity("The PaqetFire update could not be installed.", "ERROR");
        }
        finally
        {
            updateActionInProgress = false;
            if (!exitRequested)
            {
                CheckForUpdatesButton.IsEnabled = true;
                InstallUpdateButton.IsEnabled = availableUpdate is not null;
            }
        }
    }

    private void ViewReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                availableUpdate.ReleasePageUri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowInfo(UpdateInfoBar, InfoBarSeverity.Warning, "Release page could not be opened", exception.Message);
        }
    }

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
                ViewModel.LastRequestWasBusy
                    ? BusyMessage
                    : ViewModel.ErrorMessage ?? "The broker did not return a verification report.");
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
        switch (ViewModel.PrimaryAction)
        {
            case PrimaryConnectionAction.SetUpProfile:
                FocusServerEndpoint();
                break;
            case PrimaryConnectionAction.OpenRequiredSoftware:
                ShowRequiredSoftware();
                break;
            case PrimaryConnectionAction.OpenDiagnostics:
                NavigateTo("diagnostics");
                break;
            case PrimaryConnectionAction.RetryBroker:
                await ViewModel.RefreshAsync();
                break;
            case PrimaryConnectionAction.Disconnect:
                await ViewModel.DisconnectAsync();
                break;
            case PrimaryConnectionAction.Connect:
                await ViewModel.ConnectAsync();
                break;
        }
    }

    private async void CancelConnectButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.CancelConnectAsync();

    private async void OverviewErrorActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (ViewModel.FailureAction)
        {
            case FailureAction.OpenRequiredSoftware:
                ShowRequiredSoftware();
                break;
            case FailureAction.CopyDiagnostics:
                await CopyDiagnosticsAsync(OverviewInfoBar);
                break;
            case FailureAction.ReviewProfile:
                FocusServerEndpoint();
                break;
        }
    }

    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Ctrl+S saves only where the form is on screen, so it never applies edits unseen.
        InfoBar? feedback = ConnectionPage.Visibility == Visibility.Visible ? ConnectionInfoBar
            : RoutingPage.Visibility == Visibility.Visible ? RoutingInfoBar
            : null;
        if (feedback is null)
        {
            return;
        }

        args.Handled = true;
        await SaveConfigurationAsync(feedback, connectAfterSave: false);
    }

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ViewModel.RefreshAsync();
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

    // Invoking the already-selected item raises no SelectionChanged; still leave a guide page.
    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args) =>
        ShowPage(args.IsSettingsInvoked
            ? "settings"
            : args.InvokedItemContainer?.Tag?.ToString() ?? "overview");

    private void NavigateLink_Click(object sender, RoutedEventArgs e)
    {
        var destination = (sender as FrameworkElement)?.Tag?.ToString() ?? "overview";
        NavigateTo(destination);
    }

    private void NavigateTo(string destination)
    {
        if (destination == "settings")
        {
            Navigation.SelectedItem = Navigation.SettingsItem;
        }
        else if (Navigation.MenuItems
                     .OfType<NavigationViewItem>()
                     .FirstOrDefault(candidate => string.Equals(
                         candidate.Tag?.ToString(),
                         destination,
                         StringComparison.Ordinal)) is { } item)
        {
            Navigation.SelectedItem = item;
        }

        // Selecting the current item raises no SelectionChanged, so show the page directly.
        ShowPage(destination);
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

    private void RoutingModeButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfigurationEdited();
        UpdateFakeDnsCompatibility();
        var selectedOnly = RoutingModeButtons.SelectedIndex == 1;
        SelectedAppsCard.Visibility = selectedOnly ? Visibility.Visible : Visibility.Collapsed;
        RoutingModeHelpText.Text = selectedOnly
            ? "Only the listed executables are proxied. Other applications connect directly."
            : "All application traffic enters the PaqetFire route. Paqet, Xray, Psiphon, ProxiFyre, and the broker are excluded to prevent a loop.";
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

    private void FormGrid_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid grid || grid.ColumnDefinitions.Count < 2) return;
        var scale = uiSettings.TextScaleFactor;
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
            if (connectAfterSave)
            {
                saveProgressFeedback = feedback;
            }

            CapturePreferences();
            if (!await TrySavePreferencesAsync(feedback)) return;
            if (!await ViewModel.SaveSettingsAsync(settings, connectAfterSave))
            {
                // Nothing was stored: keep the pending edits and the secret boxes as they are.
                if (ViewModel.LastSaveOutcome == SaveOutcome.Cancelled)
                {
                    ShowInfo(feedback, InfoBarSeverity.Warning, "Save was cancelled — nothing was stored",
                        "Your changes are still pending. Save again when you are ready.");
                    return;
                }

                ShowInfo(feedback, InfoBarSeverity.Error, "Configuration was not applied",
                    ViewModel.LastRequestWasBusy
                        ? BusyMessage
                        : ViewModel.ErrorMessage ?? "The broker rejected the configuration. Try saving again.");
                return;
            }
            MarkSecretsSaved(settings);
            SetPendingChanges(false);
            if (ViewModel.LastSaveOutcome == SaveOutcome.SavedConnectionCancelled)
            {
                ShowInfo(feedback, InfoBarSeverity.Informational, "Connection cancelled — your changes were saved",
                    "The connection profile and routing policy are stored. Connect when you are ready.");
            }
            else
            {
                ShowInfo(feedback, ViewModel.HasError ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                    ViewModel.HasError ? "Saved — connection needs attention" : "Configuration applied",
                    ViewModel.ErrorMessage ?? "The connection profile and routing policy were saved together.");
            }

            AddActivity("Connection profile and routing policy saved.");
        }
        finally
        {
            saveProgressFeedback = null;
            configurationSaveInProgress = false;
            SetConfigurationEditingEnabled(true);
            SetSaveActionsEnabled(!ViewModel.IsBusy);
        }
    }

    /// <summary>
    /// Settings switches apply and save immediately, one at a time. A failed save puts back
    /// only the switch that failed, together with the Windows Run entry it changed.
    /// </summary>
    private async void AppSettingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (applyingAppSettings || !initialized || sender is not ToggleSwitch toggle ||
            AppSettingFor(toggle) is not { } setting)
        {
            return;
        }

        var value = toggle.IsOn;
        await appSettingsLock.WaitAsync();
        try
        {
            var previous = setting.Get();
            if (previous == value)
            {
                return;
            }

            setting.Set(value);
            var runEntryChanged = false;
            try
            {
                if (toggle == StartWithWindowsSwitch)
                {
                    ApplyStartupRegistration(value);
                    runEntryChanged = true;
                }

                await SavePreferencesAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                setting.Set(previous);
                if (runEntryChanged)
                {
                    try
                    {
                        ApplyStartupRegistration(previous);
                    }
                    catch (Exception registryException) when (registryException is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                    {
                        AddActivity("The Start with Windows entry could not be restored.", "ERROR");
                    }
                }

                applyingAppSettings = true;
                toggle.IsOn = previous;
                applyingAppSettings = false;
                ShowInfo(SettingsInfoBar, InfoBarSeverity.Error, "Setting could not be saved", exception.Message);
                AddActivity("Application settings could not be saved.", "ERROR");
                return;
            }

            SettingsInfoBar.IsOpen = false;
            if (toggle == CloseToTraySwitch && trayIcon is not null)
            {
                trayIcon.Visible = preferences.MinimizeToTray;
            }

            AddActivity($"Setting changed: {toggle.Header} {(value ? "on" : "off")}.");
            if (toggle == NetworkAutomationSwitch || toggle == ConnectOnTrustedNetworksSwitch)
            {
                ScheduleNetworkAutomation(NetworkAutomationTrigger.StatusPoll);
            }
        }
        finally
        {
            appSettingsLock.Release();
        }
    }

    private (Func<bool> Get, Action<bool> Set)? AppSettingFor(ToggleSwitch toggle) =>
        toggle == StartWithWindowsSwitch ? (() => preferences.StartWithWindows, value => preferences.StartWithWindows = value)
        : toggle == ConnectOnLaunchSwitch ? (() => preferences.ConnectOnLaunch, value => preferences.ConnectOnLaunch = value)
        : toggle == CloseToTraySwitch ? (() => preferences.MinimizeToTray, value => preferences.MinimizeToTray = value)
        : toggle == CheckForUpdatesSwitch ? (() => preferences.CheckForUpdatesOnLaunch, value => preferences.CheckForUpdatesOnLaunch = value)
        : toggle == NetworkAutomationSwitch ? (() => preferences.EnableNetworkAutomation, value => preferences.EnableNetworkAutomation = value)
        : toggle == ConnectOnTrustedNetworksSwitch ? (() => preferences.ConnectOnTrustedNetworks, value => preferences.ConnectOnTrustedNetworks = value)
        : toggle == ShowNotificationsSwitch ? (() => preferences.ShowNotifications, value => preferences.ShowNotifications = value)
        : null;

    /// <summary>Runs a fire-and-forget UI action (tray menu, timers) so a failure is reported instead of fatal.</summary>
    private async void RunSafely(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportUnexpectedError(exception);
        }
    }

    /// <summary>Last-resort report for an exception that escaped an event handler.</summary>
    public void ReportUnexpectedError(Exception exception)
    {
        AddActivity($"Unexpected error: {exception.GetType().Name}: {exception.Message}", "ERROR");
        if (DispatcherQueue.HasThreadAccess)
        {
            ShowInfo(OverviewInfoBar, InfoBarSeverity.Error, "Something went wrong",
                "PaqetFire hit an unexpected error and kept running. Copy diagnostics from the Diagnostics page if it happens again.");
        }
    }

    private void ApplyAppSettings(DesktopPreferences source)
    {
        applyingAppSettings = true;
        try
        {
            StartWithWindowsSwitch.IsOn = source.StartWithWindows;
            ConnectOnLaunchSwitch.IsOn = source.ConnectOnLaunch;
            CloseToTraySwitch.IsOn = source.MinimizeToTray;
            CheckForUpdatesSwitch.IsOn = source.CheckForUpdatesOnLaunch;
            NetworkAutomationSwitch.IsOn = source.EnableNetworkAutomation;
            ConnectOnTrustedNetworksSwitch.IsOn = source.ConnectOnTrustedNetworks;
            ShowNotificationsSwitch.IsOn = source.ShowNotifications;
        }
        finally
        {
            applyingAppSettings = false;
        }
    }

    private void CaptureAppSettings()
    {
        preferences.StartWithWindows = StartWithWindowsSwitch.IsOn;
        preferences.ConnectOnLaunch = ConnectOnLaunchSwitch.IsOn;
        preferences.MinimizeToTray = CloseToTraySwitch.IsOn;
        preferences.CheckForUpdatesOnLaunch = CheckForUpdatesSwitch.IsOn;
        preferences.EnableNetworkAutomation = NetworkAutomationSwitch.IsOn;
        preferences.ConnectOnTrustedNetworks = ConnectOnTrustedNetworksSwitch.IsOn;
        preferences.ShowNotifications = ShowNotificationsSwitch.IsOn;
    }

    private async void FactoryResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || factoryResetInProgress || Content?.XamlRoot is null) return;
        var confirmation = new ContentDialog
        {
            Title = "Reset PaqetFire to defaults?",
            Content = "This disconnects the route, turns off the kill switch, and removes all profiles, transport keys, SOCKS credentials, network rules, and app preferences. Export your profiles first if you want to keep them.",
            PrimaryButtonText = "Reset app",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowDialogAsync(confirmation) != ContentDialogResult.Primary) return;
        factoryResetInProgress = true;
        FactoryResetButton.IsEnabled = false;
        networkAutomationCancellation?.Cancel();
        pendingNetworkAutomationTrigger = null;
        try
        {
            if (!await ViewModel.ManageProfileAsync(new(ProfileActionKind.FactoryReset)))
            {
                ShowInfo(SettingsInfoBar, InfoBarSeverity.Error, "Reset could not finish",
                    ViewModel.ErrorMessage ?? "The broker could not reset the app. Try again.");
                return;
            }
            preferences = new DesktopPreferences();
            ApplyStartupRegistration(false);
            await SavePreferencesAsync();
            networkAutomationPolicy = new();
            hasSavedTransportKey = hasSavedLanSocksPassword = false;
            sessionSharePassword = string.Empty;
            TransportKeyBox.Password = SharePasswordBox.Password = string.Empty;
            ApplyPreferences(preferences);
            brokerSettingsApplied = false;
            if (lastSnapshot is { } snapshot) OnSnapshotReceived(snapshot);
            SetPendingChanges(false);
            ClearFieldErrors();
            VerificationEntries.Clear();
            ActivityEntries.Clear();
            ShowInfo(SettingsInfoBar, InfoBarSeverity.Success, "App reset",
                "All settings are back to their defaults. Add or import a connection profile to get started.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowInfo(SettingsInfoBar, InfoBarSeverity.Error, "Reset could not finish", exception.Message);
        }
        finally
        {
            factoryResetInProgress = false;
            FactoryResetButton.IsEnabled = true;
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

    private async void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        await CopyDiagnosticsAsync(VerificationInfoBar);

    private async Task CopyDiagnosticsAsync(InfoBar feedback)
    {
        var diagnostics = await ViewModel.GetDiagnosticsAsync();
        if (diagnostics is null)
        {
            ShowInfo(feedback, InfoBarSeverity.Error, "Diagnostics unavailable",
                ViewModel.ErrorMessage ?? "The broker did not return a diagnostic report.");
            return;
        }

        var activity = string.Join(Environment.NewLine, ActivityEntries.Select(entry =>
            $"{entry.Time} [{entry.Level}] {entry.Message}"));
        var package = new DataPackage();
        package.SetText(diagnostics + Environment.NewLine + "[Desktop activity]" + Environment.NewLine +
            DiagnosticRedactor.Redact(activity, []));
        Clipboard.SetContent(package);
        ShowInfo(feedback, InfoBarSeverity.Success, "Diagnostics copied",
            "Paste the report into your bug report. Review it first; secrets, the server host, and public IP addresses are redacted.");
        AddActivity("Diagnostic report copied to the clipboard.");
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        ActivityEntries.Clear();
        AddActivity("Activity log cleared.");
    }

    /// <summary>Saves are serialized: the store writes through one temporary file.</summary>
    private async Task SavePreferencesAsync()
    {
        await preferencesSaveLock.WaitAsync();
        try
        {
            await preferencesStore.SaveAsync(preferences);
        }
        finally
        {
            preferencesSaveLock.Release();
        }
    }

    private async Task<bool> TrySavePreferencesAsync(InfoBar feedback)
    {
        try
        {
            await SavePreferencesAsync();
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
            key.SetValue("PaqetFire", StartupCommand.Create(executablePath));
        }
        else
        {
            key.DeleteValue("PaqetFire", throwOnMissingValue: false);
        }
    }

    /// <summary>Older Run values start the window visibly; rewrite them to start in the notification area.</summary>
    private void MigrateStartupRegistration()
    {
        if (!preferences.StartWithWindows || Environment.ProcessPath is not { } executablePath)
        {
            return;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartupCommand.NeedsUpdate(key?.GetValue("PaqetFire") as string, executablePath))
            {
                ApplyStartupRegistration(true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            AddActivity("The Start with Windows entry could not be updated.", "WARN");
        }
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

        // Populate once per load or switch, and again if another profile became active behind
        // the form's back, so a save can never write one profile's fields into another. While a
        // profile action runs, its reply does the populating.
        var activeProfileId = ActiveProfileId(snapshot);
        if (profileActionDepth == 0 && snapshot.Settings is { } settings &&
            (!brokerSettingsApplied || (activeProfileId is not null && formProfileId is not null && activeProfileId != formProfileId)))
        {
            if (brokerSettingsApplied && hasPendingConfigurationChanges)
            {
                ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "Active profile changed",
                    $"'{settings.ProfileName}' became the active profile, so the unsaved edits for the previous profile were discarded.");
            }

            PopulateFormFromBroker(settings, activeProfileId);
        }

        if (snapshot.Verification is { } verification)
        {
            ApplyVerification(verification);
        }

        UpdatePrerequisites(snapshot, previousSnapshot);
        UpdateExitProxyStatus(snapshot);
        if (RouteNotificationPolicy.Classify(previousSnapshot, snapshot, ViewModel.IsUserTransition) is { } notification)
        {
            NotifyRouteTransition(notification);
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

    private static IReadOnlyList<string> SplitLines(string value) => ConnectionFormState.SplitLines(value);

    private void ShowInfo(
        InfoBar infoBar,
        InfoBarSeverity severity,
        string title,
        string message)
    {
        infoBar.Severity = severity;
        infoBar.Title = title;
        infoBar.Message = message;
        infoBar.IsOpen = true;

        // Screen readers do not notice an InfoBar opening elsewhere on the page; announce it.
        if (AppWindow.IsVisible &&
            (FrameworkElementAutomationPeer.FromElement(infoBar) ?? FrameworkElementAutomationPeer.CreatePeerForElement(infoBar)) is { } peer)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                severity == InfoBarSeverity.Error ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                $"{title}. {message}",
                "PaqetFireStatus");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ConnectionViewModel.ConnectionState) or
            nameof(ConnectionViewModel.CanConnect) or
            nameof(ConnectionViewModel.CanDisconnect) or
            nameof(ConnectionViewModel.CanCancelConnect) or
            nameof(ConnectionViewModel.IsBusy) or
            nameof(ConnectionViewModel.IsReconnecting) or
            nameof(ConnectionViewModel.IsTunnelStalled))
        {
            SetSaveActionsEnabled(!configurationSaveInProgress && !ViewModel.IsBusy);
            UpdateTrayState();
            UpdateConnectionVisuals();
            if (lastSnapshot is not null && args.PropertyName == nameof(ConnectionViewModel.ConnectionState))
            {
                UpdateSetupChecklist(lastSnapshot);
            }
        }
        else if (args.PropertyName is nameof(ConnectionViewModel.RoutingData))
        {
            ApplyRoutingDataStatus(ViewModel.RoutingData);
        }
        else if (args.PropertyName is nameof(ConnectionViewModel.ProgressSummary))
        {
            UpdateTrayState();
            if (saveProgressFeedback is { } feedback && ViewModel.HasProgress)
            {
                feedback.Message = $"Saving, then connecting · {ViewModel.ProgressSummary}";
            }
        }
        else if (args.PropertyName is nameof(ConnectionViewModel.StatusText) && ViewModel.HasProgress is false &&
                 OverviewPage.Visibility == Visibility.Visible && AppWindow.IsVisible &&
                 FrameworkElementAutomationPeer.FromElement(StatusTextBlock) is { } peer)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent,
                ViewModel.StatusText,
                "PaqetFireConnectionState");
        }
    }

    public Brush QualityBrush(LatencyQuality quality)
    {
        var resourceKey = quality switch
        {
            LatencyQuality.Excellent or LatencyQuality.Good => "PaqetFireSuccessBrush",
            LatencyQuality.Fair => "PaqetFireWarningBrush",
            LatencyQuality.Poor or LatencyQuality.Unreachable => "PaqetFireErrorBrush",
            _ => "PaqetFireNeutralBrush",
        };
        return (Brush)Application.Current.Resources[resourceKey];
    }

    private void UpdateConnectionVisuals()
    {
        if (ConnectionStateIndicator is null || ConnectionStateIcon is null || ConnectionStateIconSurface is null)
        {
            return;
        }

        var resourceKey = ViewModel.ConnectionState switch
        {
            BrokerConnectionState.Connected when ViewModel.IsTunnelStalled => "PaqetFireWarningBrush",
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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        networkAutomationCancellation?.Cancel();
        networkAutomationCancellation?.Dispose();
        networkChangeDebounce?.Cancel();
        updateCheckTimer?.Stop();
        updateCancellation.Cancel();
        updateCancellation.Dispose();
        updateHttpClient.Dispose();
        if (V2rayNgGuideView is not null)
        {
            V2rayNgGuideView.BackRequested -= OnGuideBackRequested;
        }
        if (HappGuideView is not null)
        {
            HappGuideView.BackRequested -= OnGuideBackRequested;
            HappGuideView.RotateCredentialsRequested -= OnGuideRotateRequested;
        }
        if (V2rayNgGuideView is not null)
        {
            V2rayNgGuideView.RotateCredentialsRequested -= OnGuideRotateRequested;
        }
        AppWindow.Closing -= OnAppWindowClosing;
        Activated -= OnWindowActivated;
        DisposeTrayIcon();

        ViewModel.ActivityOccurred -= OnActivityOccurred;
        ViewModel.SnapshotReceived -= OnSnapshotReceived;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        prerequisiteInstaller.Dispose();
        _ = ViewModel.DisposeAsync().AsTask();
    }
}
