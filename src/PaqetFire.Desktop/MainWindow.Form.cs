using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;

namespace PaqetFire.Desktop;

/// <summary>Connection and routing form: one population path, change tracking, and validation.</summary>
public sealed partial class MainWindow
{
    private readonly List<(Panel Parent, TextBlock Error)> fieldErrors = [];
    private ConnectionFormState? appliedFormState;
    private int formPopulationDepth;
    private string currentProfileName = "Default";
    private Guid? selectedInterfaceGuid;
    private bool configurationEditingEnabled = true;
    // The profile whose fields the form shows, and how many profile actions are in flight.
    private Guid? formProfileId;
    private int profileActionDepth;
    private bool dialogOpen;

    private bool IsPopulatingForm => formPopulationDepth > 0;

    /// <summary>Writes a form state into the controls without it counting as an edit.</summary>
    private void PopulateForm(ConnectionFormState state)
    {
        formPopulationDepth++;
        try
        {
            currentProfileName = state.ProfileName;
            LoadAdvancedOptions(state.Advanced);
            ServerEndpointBox.Text = state.ServerEndpoint;
            selectedInterfaceGuid = state.NetworkInterfaceGuid;
            _ = RefreshNetworkAdapterAsync(state.NetworkInterfaceGuid, AdapterRefreshMode.Load);
            SelectKcpMode(state.KcpMode);
            LocalFlagsBox.Text = state.LocalTcpFlags;
            RemoteFlagsBox.Text = state.RemoteTcpFlags;
            RoutingModeButtons.SelectedIndex = state.RouteAllApplications ? 0 : 1;
            SelectedApplicationsBox.Text = ToMultiline(state.SelectedApplications);
            UserExclusionsBox.Text = ToMultiline(state.UserExclusions);
            DirectRouteDestinationsBox.Text = ToMultiline(state.DirectRouteDestinations);
            isUpdatingHotspotInterlock = true;
            try
            {
                BypassLanSwitch.IsOn = state.BypassLan;
                ShareViaHotspotSwitch.IsOn = state.ShareViaHotspot;
            }
            finally
            {
                isUpdatingHotspotInterlock = false;
            }

            RegionalPresetBox.SelectedIndex = state.RegionalPreset == RegionalRoutingPreset.None ? 1 : 0;
            DomainStrategyBox.SelectedIndex = state.DomainStrategy switch
            {
                XrayDomainStrategy.AsIs => 0,
                XrayDomainStrategy.IPOnDemand => 2,
                _ => 1,
            };
            SelectDnsResolver(state.DnsResolver);
            FakeDnsSwitch.IsOn = state.FakeDns;
            BlockAdsSwitch.IsOn = state.BlockAds;
            BlockMalwareSwitch.IsOn = state.BlockMalware;
            BlockQuicSwitch.IsOn = state.BlockQuic;
            DirectBitTorrentSwitch.IsOn = state.DirectBitTorrent;
            SelectExitProxy(state.ExitProxy);
            SelectPsiphonRegion(state.PsiphonRegion);
            OpenVpnUsernameBox.Text = state.OpenVpnUsername;
            KillSwitchToggle.IsOn = state.KillSwitch;
            ShareWithLanSwitch.IsOn = state.ShareWithLan;
            LanShareOptions.Visibility = state.ShareWithLan ? Visibility.Visible : Visibility.Collapsed;
            LanSharePortBox.Value = state.LanSocksPort;
            HotspotPortBox.Value = state.HotspotSocksPort;
            HotspotOptions.Visibility = state.ShareViaHotspot ? Visibility.Visible : Visibility.Collapsed;
            ShareUsernameBox.Text = state.LanSocksUsername;
            RouteTcpCheckBox.IsChecked = state.RouteTcp;
            RouteUdpCheckBox.IsChecked = state.RouteUdp;
            RouteIpv4CheckBox.IsChecked = state.RouteIpv4;
            RouteIpv6CheckBox.IsChecked = state.RouteIpv6;
            SelectedAppsCard.Visibility = state.RouteAllApplications ? Visibility.Collapsed : Visibility.Visible;
            UpdateSharedCredentialsVisibility();
            UpdateLanShareEndpointText();
            if (state.ShareViaHotspot)
            {
                _ = DetectHotspotAsync(notifyOnSuccess: false, notifyOnFailure: false);
            }
            else
            {
                UpdateHotspotEndpointText();
                UpdateHotspotUri();
            }

            UpdateFakeDnsCompatibility();
        }
        finally
        {
            formPopulationDepth--;
        }

        // Compare later edits with what the controls actually show, not the source object:
        // text boxes normalize line endings and FakeDNS may have been switched off.
        appliedFormState = ReadForm();
        SetPendingChanges(false);
    }

