using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Usage;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop;

public sealed class DataUsageProfileOption
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>The Data usage page: traffic through Paqet per day, for one profile or all of them.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan DataUsageRefreshInterval = TimeSpan.FromSeconds(5);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? dataUsageTimer;
    private DataUsageReport? lastDataUsage;
    private bool dataUsageRequestInFlight;
    private bool updatingDataUsageProfiles;

    public ObservableCollection<DataUsageProfileOption> DataUsageProfiles { get; } = [];

    private Guid? SelectedDataUsageProfileId => (DataUsageProfileBox.SelectedItem as DataUsageProfileOption)?.Id;

    /// <summary>Refreshes while the page is shown; usage keeps being recorded by the broker regardless.</summary>
    private void UpdateDataUsagePolling(bool visible)
    {
        if (!visible)
        {
            dataUsageTimer?.Stop();
            return;
        }

        if (dataUsageTimer is null)
        {
            dataUsageTimer = DispatcherQueue.CreateTimer();
            dataUsageTimer.Interval = DataUsageRefreshInterval;
            dataUsageTimer.IsRepeating = true;
            dataUsageTimer.Tick += (_, _) =>
            {
                if (AppWindow.IsVisible)
                {
                    _ = RefreshDataUsageAsync(reset: null);
                }
            };
        }

        UpdateDataUsageProfiles();
        _ = RefreshDataUsageAsync(reset: null);
        dataUsageTimer.Start();
    }

    private void DataUsageProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingDataUsageProfiles)
        {
            RenderDataUsage();
        }
    }

    private async void RefreshDataUsageButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshDataUsageAsync(reset: null);

    private async void ResetDataUsageButton_Click(object sender, RoutedEventArgs e)
    {
        if (Content?.XamlRoot is null)
        {
            return;
        }

        var selected = DataUsageProfileBox.SelectedItem as DataUsageProfileOption;
        var dialog = new ContentDialog
        {
            Title = "Reset data usage?",
            Content = selected?.Id is null
                ? "This clears the recorded usage of every profile. It cannot be undone."
                : $"This clears the recorded usage of {selected.Name}. It cannot be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        if (await RefreshDataUsageAsync(new DataUsageReset(selected?.Id)))
        {
            ShowInfo(DataUsageInfoBar, InfoBarSeverity.Success, "Usage reset", "Recording starts again from now.");
        }
    }

    private async Task<bool> RefreshDataUsageAsync(DataUsageReset? reset)
    {
        // A reset is the user's request; a timer tick simply waits for the next one.
        if (dataUsageRequestInFlight && reset is null)
        {
            return false;
        }

        dataUsageRequestInFlight = true;
        try
        {
            var result = await ViewModel.GetDataUsageAsync(reset);
            if (result.Report is null)
            {
                ShowInfo(
                    DataUsageInfoBar,
                    InfoBarSeverity.Error,
                    reset is null ? "Usage is unavailable" : "Usage was not reset",
                    result.Error ?? "The broker did not return data usage.");
                return false;
            }

            if (DataUsageInfoBar.IsOpen && DataUsageInfoBar.Severity == InfoBarSeverity.Error)
            {
                DataUsageInfoBar.IsOpen = false;
            }

            lastDataUsage = result.Report;
            UpdateDataUsageProfiles();
            RenderDataUsage();
            return true;
        }
        finally
        {
            dataUsageRequestInFlight = false;
        }
    }

    /// <summary>"All profiles" plus each saved profile, keeping the current choice.</summary>
    private void UpdateDataUsageProfiles()
    {
        var options = new List<DataUsageProfileOption> { new() { Name = "All profiles" } };
        options.AddRange(ProfileEntries.Select(profile => new DataUsageProfileOption { Id = profile.Id, Name = profile.Name }));
        if (DataUsageProfiles.Select(option => (option.Id, option.Name))
            .SequenceEqual(options.Select(option => (option.Id, option.Name))))
        {
            return;
        }

        var selectedId = SelectedDataUsageProfileId;
        updatingDataUsageProfiles = true;
        try
        {
            DataUsageProfiles.Clear();
            foreach (var option in options)
            {
                DataUsageProfiles.Add(option);
            }

            DataUsageProfileBox.SelectedItem =
                DataUsageProfiles.FirstOrDefault(option => option.Id == selectedId) ?? DataUsageProfiles[0];
        }
        finally
        {
            updatingDataUsageProfiles = false;
        }
    }

    private void RenderDataUsage()
    {
        if (lastDataUsage is null)
        {
            return;
        }

        var summary = DataUsageText.Summarize(lastDataUsage, SelectedDataUsageProfileId);
        DataUsageTodayText.Text = summary.Today.TotalText;
        DataUsageTodayDetail.Text = summary.Today.DetailText;
        DataUsageMonthText.Text = summary.ThisMonth.TotalText;
        DataUsageMonthDetail.Text = summary.ThisMonth.DetailText;
        DataUsageRecentText.Text = summary.RecentDays.TotalText;
        DataUsageRecentDetail.Text = summary.RecentDays.DetailText;
        DataUsageDaysList.ItemsSource = summary.Days;
        var empty = summary.Days.Count == 0;
        DataUsageEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DataUsageDaysHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
}
