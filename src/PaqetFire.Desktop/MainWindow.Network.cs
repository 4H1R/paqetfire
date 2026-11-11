using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Connections.Automation;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Network;
using PaqetFire.Core.Profiles;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.Services;
using PaqetFire.Desktop.ViewModels;

namespace PaqetFire.Desktop;

/// <summary>
/// Adapter preview, network identity, and network automation. Adapter enumeration and ARP
/// run on worker threads; the UI only applies their results.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan NetworkChangeSettleDelay = TimeSpan.FromSeconds(1);

    private readonly GatewayMacCache gatewayMacCache = new();
    private NetworkAdapterPreview networkAdapterPreview = null!;
    private NetworkContextProbe networkContextProbe = null!;
    private int adapterRefreshVersion;
    private int networkObservationVersion;
    private CancellationTokenSource? networkChangeDebounce;
    private string currentNetworkDisplayName = "Current network";

    private enum AdapterRefreshMode
    {
        /// <summary>Profile load or network change: list quickly, resolve the router MAC quietly.</summary>
        Load,
        /// <summary>The user picked an interface.</summary>
        Selection,
        /// <summary>The user asked to detect again; bypass the cached MAC.</summary>
        Explicit,
    }

    private void InitializeNetworkServices()
    {
        networkAdapterPreview = new NetworkAdapterPreview(new NetworkAdapterDetector(), gatewayMacCache);
        networkContextProbe = new NetworkContextProbe(gatewayMacCache);
    }

    private void NetworkInterfaceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingNetworkInterfaceSelection || NetworkInterfaceBox.SelectedItem is not NetworkInterfaceOption option) return;
        selectedInterfaceGuid = option.InterfaceGuid;
        ConfigurationEdited();
        _ = RefreshNetworkAdapterAsync(option.InterfaceGuid, AdapterRefreshMode.Selection);
    }

    private void DetectAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshNetworkAdapterAsync(selectedInterfaceGuid, AdapterRefreshMode.Explicit);
        if (ShareViaHotspotSwitch?.IsOn == true)
            _ = DetectHotspotAsync(notifyOnSuccess: false, notifyOnFailure: false);
    }

    private async Task RefreshNetworkAdapterAsync(Guid? selected, AdapterRefreshMode mode)
    {
        var version = ++adapterRefreshVersion;
        if (mode == AdapterRefreshMode.Load)
        {
            // Without ARP: fills the picker and the local address (used by the LAN share test) at once.
            var listed = await RunAdapterPreviewAsync(selected, resolveRouterMac: false, refreshRouterMac: false);
            if (version != adapterRefreshVersion) return;
            ApplyAdapterPreview(listed.State, listed.Failure, notify: false);
        }
        else
        {
            RouterMacBox.PlaceholderText = "Resolving…";
        }

        var detected = await RunAdapterPreviewAsync(selected, resolveRouterMac: true, mode == AdapterRefreshMode.Explicit);
        if (version != adapterRefreshVersion) return;
        RouterMacBox.PlaceholderText = "Resolved when the broker saves";
        ApplyAdapterPreview(detected.State, detected.Failure, notify: mode != AdapterRefreshMode.Load);
    }

    private Task<(NetworkAdapterPreviewState State, Exception? Failure)> RunAdapterPreviewAsync(
        Guid? selected,
        bool resolveRouterMac,
        bool refreshRouterMac) => Task.Run(() =>
    {
        try
        {
            return (networkAdapterPreview.Refresh(selected, resolveRouterMac, refreshRouterMac), (Exception?)null);
        }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException or InvalidOperationException)
        {
            return (new NetworkAdapterPreviewState(selected, networkAdapterPreview.Options, null, []), exception);
        }
    });

    private void ApplyAdapterPreview(NetworkAdapterPreviewState state, Exception? failure, bool notify)
    {
        if (failure is not null && notify)
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "Detection was incomplete", failure.Message);
        }
        else if (notify && state.Detected is { } detected)
        {
            var hasMac = !string.IsNullOrWhiteSpace(detected.RouterMac);
            ShowInfo(ConnectionInfoBar, hasMac ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                hasMac ? "Network detected" : "Router MAC not detected",
                hasMac ? "Adapter and router details were detected. Save the profile to apply your interface choice."
                    : "Adapter details were detected, but its router MAC could not be resolved. Check the connection and retry. Saving the profile will retry detection.");
            AddActivity($"Detected adapter '{detected.Adapter.InterfaceName}'.");
        }

        updatingNetworkInterfaceSelection = true;
        try
        {
            NetworkInterfaceBox.ItemsSource = state.Options;
            NetworkInterfaceBox.SelectedItem = state.Options
                .FirstOrDefault(option => option.InterfaceGuid == state.SelectedInterfaceGuid);
        }
        finally
        {
            updatingNetworkInterfaceSelection = false;
        }

        // Before the MAC is resolved, still show the adapter the detector would pick.
        var adapter = state.Detected?.Adapter ?? (state.SelectedInterfaceGuid is { } selected
            ? state.Adapters.FirstOrDefault(candidate => candidate.InterfaceGuid == selected)
            : state.Adapters.FirstOrDefault());
        InterfaceNameBox.Text = adapter?.InterfaceName ?? string.Empty;
        InterfaceGuidBox.Text = adapter?.InterfaceGuid.ToString("D") ?? string.Empty;
        LocalIpv4Box.Text = adapter?.LocalAddress.ToString() ?? string.Empty;
        RouterMacBox.Text = state.Detected?.RouterMac ?? string.Empty;
        UpdateLanShareEndpointText();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Windows raises bursts of these while an adapter settles; act once it is quiet.
        DispatcherQueue.TryEnqueue(() =>
        {
            networkChangeDebounce?.Cancel();
            networkChangeDebounce?.Dispose();
            networkChangeDebounce = new CancellationTokenSource();
            _ = HandleNetworkChangeAsync(networkChangeDebounce.Token);
        });
    }

    private async Task HandleNetworkChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(NetworkChangeSettleDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (exitRequested)
        {
            return;
        }

        unidentifiedNetworkNonce = Guid.NewGuid().ToString("N");
        if (ShareViaHotspotSwitch?.IsOn == true)
        {
            _ = DetectHotspotAsync(notifyOnSuccess: false, notifyOnFailure: false);
        }

        _ = RefreshNetworkAdapterAsync(selectedInterfaceGuid, AdapterRefreshMode.Load);
        ScheduleNetworkAutomation(NetworkAutomationTrigger.NetworkChanged, TimeSpan.FromMilliseconds(50));
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
        var observation = await ObserveNetworkAsync();
        var network = observation?.Context;
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
            DisplayName = currentNetworkDisplayName,
            IsTrusted = isTrusted,
            ProfileId = activeProfile?.Id,
        });
        if (!await TrySavePreferencesAsync(SettingsInfoBar)) return;
        RenderCurrentNetworkText();
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
        if (factoryResetInProgress) return;
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
        if (factoryResetInProgress)
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
            var observation = await ObserveNetworkAsync();
            if (lastSnapshot is null || observation is null)
            {
                return;
            }

            var network = observation.Context;
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

            // Only ever connect from here. Recovering a faulted or degraded route belongs to the
            // broker; a desktop Disconnect would also clear its persisted "keep connected" intent.
            if (decision.Action == NetworkAutomationAction.Connect)
            {
                if (decision.ProfileId is { } profileId &&
                    ProfileEntries.FirstOrDefault(profile => profile.IsActive)?.Id != profileId)
                {
                    brokerSettingsApplied = false;
                    if (!await ViewModel.ManageProfileAsync(new ProfileAction(ProfileActionKind.Activate, profileId)))
                    {
                        brokerSettingsApplied = true;
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

    private static bool HasAutomationRelevantStateChanged(BrokerSnapshot previous, BrokerSnapshot current) =>
        previous.ConnectionState != current.ConnectionState ||
        previous.IsKillSwitchEnabled != current.IsKillSwitchEnabled ||
        previous.Engines.Count != current.Engines.Count ||
        previous.Engines.Any(oldEngine =>
            current.Engines.FirstOrDefault(candidate => candidate.Engine == oldEngine.Engine)?.State != oldEngine.State);

    /// <summary>Identifies the current network off the UI thread and updates the Settings summary.</summary>
    private async Task<NetworkObservation?> ObserveNetworkAsync()
    {
        var version = ++networkObservationVersion;
        var nonce = unidentifiedNetworkNonce;
        NetworkObservation? observation;
        try
        {
            observation = await Task.Run(() => networkContextProbe.Observe(nonce));
        }
        catch (NetworkInformationException)
        {
            observation = null;
        }

        if (version == networkObservationVersion)
        {
            currentNetworkContext = observation?.Context;
            currentNetworkDisplayName = observation?.DisplayName ?? "Current network";
            RenderCurrentNetworkText();
        }

        return observation;
    }

    private Task RefreshCurrentNetworkAsync() => ObserveNetworkAsync();

    private void RenderCurrentNetworkText()
    {
        if (CurrentNetworkText is null) return;
        if (currentNetworkContext is null)
        {
            CurrentNetworkText.Text = "No upstream Ethernet or Wi-Fi network is currently available.";
            UpdateLanShareStatus();
            return;
        }

        var rule = preferences.NetworkRules.FirstOrDefault(candidate =>
            string.Equals(candidate.NetworkId, currentNetworkContext.NetworkId, StringComparison.Ordinal));
        var policy = rule is null ? "unknown" : rule.IsTrusted ? "trusted" : "route required";
        CurrentNetworkText.Text = $"Current: {currentNetworkDisplayName} · {currentNetworkContext.Kind} · {currentNetworkContext.Connectivity} · {policy}.";
        UpdateLanShareStatus();
    }
}
