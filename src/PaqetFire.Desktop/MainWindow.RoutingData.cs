using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.RoutingData;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop;

/// <summary>The Settings page's routing data card, the Routing page's malware hint, and broker notices.</summary>
public sealed partial class MainWindow
{
    private bool applyingRoutingDataStatus;
    private bool routingDataRequestInFlight;
    // Set while an update the user started is running; its result is announced when it ends.
    private bool awaitingRoutingDataUpdate;
    private string? routingDataVersionBeforeUpdate;
    private string? shownRoutingDataError;

    private void NoticeInfoBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissNotice();

    private async void RoutingDataUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        RoutingDataInfoBar.IsOpen = false;
        routingDataVersionBeforeUpdate = ViewModel.RoutingData?.Version;
        var error = await SendRoutingDataActionAsync(RoutingDataAction.UpdateNow);
        if (error is not null)
        {
            ShowInfo(RoutingDataInfoBar, InfoBarSeverity.Error, "Update could not start", error);
            return;
        }

        AddActivity("Routing data update requested.");
        if (ViewModel.RoutingData is { IsUpdating: false } finished)
        {
            // The check finished before the broker answered.
            ShowRoutingDataUpdateResult(finished);
        }
        else
        {
            awaitingRoutingDataUpdate = true;
        }
    }

    private async void RoutingDataBundledButton_Click(object sender, RoutedEventArgs e)
    {
        RoutingDataInfoBar.IsOpen = false;
        awaitingRoutingDataUpdate = false;
        var error = await SendRoutingDataActionAsync(RoutingDataAction.UseBundled);
        if (error is not null)
        {
            ShowInfo(RoutingDataInfoBar, InfoBarSeverity.Error, "Bundled data could not be restored", error);
            return;
        }

        ShowInfo(RoutingDataInfoBar, InfoBarSeverity.Success, "Using bundled data",
            "The downloaded rules were removed and automatic updates are off. Xray uses its bundled data from the next connection.");
        AddActivity("Routing data switched to the data bundled with Xray.");
    }

    private async void RoutingDataAutomaticSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (applyingRoutingDataStatus || !initialized)
        {
            return;
        }

        var enabled = RoutingDataAutomaticSwitch.IsOn;
        var error = await SendRoutingDataActionAsync(enabled
            ? RoutingDataAction.EnableAutomaticUpdates
            : RoutingDataAction.DisableAutomaticUpdates);
        if (error is not null)
        {
            ApplyRoutingDataStatus(ViewModel.RoutingData);
            ShowInfo(RoutingDataInfoBar, InfoBarSeverity.Error, "Setting could not be saved", error);
            return;
        }

        AddActivity($"Setting changed: routing data updates {(enabled ? "automatic" : "manual")}.");
    }

    private async Task<string?> SendRoutingDataActionAsync(RoutingDataAction action)
    {
        routingDataRequestInFlight = true;
        ApplyRoutingDataStatus(ViewModel.RoutingData);
        try
        {
            return await ViewModel.ManageRoutingDataAsync(action);
        }
        finally
        {
            routingDataRequestInFlight = false;
            ApplyRoutingDataStatus(ViewModel.RoutingData);
        }
    }

    private void ApplyRoutingDataStatus(RoutingDataStatus? status)
    {
        if (RoutingDataSourceText is null)
        {
            return;
        }

        var updating = status?.IsUpdating == true;
        RoutingDataSourceText.Text = RoutingDataText.Source(status);
        RoutingDataCheckedText.Text = RoutingDataText.Checked(status);
        RoutingDataProgressBar.Visibility = updating ? Visibility.Visible : Visibility.Collapsed;
        RoutingDataUpdateButton.Content = updating ? "Updating…" : "Update now";
        RoutingDataUpdateButton.IsEnabled = status is not null && !updating && !routingDataRequestInFlight;
        RoutingDataBundledButton.IsEnabled = status is not null && !routingDataRequestInFlight &&
                                             (status.Origin == RoutingDataOrigin.Updated || updating);
        applyingRoutingDataStatus = true;
        try
        {
            RoutingDataAutomaticSwitch.IsOn = status?.AutomaticUpdates ?? true;
            RoutingDataAutomaticSwitch.IsEnabled = status is not null && !routingDataRequestInFlight;
        }
        finally
        {
            applyingRoutingDataStatus = false;
        }

        // The toggle stays usable; the rule simply waits for data that has the categories.
        BlockMalwareHint.Visibility = status is { SupportsMalwareCategories: false } ? Visibility.Visible : Visibility.Collapsed;

        if (status is null || updating)
        {
            return;
        }

        if (awaitingRoutingDataUpdate)
        {
            awaitingRoutingDataUpdate = false;
            ShowRoutingDataUpdateResult(status);
        }
        else if (status.LastError is { } error && !string.Equals(error, shownRoutingDataError, StringComparison.Ordinal))
        {
            // A background update failed; say so once, without interrupting.
            shownRoutingDataError = error;
            ShowInfo(RoutingDataInfoBar, InfoBarSeverity.Warning, "The last routing data update failed", error,
                bringIntoView: false);
        }
        else if (status.LastError is null && shownRoutingDataError is not null)
        {
            shownRoutingDataError = null;
            if (RoutingDataInfoBar.Severity == InfoBarSeverity.Warning)
            {
                RoutingDataInfoBar.IsOpen = false;
            }
        }
    }

    private void ShowRoutingDataUpdateResult(RoutingDataStatus status)
    {
        var (succeeded, title, message) = RoutingDataText.UpdateResult(routingDataVersionBeforeUpdate, status);
        shownRoutingDataError = status.LastError;
        ShowInfo(RoutingDataInfoBar, succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error, title, message);
        AddActivity(title + ".", succeeded ? "INFO" : "WARN");
    }
}