    private ConnectionFormState ReadForm() => new ConnectionFormState
    {
        ProfileName = currentProfileName,
        ServerEndpoint = ServerEndpointBox.Text,
        NetworkInterfaceGuid = selectedInterfaceGuid,
        KcpMode = SelectedKcpMode(),
        LocalTcpFlags = LocalFlagsBox.Text,
        RemoteTcpFlags = RemoteFlagsBox.Text,
        Advanced = advancedOptions,
        RouteAllApplications = RoutingModeButtons.SelectedIndex != 1,
        SelectedApplications = SelectedApplicationsBox.Text,
        UserExclusions = UserExclusionsBox.Text,
        DirectRouteDestinations = DirectRouteDestinationsBox.Text,
        BypassLan = BypassLanSwitch.IsOn,
        RegionalPreset = RegionalPresetBox.SelectedIndex == 1 ? RegionalRoutingPreset.None : RegionalRoutingPreset.IranDirect,
        DomainStrategy = DomainStrategyBox.SelectedIndex switch
        {
            0 => XrayDomainStrategy.AsIs,
            2 => XrayDomainStrategy.IPOnDemand,
            _ => XrayDomainStrategy.IPIfNonMatch,
        },
        DnsResolver = SelectedDnsResolver(),
        FakeDns = FakeDnsSwitch.IsOn,
        BlockAds = BlockAdsSwitch.IsOn,
        BlockMalware = BlockMalwareSwitch.IsOn,
        BlockQuic = BlockQuicSwitch.IsOn,
        DirectBitTorrent = DirectBitTorrentSwitch.IsOn,
        ExitProxy = SelectedExitProxy(),
        PsiphonRegion = SelectedPsiphonRegion(),
        OpenVpnUsername = OpenVpnUsernameBox.Text.Trim(),
        KillSwitch = KillSwitchToggle.IsOn,
        ShareWithLan = ShareWithLanSwitch.IsOn,
        LanSocksPort = double.IsNaN(LanSharePortBox.Value) ? 0 : (int)LanSharePortBox.Value,
        LanSocksUsername = ShareUsernameBox.Text,
        ShareViaHotspot = ShareViaHotspotSwitch.IsOn,
        HotspotSocksPort = double.IsNaN(HotspotPortBox.Value) ? 10808 : (int)HotspotPortBox.Value,
        RouteTcp = RouteTcpCheckBox.IsChecked == true,
        RouteUdp = RouteUdpCheckBox.IsChecked == true,
        RouteIpv4 = RouteIpv4CheckBox.IsChecked == true,
        RouteIpv6 = RouteIpv6CheckBox.IsChecked == true,
    }.Normalize();

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
                case ComboBox box when box != NetworkInterfaceBox:
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

