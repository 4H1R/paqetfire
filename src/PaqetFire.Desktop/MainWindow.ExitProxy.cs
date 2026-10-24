using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Desktop;

/// <summary>Exit proxy card: the provider choice and each provider's own options.</summary>
public sealed partial class MainWindow
{
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
    }

    private ExitProxyKind SelectedExitProxy() =>
        (ExitProxyBox.SelectedItem as ComboBoxItem)?.Tag as string == "psiphon" ? ExitProxyKind.Psiphon : ExitProxyKind.None;

    private void SelectExitProxy(ExitProxyKind kind)
    {
        ExitProxyBox.SelectedIndex = kind == ExitProxyKind.Psiphon ? 1 : 0;
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
        if (PsiphonOptionsPanel is null) return;
        PsiphonOptionsPanel.Visibility = SelectedExitProxy() == ExitProxyKind.Psiphon ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows what the running Psiphon process reports, and adds any country it offers that the list lacks.</summary>
    private void UpdateExitProxyStatus(BrokerSnapshot snapshot)
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
