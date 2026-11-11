using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Ipc;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.Services;
using PaqetFire.Desktop.ViewModels;

namespace PaqetFire.Desktop;

/// <summary>Required software and the first-run checklist.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan PrerequisiteRecheckInterval = TimeSpan.FromSeconds(3);

    private DateTimeOffset lastPrerequisiteRecheck = DateTimeOffset.MinValue;
    private CancellationTokenSource? prerequisiteCancellation;
    private readonly List<string> restartPendingPrerequisites = [];

    private async void PrerequisiteActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await InstallPrerequisiteAsync(id, PrerequisitesInfoBar);
        }
    }

    private async void InstallAllMissingButton_Click(object sender, RoutedEventArgs e)
    {
        var missing = prerequisites.Values.Where(item => !item.IsInstalled).Select(item => item.Id).ToArray();
        if (missing.Length == 0 || prerequisiteActionInProgress)
        {
            return;
        }

        // Automatic installers first, one at a time (each shows its own permission prompt);
        // download pages last so they do not hide an installer that is waiting.
        foreach (var id in missing.OrderBy(id => PrerequisiteInstallerService.CanInstallAutomatically(id) ? 0 : 1))
        {
            if (!await InstallPrerequisiteAsync(id, OverviewInfoBar))
            {
                break;
            }
        }
    }

    /// <returns>False when the user cancelled or an install failed, so a batch should stop.</returns>
    private async Task<bool> InstallPrerequisiteAsync(string id, InfoBar feedback)
    {
        if (prerequisiteActionInProgress || !prerequisites.TryGetValue(id, out var prerequisite) || prerequisite.IsInstalled)
        {
            return true;
        }

        PrerequisitesInfoBar.ActionButton = null;

        if (!PrerequisiteInstallerService.CanInstallAutomatically(id))
        {
            if (prerequisite.HelpUri is null)
            {
                ShowInfo(feedback, InfoBarSeverity.Warning, "Download unavailable", "No official download page was reported for this component.");
                return true;
            }

            try
            {
                PrerequisiteInstallerService.OpenOfficialPage(prerequisite.HelpUri);
                ShowInfo(
                    feedback,
                    InfoBarSeverity.Informational,
                    $"Install {prerequisite.DisplayName}",
                    id == "npcap"
                        ? "The official Npcap page is open. Install Npcap with \"WinPcap API-compatible mode\" enabled, then return here; PaqetFire checks again automatically."
                        : "The official Microsoft download page is open. Finish the installer, then return here; PaqetFire checks again automatically.");
                AddActivity($"Opened the official {prerequisite.DisplayName} download page.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                ShowInfo(feedback, InfoBarSeverity.Error, "Could not open the download page",
                    $"Windows could not open your browser. Visit {prerequisite.HelpUri.AbsoluteUri} to download {prerequisite.DisplayName}.");
                AddActivity($"Could not open the {prerequisite.DisplayName} download page: {exception.Message}", "ERROR");
                return false;
            }

            return true;
        }

        prerequisiteActionInProgress = true;
        UpdatePrerequisiteCards();
        using var cancellation = new CancellationTokenSource();
        prerequisiteCancellation = cancellation;
        try
        {
            string? shownMessage = null;
            var progress = new Progress<PrerequisiteInstallProgress>(update =>
            {
                ShowPrerequisiteProgress(update);
                // Announce each step once; percentages only move the bar and the text.
                if (update.Message != shownMessage)
                {
                    shownMessage = update.Message;
                    ShowInfo(feedback, InfoBarSeverity.Informational, "Installing required software", update.Message);
                }
                else if (feedback != PrerequisitesInfoBar && update.Percentage is double percentage)
                {
                    feedback.Message = $"{update.Message} {percentage:0}%";
                }
            });
            var result = await prerequisiteInstaller.InstallAsync(id, progress, cancellation.Token);
            if (result.RestartRequired)
            {
                ShowRestartNote(prerequisite.DisplayName);
            }

            ShowInfo(
                feedback,
                result.RestartRequired ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                result.RestartRequired ? "Installation complete — restart required" : "Installation complete",
                result.RestartRequired
                    ? "Restart Windows before connecting with PaqetFire."
                    : "PaqetFire is checking the component again now.");
            AddActivity($"Installed {prerequisite.DisplayName}.");
            return true;
        }
        catch (OperationCanceledException exception)
        {
            ShowInfo(feedback, InfoBarSeverity.Warning, "Installation cancelled",
                cancellation.IsCancellationRequested ? "The download was cancelled. Nothing was installed." : exception.Message);
            AddActivity($"Installation cancelled for {prerequisite.DisplayName}.");
            return false;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowPrerequisiteFailure(feedback, prerequisite);
            AddActivity($"Could not install {prerequisite.DisplayName}: {exception.Message}", "ERROR");
            return false;
        }
        finally
        {
            prerequisiteCancellation = null;
            PrerequisiteProgressPanel.Visibility = Visibility.Collapsed;
            prerequisiteActionInProgress = false;
            UpdatePrerequisiteCards();
            // Re-check whenever an installer process has exited, whatever its result.
            lastPrerequisiteRecheck = DateTimeOffset.UtcNow;
            await ViewModel.RefreshAsync();
        }
    }

    /// <summary>Re-checks required software when the user comes back from an installer or download page.</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || !initialized ||
            prerequisiteActionInProgress || ViewModel.IsBusy ||
            !prerequisites.Values.Any(item => !item.IsInstalled) ||
            DateTimeOffset.UtcNow - lastPrerequisiteRecheck < PrerequisiteRecheckInterval)
        {
            return;
        }

        lastPrerequisiteRecheck = DateTimeOffset.UtcNow;
        _ = ViewModel.RefreshAsync();
    }

    private void ShowPrerequisiteProgress(PrerequisiteInstallProgress update)
    {
        PrerequisiteProgressPanel.Visibility = Visibility.Visible;
        PrerequisiteProgressBar.IsIndeterminate = update.Percentage is null;
        PrerequisiteProgressBar.Value = update.Percentage ?? 0;
        PrerequisiteProgressText.Text = update.Percentage is double percentage
            ? $"{update.Message} {percentage:0}%"
            : update.Message;
        CancelPrerequisiteButton.Visibility = update.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelPrerequisiteButton.IsEnabled = update.CanCancel && prerequisiteCancellation?.IsCancellationRequested == false;
    }

    private void CancelPrerequisiteButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPrerequisiteButton.IsEnabled = false;
        prerequisiteCancellation?.Cancel();
    }

    /// <summary>A short, plain failure with the official page as the way forward; the raw error goes to the activity log.</summary>
    private void ShowPrerequisiteFailure(InfoBar feedback, PrerequisiteStatus prerequisite)
    {
        const string Title = "Installation failed";
        var message = $"{prerequisite.DisplayName} could not be downloaded or installed. Check your internet connection and try again, or install it from the official download page.";
        ShowInfo(feedback, InfoBarSeverity.Error, Title, message);
        if (feedback != PrerequisitesInfoBar)
        {
            ShowInfo(PrerequisitesInfoBar, InfoBarSeverity.Error, Title, message);
        }

        // Only this card's InfoBar gets the link: every other message on it comes from this file, which clears it.
        if (prerequisite.HelpUri is { } helpUri)
        {
            PrerequisitesInfoBar.ActionButton = new HyperlinkButton { Content = "Open download page", NavigateUri = helpUri };
        }
    }

    /// <summary>Stays on the card until PaqetFire restarts, unlike an InfoBar that can be closed and forgotten.</summary>
    private void ShowRestartNote(string displayName)
    {
        if (!restartPendingPrerequisites.Contains(displayName))
        {
            restartPendingPrerequisites.Add(displayName);
        }

        PrerequisiteRestartText.Text =
            $"Restart Windows to finish installing {string.Join(" and ", restartPendingPrerequisites)}. PaqetFire cannot connect until you do.";
        PrerequisiteRestartNote.Visibility = Visibility.Visible;
    }

    private async void RestartWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Content?.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restart Windows now?",
            Content = "Save your work in other apps first. Windows restarts right away and the route disconnects.",
            PrimaryButtonText = "Restart now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe",
                ArgumentList = { "/r", "/t", "0" },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            AddActivity("Windows restart requested to finish installing required software.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ShowInfo(PrerequisitesInfoBar, InfoBarSeverity.Error, "Windows did not restart", "Restart from the Start menu instead.");
            AddActivity($"Windows restart could not be started: {exception.Message}", "ERROR");
        }
    }

    /// <summary>Until everything is installed, the Required software card comes first on Diagnostics.</summary>
    private void PlaceRequiredSoftwareCard(bool first)
    {
        var children = DiagnosticsContent.Children;
        var current = children.IndexOf(RequiredSoftwareCard);
        var placed = first
            ? children.IndexOf(DiagnosticsEngineCards) == current + 1
            : children.IndexOf(VerificationCard) == current - 1;
        if (current < 0 || placed)
        {
            return;
        }

        children.RemoveAt(current);
        children.Insert(
            first ? children.IndexOf(DiagnosticsEngineCards) : children.IndexOf(VerificationCard) + 1,
            RequiredSoftwareCard);
    }

    private void UpdatePrerequisites(BrokerSnapshot snapshot, BrokerSnapshot? previousSnapshot)
    {
        var hadMissing = prerequisites.Values.Any(item => !item.IsInstalled);
        prerequisites.Clear();
        if (snapshot.Prerequisites is { Count: > 0 } reported)
        {
            foreach (var prerequisite in reported)
            {
                prerequisites[prerequisite.Id] = prerequisite;
            }

            var missing = reported.Count(item => !item.IsInstalled);
            PrerequisiteSummaryText.Text = missing == 0
                ? "Prerequisites · Ready"
                : $"Prerequisites · {missing} missing";
        }
        else
        {
            PrerequisiteSummaryText.Text = "Prerequisites · No report";
        }

        UpdatePrerequisiteCards();
        PlaceRequiredSoftwareCard(first: prerequisites.Values.Any(item => !item.IsInstalled));
        UpdateSetupChecklist(snapshot);

        // Everything is installed now: the next step is the server profile.
        if (hadMissing && previousSnapshot is not null && snapshot.Prerequisites is { Count: > 0 } &&
            !prerequisites.Values.Any(item => !item.IsInstalled) && !snapshot.IsConfigured && AppWindow.IsVisible)
        {
            ShowInfo(OverviewInfoBar, InfoBarSeverity.Success, "Required software is ready",
                "Next, enter your Paqet server endpoint and transport key.");
            FocusServerEndpoint();
        }
    }

    private void UpdateSetupChecklist(BrokerSnapshot snapshot)
    {
        var checklist = SetupChecklist.Create(snapshot, ViewModel.IsConnected);
        SetupChecklistCard.Visibility = checklist.ShouldShow ? Visibility.Visible : Visibility.Collapsed;

        // During first-run setup the checklist is the call to action: hide the status card that
        // would repeat it with an empty profile picker, and the engine cards that only say Stopped.
        // An error, or a broken install the checklist does not cover, keeps the status card.
        var checklistOwnsSetup = checklist.ShouldShow && !ViewModel.HasError &&
            ViewModel.Readiness is ReadinessIssue.ProfileRequired or ReadinessIssue.PrerequisiteMissing;
        StatusCard.Visibility = checklistOwnsSetup ? Visibility.Collapsed : Visibility.Visible;
        EngineCardsGrid.Visibility = checklist.ShouldShow ? Visibility.Collapsed : Visibility.Visible;
        // One accented button at a time: the checklist's only while the status card is hidden.
        InstallAllMissingButton.Style = ButtonStyle(accent: checklistOwnsSetup);
        ChecklistServerButton.Style = ButtonStyle(accent: checklistOwnsSetup);
        // Snapshots repeat every few seconds; rebuilding would move focus and re-announce the list.
        if (!ChecklistEntries.SequenceEqual(checklist.Items))
        {
            ChecklistEntries.Clear();
            foreach (var item in checklist.Items)
            {
                ChecklistEntries.Add(item);
            }
        }

        InstallAllMissingButton.Visibility = checklist.MissingPrerequisites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallAllMissingButton.IsEnabled = !prerequisiteActionInProgress;
        ChecklistServerButton.Visibility = checklist.CurrentStep == SetupStep.ServerProfile ? Visibility.Visible : Visibility.Collapsed;
        var manualOnly = checklist.MissingPrerequisites.Where(item => !PrerequisiteInstallerService.CanInstallAutomatically(item.Id)).ToArray();
        ChecklistNoteText.Text = manualOnly.Length == 0
            ? string.Empty
            : $"{string.Join(" and ", manualOnly.Select(item => item.DisplayName))} must be installed from the official download page" +
              (manualOnly.Any(item => item.Id == "npcap") ? "; for Npcap, enable \"WinPcap API-compatible mode\" in its installer." : ".");
        ChecklistNoteText.Visibility = manualOnly.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChecklistServerButton_Click(object sender, RoutedEventArgs e) => FocusServerEndpoint();

    /// <summary>Deep link from a prerequisite error to the Required software card.</summary>
    private void ShowRequiredSoftware()
    {
        NavigateTo("diagnostics");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            RequiredSoftwareCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true }));
    }

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
        if (InstallAllMissingButton is not null)
        {
            InstallAllMissingButton.IsEnabled = !prerequisiteActionInProgress;
        }
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

        statusText.Text = prerequisite.IsInstalled ? "✓ Installed" : "⚠ Missing";
        detailText.Text = prerequisite.Detail;
        actionButton.Content = prerequisite.IsInstalled ? "Installed" : actionLabel;
        actionButton.IsEnabled = !prerequisite.IsInstalled && !prerequisiteActionInProgress;
    }
}