        UserExclusionsBox.TextChanged += (_, _) => UpdateFakeDnsCompatibility();
        DnsResolverBox.SelectionChanged += (_, _) => UpdateFakeDnsCompatibility();
        RouteIpv4CheckBox.Checked += (_, _) => UpdateFakeDnsCompatibility();
        RouteIpv4CheckBox.Unchecked += (_, _) => UpdateFakeDnsCompatibility();
    }

    /// <summary>
    /// Pending changes are a comparison with the last applied form, so programmatic updates
    /// (including text-changed events WinUI raises after population) never mark it dirty.
    /// </summary>
    private void ConfigurationEdited()
    {
        if (!trackingConfigurationChanges || IsPopulatingForm || configurationSaveInProgress || appliedFormState is null)
        {
            return;
        }

        var wasPending = hasPendingConfigurationChanges;
        var pending = !ReadForm().IsEquivalentTo(appliedFormState) ||
            TransportKeyBox.Password.Length > 0 ||
            HasPendingOpenVpnSecrets ||
            (SharePasswordBox.Password.Length > 0 &&
             !string.Equals(SharePasswordBox.Password, sessionSharePassword, StringComparison.Ordinal));
        SetPendingChanges(pending);
        if (pending && !wasPending)
        {
            ConnectionInfoBar.IsOpen = RoutingInfoBar.IsOpen = false;
        }

        if (pending)
        {
            ClearFieldErrors();
        }
    }

    private void SetPendingChanges(bool pending)
    {
        hasPendingConfigurationChanges = pending;
        ConnectionPendingText.Text = RoutingPendingText.Text = pending
            ? "Pending changes — save to apply the connection profile and routing policy."
            : "No pending changes.";
    }

    /// <summary>Switches FakeDNS off with an explanation instead of letting the broker reject the save.</summary>
    private void UpdateFakeDnsCompatibility()
    {
        if (FakeDnsSwitch is null || FakeDnsHintText is null)
        {
            return;
        }

        var conflict = FakeDnsCompatibility.GetConflict(
            RoutingModeButtons.SelectedIndex != 1,
            ConnectionFormState.SplitLines(UserExclusionsBox.Text).Count > 0,
            SelectedDnsResolver(),
            RouteIpv4CheckBox.IsChecked == true);
        if (conflict is null)
        {
            FakeDnsSwitch.IsEnabled = configurationEditingEnabled;
            FakeDnsHintText.Visibility = Visibility.Collapsed;
            return;
        }

        if (FakeDnsSwitch.IsOn)
        {
            FakeDnsSwitch.IsOn = false;
            if (!IsPopulatingForm)
            {
                AddActivity("FakeDNS was turned off because it cannot be combined with the current routing choices.");
            }
        }

        FakeDnsSwitch.IsEnabled = false;
        FakeDnsHintText.Text = conflict;
        FakeDnsHintText.Visibility = Visibility.Visible;
    }

    private string SelectedKcpMode() =>
        (KcpModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "fast";

    private void SelectKcpMode(string mode)
    {
        var item = KcpModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, mode, StringComparison.OrdinalIgnoreCase));
        KcpModeBox.SelectedItem = item ?? KcpModeBox.Items.OfType<ComboBoxItem>()
            .First(candidate => string.Equals(candidate.Tag as string, "fast", StringComparison.Ordinal));
    }

    private XrayDnsResolver SelectedDnsResolver() => DnsResolverBox.SelectedIndex switch
    {
        1 => XrayDnsResolver.Google,
        2 => XrayDnsResolver.Quad9,
        3 => XrayDnsResolver.System,
        _ => XrayDnsResolver.Cloudflare,
    };

    private void SelectDnsResolver(XrayDnsResolver resolver) => DnsResolverBox.SelectedIndex = resolver switch
    {
        XrayDnsResolver.Google => 1,
        XrayDnsResolver.Quad9 => 2,
        XrayDnsResolver.System => 3,
        _ => 0,
    };

    private IEnumerable<Control> ConfigurationControls() =>
        new Control[] { ServerEndpointBox, TransportKeyBox, KcpModeBox, NetworkInterfaceBox, DetectAdapterButton,
            LocalFlagsBox, RemoteFlagsBox, RoutingModeButtons, SelectedApplicationsBox,
            UserExclusionsBox, BypassLanSwitch, RegionalPresetBox, DomainStrategyBox, DnsResolverBox, FakeDnsSwitch,
            DirectRouteDestinationsBox,
            BlockAdsSwitch, BlockMalwareSwitch, BlockQuicSwitch, DirectBitTorrentSwitch,
            ExitProxyBox, PsiphonRegionBox, OpenVpnImportButton, OpenVpnUsernameBox, OpenVpnPasswordBox, KillSwitchToggle,
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
        configurationEditingEnabled = enabled;
        foreach (var control in ConfigurationControls()) control.IsEnabled = enabled;
        AdvancedTransportExpander.IsEnabled = enabled;
        UpdateFakeDnsCompatibility();
    }

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
        AdvancedTransportExpander.IsExpanded |= errors.Any(IsAdvancedOptionError);
        Control? first = null;
        foreach (var message in errors)
        {
            var control = message switch
            {
                var m when m.Contains("profile name") => (Control)ProfilePicker,
                var m when m.Contains("Psiphon exit country") => PsiphonRegionBox,
                var m when m.Contains("OpenVPN username") => OpenVpnUsernameBox,
                var m when m.Contains("OpenVPN password") => OpenVpnPasswordBox,
                var m when m.Contains("OpenVPN configuration") || m.Contains("OpenVPN exit") => OpenVpnImportButton,
                var m when m.Contains("exit proxy") || m.Contains("Psiphon exit") => ExitProxyBox,
                var m when m.Contains("server must") => ServerEndpointBox,
                var m when m.Contains("transport key") => TransportKeyBox,
                var m when m.Contains("KCP mode") => KcpModeBox,
                var m when m.Contains("local TCP") => LocalFlagsBox,
                var m when m.Contains("remote TCP") => RemoteFlagsBox,
                var m when IsAdvancedOptionError(m) => AdvancedTransportExpander,
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
                var m when m.Contains("DNS resolver") => DnsResolverBox,
                var m when m.Contains("FakeDNS") => FakeDnsSwitch,
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
        NavigateTo(new Control[] { ProfilePicker, ServerEndpointBox, TransportKeyBox, KcpModeBox, LocalFlagsBox, RemoteFlagsBox, AdvancedTransportExpander }.Contains(first) ? "connection" : "routing");
        for (DependencyObject? ancestor = first; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is Expander expander) expander.IsExpanded = true;
        var invalidControl = first;
        DispatcherQueue.TryEnqueue(() =>
        {
            invalidControl.Focus(FocusState.Programmatic);
            invalidControl.StartBringIntoView();
        });
    }

    private bool TryCreateBrokerSettings(out PaqetFireSettings settings, out string error)
    {
        settings = ReadForm().ToSettings(
            TransportKeyBox.Password,
            SharePasswordBox.Password,
            pendingOpenVpnConfiguration ?? string.Empty,
            OpenVpnPasswordBox.Password);

        if (advancedNumberInputs.Any(control => !double.IsNaN(control.Value) &&
            (!double.IsFinite(control.Value) || control.Value != Math.Truncate(control.Value) || control.Value < int.MinValue || control.Value > int.MaxValue)))
        {
            error = "Advanced Paqet number fields require whole numbers within their supported ranges.";
            return false;
        }
        var validationCandidate = settings;
        if (hasSavedTransportKey && validationCandidate.TransportKey.Length == 0)
        {
            validationCandidate = validationCandidate with { TransportKey = "saved-by-broker" };
        }

        if (hasSavedLanSocksPassword && validationCandidate.LanSocksPassword.Length == 0)
        {
            validationCandidate = validationCandidate with { LanSocksPassword = "saved-by-broker" };
        }
        var errors = PaqetFireSettingsValidator.Validate(validationCandidate)
            .Where(message => !IsSatisfiedBySavedOpenVpn(message, validationCandidate.OpenVpn))
            .ToList();
        ShowFieldErrors(errors);
        error = string.Join(" ", errors);
        return errors.Count == 0;
    }

    /// <summary>Applies saved desktop preferences: app-behavior switches and the cached form.</summary>
    private void ApplyPreferences(DesktopPreferences source)
    {
        SocksEndpointBox.Text = source.LocalSocksEndpoint;
        PopulateForm(ConnectionFormState.FromPreferences(source));
        ApplyAppSettings(source);
        _ = RefreshCurrentNetworkAsync();
        if (trayIcon is not null)
        {
            trayIcon.Visible = source.MinimizeToTray || startedInBackground;
        }
    }

    /// <summary>Applies the broker's saved profile once per load or profile switch.</summary>
    private void PopulateFormFromBroker(PaqetFireSettingsView settings, Guid? profileId)
    {
        formProfileId = profileId;
        brokerSettingsApplied = true;
        TransportKeyBox.Password = string.Empty;
        SharePasswordBox.Password = string.Empty;
        hasSavedTransportKey = settings.HasTransportKey;
        hasSavedLanSocksPassword = settings.HasLanSocksPassword;
        ApplySavedOpenVpn(settings.OpenVpn ?? new());
        PopulateForm(ConnectionFormState.FromSettings(settings));
        TransportKeyBox.PlaceholderText = hasSavedTransportKey
            ? "Saved securely · leave blank to keep it"
            : "Enter the shared Paqet key";
        SharePasswordBox.PlaceholderText = hasSavedLanSocksPassword
            ? "Saved securely · leave blank to keep it"
            : "At least 8 characters";
        UpdateHotspotUri();
    }

    private void CapturePreferences()
    {
        var state = ReadForm();
        state.ApplyTo(preferences);
        preferences.LocalSocksEndpoint = SocksEndpointBox.Text.Trim();
        preferences.LanSocksPort = state.LanSocksPort == 0 ? 1082 : state.LanSocksPort;
        CaptureAppSettings();
    }

    private void MarkSecretsSaved(PaqetFireSettings settings)
    {
        hasSavedTransportKey = true;
        TransportKeyBox.Password = string.Empty;
        TransportKeyBox.PlaceholderText = "Saved securely · leave blank to keep it";

        if (settings.ShareWithLan || settings.ShareViaHotspot)
        {
            // Keep the sharing password for this session so the hotspot URI and QR code stay
            // available after the box is cleared; it is never written to disk by the app.
            if (settings.LanSocksPassword.Length > 0)
            {
                sessionSharePassword = settings.LanSocksPassword;
            }

            hasSavedLanSocksPassword = true;
            SharePasswordBox.Password = string.Empty;
            SharePasswordBox.PlaceholderText = "Saved securely · leave blank to keep it";
        }

        MarkOpenVpnSecretsSaved(settings.OpenVpn);
        appliedFormState = ReadForm();
        UpdateLanShareEndpointText();
        UpdateHotspotUri();
    }

    private static string ToMultiline(string normalized) =>
        normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
}
