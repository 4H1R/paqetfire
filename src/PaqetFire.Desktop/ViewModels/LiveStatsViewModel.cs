using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop.ViewModels;

public sealed record PingBar(double Height, double Opacity);

/// <summary>
/// Presentation state for the connected-session card. Rates are derived from the
/// difference between consecutive broker counter samples.
/// </summary>
public sealed class LiveStatsViewModel : INotifyPropertyChanged
{
    public const int PingHistoryLength = 28;
    private const double MinimumBarHeight = 6;
    private const double MaximumBarHeight = 22;

    private LiveConnectionStats? previous;
    private DateTimeOffset? connectedSince;
    private DateTimeOffset? lastLatencySampleAt;
    private string elapsedText = "00:00:00";
    private string publicAddressText = "Checking…";
    private string countryCode = string.Empty;
    private string downloadRateText = "0 B/s";
    private string uploadRateText = "0 B/s";
    private string downloadTotalText = "Total 0 B";
    private string uploadTotalText = "Total 0 B";
    private string protocolText = LiveStatsText.FormatProtocol(null, null);
    private string endpointText = "—";
    private string latencyText = "—";
    private LatencyQuality quality = LatencyQuality.Unknown;
    private bool hasTrafficCounters;

    public LiveStatsViewModel()
    {
        ResetPingBars();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PingBar> PingBars { get; } = [];

    public string ElapsedText { get => elapsedText; private set => SetProperty(ref elapsedText, value); }

    public string PublicAddressText { get => publicAddressText; private set => SetProperty(ref publicAddressText, value); }

    public string CountryCode
    {
        get => countryCode;
        private set
        {
            if (SetProperty(ref countryCode, value))
            {
                OnPropertyChanged(nameof(HasCountryCode));
            }
        }
    }

    public bool HasCountryCode => CountryCode.Length > 0;

    public string DownloadRateText { get => downloadRateText; private set => SetProperty(ref downloadRateText, value); }

    public string UploadRateText { get => uploadRateText; private set => SetProperty(ref uploadRateText, value); }

    public string DownloadTotalText { get => downloadTotalText; private set => SetProperty(ref downloadTotalText, value); }

    public string UploadTotalText { get => uploadTotalText; private set => SetProperty(ref uploadTotalText, value); }

    public bool HasTrafficCounters { get => hasTrafficCounters; private set => SetProperty(ref hasTrafficCounters, value); }

    public string ProtocolText { get => protocolText; private set => SetProperty(ref protocolText, value); }

    public string EndpointText { get => endpointText; private set => SetProperty(ref endpointText, value); }

    public string LatencyText { get => latencyText; private set => SetProperty(ref latencyText, value); }

    public LatencyQuality Quality
    {
        get => quality;
        private set
        {
            if (SetProperty(ref quality, value))
            {
                OnPropertyChanged(nameof(QualityText));
            }
        }
    }

    public string QualityText => LiveStatsText.GetQualityLabel(Quality);

    public void ApplyProfile(PaqetFireSettingsView? settings)
    {
        ProtocolText = LiveStatsText.FormatProtocol(settings?.KcpMode, settings?.Advanced.KcpBlock);
        EndpointText = string.IsNullOrWhiteSpace(settings?.ServerEndpoint) ? "—" : settings.ServerEndpoint;
    }

    public void Apply(LiveConnectionStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (!stats.IsConnected)
        {
            Reset();
            return;
        }

        if (stats.ConnectedSince != connectedSince)
        {
            Reset();
            connectedSince = stats.ConnectedSince;
        }

        ApplyTraffic(stats);
        ApplyLatency(stats);
        PublicAddressText = stats.PublicAddress ?? "Checking…";
        CountryCode = stats.PublicAddressCountryCode ?? string.Empty;
        Tick(stats.CapturedAt);
        previous = stats;
    }

    /// <summary>Advances the session timer between broker samples.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (connectedSince is { } since)
        {
            ElapsedText = LiveStatsText.FormatElapsed(now - since);
        }
    }

    public void Reset()
    {
        previous = null;
        connectedSince = null;
        lastLatencySampleAt = null;
        ElapsedText = "00:00:00";
        PublicAddressText = "Checking…";
        CountryCode = string.Empty;
        DownloadRateText = "0 B/s";
        UploadRateText = "0 B/s";
        DownloadTotalText = "Total 0 B";
        UploadTotalText = "Total 0 B";
        HasTrafficCounters = false;
        LatencyText = "—";
        Quality = LatencyQuality.Unknown;
        ResetPingBars();
    }

    private void ApplyTraffic(LiveConnectionStats stats)
    {
        if (stats.UplinkBytes is not { } uplink || stats.DownlinkBytes is not { } downlink)
        {
            HasTrafficCounters = false;
            return;
        }

        HasTrafficCounters = true;
        DownloadTotalText = "Total " + LiveStatsText.FormatBytes(downlink);
        UploadTotalText = "Total " + LiveStatsText.FormatBytes(uplink);

        if (previous is { UplinkBytes: { } previousUplink, DownlinkBytes: { } previousDownlink } &&
            stats.CapturedAt > previous.CapturedAt &&
            uplink >= previousUplink && downlink >= previousDownlink)
        {
            var seconds = (stats.CapturedAt - previous.CapturedAt).TotalSeconds;
            DownloadRateText = LiveStatsText.FormatRate((downlink - previousDownlink) / seconds);
            UploadRateText = LiveStatsText.FormatRate((uplink - previousUplink) / seconds);
        }
        else
        {
            // First sample, or Xray restarted and its counters were reset.
            DownloadRateText = "0 B/s";
            UploadRateText = "0 B/s";
        }
    }

    private void ApplyLatency(LiveConnectionStats stats)
    {
        if (stats.LatencyMeasuredAt is not { } measuredAt || measuredAt == lastLatencySampleAt)
        {
            return;
        }

        lastLatencySampleAt = measuredAt;
        LatencyText = stats.LatencyMilliseconds is { } latency ? $"{latency} ms" : "Timeout";
        Quality = LiveStatsText.GetQuality(stats.LatencyMilliseconds);

        var strength = LiveStatsText.GetStrength(stats.LatencyMilliseconds);
        PingBars.RemoveAt(0);
        PingBars.Add(strength > 0
            ? new PingBar(MinimumBarHeight + (MaximumBarHeight - MinimumBarHeight) * strength, 1)
            : new PingBar(MinimumBarHeight, 0.35));
    }

    private void ResetPingBars()
    {
        PingBars.Clear();
        for (var index = 0; index < PingHistoryLength; index++)
        {
            PingBars.Add(new PingBar(MinimumBarHeight, 0.25));
        }
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
