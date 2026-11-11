using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PaqetFire.Desktop;

/// <summary>Saved profiles: switching (Overview, Connection page, tray), create, import, export.</summary>
public sealed partial class MainWindow
{
    private async void ProfilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingProfileSelection || ProfilePicker.SelectedItem is not ProfileDisplayItem selected || selected.IsActive)
        {
            return;
        }

        await SwitchProfileAsync(selected, ConnectionInfoBar);
    }

    private async void OverviewProfilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingProfileSelection || OverviewProfilePicker.SelectedItem is not ProfileDisplayItem selected || selected.IsActive)
        {
            return;
        }

        await SwitchProfileAsync(selected, OverviewInfoBar);
    }

    private async Task SwitchProfileAsync(ProfileDisplayItem selected, InfoBar feedback)
    {
        if (selected.IsActive)
        {
            return;
        }

        var restartsRoute = ProfileSwitch.RestartsRoute(ViewModel.ConnectionState);
        if (hasPendingConfigurationChanges || restartsRoute)
        {
            if (!AppWindow.IsVisible)
            {
                RestoreFromTray();
            }

            if (!await ConfirmProfileActionAsync(
                    ProfileSwitch.SwitchTitle(selected.Name),
                    ProfileSwitch.SwitchMessage(restartsRoute, hasPendingConfigurationChanges),
                    ProfileSwitch.SwitchAction(restartsRoute, hasPendingConfigurationChanges),
                    hasPendingConfigurationChanges ? "Keep editing" : "Cancel"))
            {
                SelectActiveProfile();
                return;
            }
        }

        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Activate, selected.Id),
            reloadSettings: true,
            $"Switched to profile '{selected.Name}'.",
            feedback);
    }

    private async void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (Content?.XamlRoot is null) return;
        // Decided before the dialog: the broker applies the same rule when it creates the profile.
        var becomesActive = ProfileCreation.BecomesActive(lastSnapshot);
        var activeName = ProfileEntries.FirstOrDefault(profile => profile.IsActive)?.Name;
        var nameBox = new TextBox
        {
            Header = "Profile name",
            PlaceholderText = "Office, Travel, Backup server…",
            MaxLength = 80,
        };
        var dialog = new ContentDialog
        {
            Title = "New profile",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    nameBox,
                    new TextBlock
                    {
                        Text = ProfileCreation.DialogHint(becomesActive, hasPendingConfigurationChanges, activeName),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72,
                    },
                },
            },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        dialog.IsPrimaryButtonEnabled = false;
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        var existing = ProfileEntries.Select(profile => profile.Id).ToHashSet();
        // While the route is up the active profile does not change, so unsaved edits to it stay.
        if (!await RunProfileActionAsync(
                new ProfileAction(ProfileActionKind.Create, Name: name),
                reloadSettings: becomesActive,
                $"Created profile '{name}'.",
                ConnectionInfoBar))
        {
            return;
        }

        var created = ProfileEntries.FirstOrDefault(profile => !existing.Contains(profile.Id));
        if (created is { IsActive: false })
        {
            // Activating an empty profile would restart a running route with it. Only an
            // idle route lets the new profile become the one being edited.
            if (!ProfileCreation.BecomesActive(lastSnapshot))
            {
                ShowInfo(ConnectionInfoBar, InfoBarSeverity.Informational, "Profile added",
                    ProfileCreation.AddedWhileConnected(name, activeName));
                return;
            }

            if (!await RunProfileActionAsync(
                    new ProfileAction(ProfileActionKind.Activate, created.Id),
                    reloadSettings: true,
                    $"Created profile '{name}' and switched to it.",
                    ConnectionInfoBar))
            {
                return;
            }
        }

        FocusServerEndpoint();
    }

    private async void DuplicateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not ProfileDisplayItem selected) return;
        // The copy becomes the active profile, which restarts a live route.
        var restartsRoute = ProfileSwitch.RestartsRoute(ViewModel.ConnectionState);
        if ((restartsRoute || hasPendingConfigurationChanges) &&
            !await ConfirmProfileActionAsync(
                ProfileSwitch.DuplicateTitle(selected.Name),
                ProfileSwitch.DuplicateMessage(restartsRoute, hasPendingConfigurationChanges),
                ProfileSwitch.DuplicateAction(restartsRoute, hasPendingConfigurationChanges),
                hasPendingConfigurationChanges ? "Keep editing" : "Cancel"))
        {
            return;
        }

        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Duplicate, selected.Id),
            reloadSettings: true,
            $"Duplicated profile '{selected.Name}'.",
            ConnectionInfoBar);
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
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        var renamed = await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Rename, selected.Id, nameBox.Text),
            reloadSettings: selected.IsActive && !hasPendingConfigurationChanges,
            $"Renamed profile to '{nameBox.Text.Trim()}'.",
            ConnectionInfoBar);
        // A refused rename must not leak into the next save.
        if (renamed && selected.IsActive)
        {
            // Keep the name a later save sends in step with the catalog.
            currentProfileName = nameBox.Text.Trim();
            if (appliedFormState is not null)
            {
                appliedFormState = appliedFormState with { ProfileName = currentProfileName };
            }
        }
    }

    private async void MakeDefaultProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not ProfileDisplayItem selected) return;
        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.MakeDefault, selected.Id),
            reloadSettings: false,
            $"'{selected.Name}' is now the default profile.",
            ConnectionInfoBar);
    }

    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is not ProfileDisplayItem selected || Content?.XamlRoot is null) return;
        // Deleting the active profile activates another one, which restarts a live route.
        var restartsRoute = ProfileSwitch.RestartsRoute(ViewModel.ConnectionState);
        var next = ProfileSwitch.ActiveAfterDelete(ProfileEntries, selected.Id);
        var dialog = new ContentDialog
        {
            Title = $"Delete '{selected.Name}'?",
            Content = new TextBlock
            {
                Text = ProfileSwitch.DeleteMessage(selected.IsActive, restartsRoute, next),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = selected.IsActive && restartsRoute && next is not null ? "Delete and reconnect" : "Delete profile",
            IsPrimaryButtonEnabled = ProfileSwitch.CanDelete(selected.IsActive, restartsRoute, next),
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await RunProfileActionAsync(
            new ProfileAction(ProfileActionKind.Delete, selected.Id),
            reloadSettings: selected.IsActive,
            $"Deleted profile '{selected.Name}'.",
            ConnectionInfoBar);
    }

    private async void ExportProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var json = await ViewModel.ExportProfilesAsync();
        if (json is null)
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Error, "Profiles could not be exported",
                ViewModel.LastRequestWasBusy ? BusyMessage : ViewModel.ErrorMessage ?? "The broker did not provide an export.");
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
            "The export includes transport keys. Keep it private and share it only with trusted recipients. SOCKS credentials stay on this PC.");
        AddActivity("Portable profiles exported.");
    }

    private async void ImportProfilesFromFileMenuItem_Click(object sender, RoutedEventArgs e)
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
            ShowImportTooLarge();
            return;
        }

        await ImportProfilesAsync(await File.ReadAllTextAsync(dialog.FileName));
    }

    private async void ImportProfilesFromClipboardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        string? text = null;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                text = await content.GetTextAsync();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "Clipboard unavailable", exception.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Warning, "No profile export on the clipboard",
                "Copy the contents of a PaqetFire profile export (.json), then try again.");
            return;
        }

        if (Encoding.UTF8.GetByteCount(text) > ProfileCatalogJson.MaximumDocumentBytes)
        {
            ShowImportTooLarge();
            return;
        }

        await ImportProfilesAsync(text);
    }

    private void ShowImportTooLarge() =>
        ShowInfo(ConnectionInfoBar, InfoBarSeverity.Error, "Import is too large",
            $"Choose a PaqetFire profile export smaller than {ProfileCatalogJson.MaximumDocumentBytes / (1024 * 1024)} MB.");

    private async Task ImportProfilesAsync(string json)
    {
        if (Content?.XamlRoot is null) return;
        IReadOnlyList<string> importedNames;
        try
        {
            importedNames = ProfileCatalogJson.ReadImport(json).Profiles
                .Select(profile => profile.Settings.ProfileName)
                .ToList();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            ShowInfo(ConnectionInfoBar, InfoBarSeverity.Error, "Profiles could not be imported",
                "This is not a valid PaqetFire profile export. Choose an export (.json) made by PaqetFire.");
            return;
        }

        // The broker replaces the catalog only while the route is disconnected.
        var canReplace = ProfileCreation.BecomesActive(lastSnapshot);
        var choice = new ContentDialog
        {
            Title = "Import profiles",
            Content = new TextBlock
            {
                Text = ProfileImportText.ChoiceMessage(importedNames, canReplace),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Add to my profiles",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (canReplace)
        {
            choice.SecondaryButtonText = "Replace all profiles…";
        }

        var result = await ShowDialogAsync(choice);
        if (result is null or ContentDialogResult.None) return;
        if (result == ContentDialogResult.Secondary)
        {
            var confirm = new ContentDialog
            {
                Title = ProfileImportText.ReplaceTitle(ProfileEntries.Count),
                Content = new TextBlock
                {
                    Text = ProfileImportText.ReplaceMessage(importedNames.Count, hasPendingConfigurationChanges),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Replace all",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        }

        var merge = result == ContentDialogResult.Primary;
        await RunProfileActionAsync(
            new ProfileAction(merge ? ProfileActionKind.ImportMerge : ProfileActionKind.Import, Payload: json),
            reloadSettings: !merge,
            merge
                ? "Imported profiles were added. Select one with a missing key to complete it. Your SOCKS credentials are unchanged."
                : "Imported profiles replaced the saved ones. Keys included in the file are ready to use; select any profile with a missing key to complete it. Your SOCKS credentials are unchanged.",
            ConnectionInfoBar);
    }

    /// <remarks>
    /// Form state is left alone until the broker answers: snapshots that arrive meanwhile may
    /// still describe the previous profile, so population waits for the reply, and a refused
    /// action keeps the pending edits exactly as they were.
    /// </remarks>
    private async Task<bool> RunProfileActionAsync(ProfileAction action, bool reloadSettings, string successMessage, InfoBar feedback)
    {
        bool succeeded;
        profileActionDepth++;
        try
        {
            succeeded = await ViewModel.ManageProfileAsync(action);
        }
        finally
        {
            profileActionDepth--;
        }

        if (succeeded)
        {
            if (lastSnapshot is { Settings: { } settings } snapshot &&
                (reloadSettings || ActiveProfileId(snapshot) != formProfileId))
            {
                PopulateFormFromBroker(settings, ActiveProfileId(snapshot));
            }

            ShowInfo(feedback, InfoBarSeverity.Success, "Profiles updated", successMessage);
            AddActivity(successMessage);
            return true;
        }

        ShowInfo(feedback, InfoBarSeverity.Error, "Profiles could not be updated",
            ViewModel.LastRequestWasBusy ? BusyMessage : ViewModel.ErrorMessage ?? "The broker rejected the profile action.");
        SelectActiveProfile();
        return false;
    }

    private static Guid? ActiveProfileId(BrokerSnapshot? snapshot) =>
        snapshot?.ProfileCatalog?.Profiles.FirstOrDefault(profile => profile.IsActive)?.Id;

    /// <summary>
    /// WinUI allows one ContentDialog per window; a second ShowAsync throws. A request that
    /// arrives while one is open (a tray action, say) brings the window forward and is dropped.
    /// </summary>
    private async Task<ContentDialogResult?> ShowDialogAsync(ContentDialog dialog)
    {
        if (dialogOpen)
        {
            RestoreFromTray();
            return null;
        }

        dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        finally
        {
            dialogOpen = false;
        }
    }

    private void UpdateProfileEntries(ProfileCatalogView? catalog)
    {
        if (catalog is null) return;
        // Snapshots repeat (heartbeat, events); rebuilding would close an open drop-down.
        if (ProfileEntries.Count == catalog.Profiles.Count &&
            ProfileEntries.Zip(catalog.Profiles).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
                pair.First.IsActive == pair.Second.IsActive && pair.First.IsDefault == pair.Second.IsDefault))
        {
            return;
        }

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
            var active = ProfileEntries.FirstOrDefault(profile => profile.IsActive);
            ProfilePicker.SelectedItem = active;
            OverviewProfilePicker.SelectedItem = active;
        }
        finally
        {
            updatingProfileSelection = false;
        }
    }

    /// <summary>Asks before a profile action that restarts the route or discards edits; Cancel is the default.</summary>
    private async Task<bool> ConfirmProfileActionAsync(string title, string message, string primaryText, string closeText)
    {
        if (Content?.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private void SelectActiveProfile()
    {
        updatingProfileSelection = true;
        var active = ProfileEntries.FirstOrDefault(profile => profile.IsActive);
        ProfilePicker.SelectedItem = active;
        OverviewProfilePicker.SelectedItem = active;
        updatingProfileSelection = false;
    }

    private async void TestServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsConnected)
        {
            ServerTestResultText.Text = ServerReachabilityText.FormatTunnelLatency(ViewModel.Live.LatencyText);
            return;
        }

        TestServerButton.IsEnabled = false;
        ServerTestResultText.Text = "Pinging the server…";
        try
        {
            // Name lookups are only trustworthy while nothing captures this app's DNS.
            var routeIdle = lastSnapshot?.IsRouting != true &&
                ViewModel.ConnectionState is not (BrokerConnectionState.Connecting or BrokerConnectionState.Guarded or
                    BrokerConnectionState.Disconnecting or BrokerConnectionState.Connected);
            var result = await serverReachabilityProbe.CheckAsync(ServerEndpointBox.Text, allowNameResolution: routeIdle);
            ServerTestResultText.Text = ServerReachabilityText.Format(result);
        }
        catch (Exception exception) when (exception is System.Net.NetworkInformation.PingException or InvalidOperationException)
        {
            ServerTestResultText.Text = $"The ping could not run: {exception.Message}";
        }
        finally
        {
            TestServerButton.IsEnabled = true;
        }
    }

    private void FocusServerEndpoint()
    {
        NavigateTo("connection");
        DispatcherQueue.TryEnqueue(() =>
        {
            ServerEndpointBox.Focus(FocusState.Programmatic);
            ServerEndpointBox.StartBringIntoView();
        });
    }
}
