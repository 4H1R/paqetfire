using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Configuration;
using PaqetFire.Desktop.Presentation;
using Windows.Globalization.NumberFormatting;

namespace PaqetFire.Desktop;

public sealed partial class MainWindow
{
    private PaqetAdvancedOptions advancedOptions = new();
    private bool loadingAdvancedOptions;
    private readonly List<Action> advancedOptionReloaders = [];
    private readonly List<NumberBox> advancedNumberInputs = [];
    private readonly List<Control> manualTuningControls = [];

    private static bool IsAdvancedOptionError(string message) =>
        new[] { "KCP", "TCP flag", "PCAP", "SMUX", "Stream buffer", "FEC", "local IPv6", "IPv6 router", "source port", "Connection count", "Log level", "Advanced Paqet" }
            .Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));

    private void InitializeAdvancedOptions()
    {
        AddAdvancedChoice("Log level", "How much detail Paqet writes to the diagnostics log.", ["none", "debug", "info", "warn", "error", "fatal"],
            value => value.LogLevel, (value, edited) => value with { LogLevel = edited });
        AddAdvancedNumber("IPv4 source port · 0 chooses automatically", "The local port Paqet sends from.", value => value.LocalIpv4Port,
            (value, edited) => value with { LocalIpv4Port = edited ?? 0 }, 0, 65535);
        AddAdvancedText("Local IPv6 endpoint", "This PC's IPv6 address and port, only if the server is reached over IPv6.", value => value.LocalIpv6Endpoint,
            (value, edited) => value with { LocalIpv6Endpoint = string.IsNullOrWhiteSpace(edited) ? null : edited.Trim() });
        AddAdvancedText("IPv6 router MAC", "The MAC address of this network's IPv6 gateway.", value => value.Ipv6RouterMac,
            (value, edited) => value with { Ipv6RouterMac = string.IsNullOrWhiteSpace(edited) ? null : edited.Trim() });
        AddAdvancedNumber("PCAP socket buffer (bytes)", "Packet capture buffer; larger values absorb traffic bursts.", value => value.PcapSocketBufferBytes,
            (value, edited) => value with { PcapSocketBufferBytes = edited }, 1024, 104857600);
        AddAdvancedNumber("Parallel connections", "How many transport connections are opened to the server at once.", value => value.ConnectionCount,
            (value, edited) => value with { ConnectionCount = edited ?? 1 }, 1, 256);
        manualTuningControls.Add(AddAdvancedNumber("No-delay · 0 or 1", "1 sends packets without waiting to batch them. Manual mode only.", value => value.KcpNoDelay,
            (value, edited) => value with { KcpNoDelay = edited }, 0, 1));
        manualTuningControls.Add(AddAdvancedNumber("Update interval (milliseconds)", "How often KCP flushes and retransmits; lower is quicker but busier. Manual mode only.", value => value.KcpIntervalMilliseconds,
            (value, edited) => value with { KcpIntervalMilliseconds = edited }, 10, 5000));
        manualTuningControls.Add(AddAdvancedNumber("Fast retransmit · 0, 1 or 2", "Resends a lost packet early after this many skipped acknowledgments; 0 turns it off. Manual mode only.", value => value.KcpResend,
            (value, edited) => value with { KcpResend = edited }, 0, 2));
        manualTuningControls.Add(AddAdvancedNumber("Disable congestion control · 0 or 1", "1 ignores congestion for maximum throughput. Manual mode only.", value => value.KcpNoCongestion,
            (value, edited) => value with { KcpNoCongestion = edited }, 0, 1));
        manualTuningControls.Add(AddAdvancedChoice("Delay writes", "Batches writes into fewer, larger packets. Manual mode only.", ["Default", "On", "Off"],
            value => value.KcpWriteDelay is null ? "Default" : value.KcpWriteDelay.Value ? "On" : "Off",
            (value, edited) => value with { KcpWriteDelay = edited == "Default" ? null : edited == "On" }));
        manualTuningControls.Add(AddAdvancedChoice("Immediate acknowledgments", "Acknowledges each packet as soon as it arrives. Manual mode only.", ["Default", "On", "Off"],
            value => value.KcpAckNoDelay is null ? "Default" : value.KcpAckNoDelay.Value ? "On" : "Off",
            (value, edited) => value with { KcpAckNoDelay = edited == "Default" ? null : edited == "On" }));
        AddAdvancedNumber("MTU (bytes)", "The largest packet Paqet sends; lower it if the network drops large packets.", value => value.KcpMtu,
            (value, edited) => value with { KcpMtu = edited }, 50, 1500);
        AddAdvancedNumber("Receive window (packets)", "Packets that can arrive before they are acknowledged; larger helps fast, distant links.", value => value.KcpReceiveWindow,
            (value, edited) => value with { KcpReceiveWindow = edited }, 1, 32768, PaqetAdvancedOptions.RecommendedKcpReceiveWindow);
        AddAdvancedNumber("Send window (packets)", "Packets that can be in flight before an acknowledgment; larger helps fast, distant links.", value => value.KcpSendWindow,
            (value, edited) => value with { KcpSendWindow = edited }, 1, 32768, PaqetAdvancedOptions.RecommendedKcpSendWindow);
        AddAdvancedChoice("Encryption algorithm", "Must match the server. none (insecure) turns off encryption and key authentication.", AdvancedTransportText.EncryptionChoices,
            value => AdvancedTransportText.EncryptionChoice(value.KcpBlock), (value, edited) => value with { KcpBlock = edited },
            AdvancedTransportText.EncryptionLabel, ConfirmEncryptionChoiceAsync);
        AddAdvancedNumber("Multiplexer receive buffer (bytes)", "Memory shared by all streams on one connection.", value => value.SmuxBufferBytes,
            (value, edited) => value with { SmuxBufferBytes = edited }, 1024, 2147483647);
        AddAdvancedNumber("Per-stream buffer (bytes)", "Memory for each individual stream.", value => value.StreamBufferBytes,
            (value, edited) => value with { StreamBufferBytes = edited }, 1024, 2147483647);
        AddAdvancedNumber("Keepalive interval (seconds)", "How often an idle connection is checked.", value => value.SmuxKeepAliveSeconds,
            (value, edited) => value with { SmuxKeepAliveSeconds = edited }, 1, 86400);
        AddAdvancedNumber("Keepalive timeout (seconds)", "How long without a reply before a connection is closed.", value => value.SmuxKeepAliveTimeoutSeconds,
            (value, edited) => value with { SmuxKeepAliveTimeoutSeconds = edited }, 1, 86400);
        AddAdvancedNumber("FEC data shards", "Error correction: data packets per group. Must match the server; 0 turns it off.", value => value.FecDataShards,
            (value, edited) => value with { FecDataShards = edited }, 0, 255);
        AddAdvancedNumber("FEC parity shards", "Error correction: recovery packets per group. Must match the server.", value => value.FecParityShards,
            (value, edited) => value with { FecParityShards = edited }, 0, 255);
        KcpModeBox.SelectionChanged += (_, _) => UpdateManualTuningControls();
        LoadAdvancedOptions(new());
    }

    private void LoadAdvancedOptions(PaqetAdvancedOptions options)
    {
        loadingAdvancedOptions = true;
        try
        {
            advancedOptions = options;
            foreach (var reload in advancedOptionReloaders) reload();
            UpdateManualTuningControls();
        }
        finally { loadingAdvancedOptions = false; }
    }

    private void UpdateManualTuningControls()
    {
        foreach (var control in manualTuningControls)
            control.IsEnabled = KcpModeBox.SelectedIndex == 4;
    }

    private NumberBox AddAdvancedNumber(string label, string explanation,
        Func<PaqetAdvancedOptions, int?> read, Func<PaqetAdvancedOptions, int?, PaqetAdvancedOptions> write,
        int minimum, int maximum, int? defaultValue = null)
    {
        var control = new NumberBox
        {
            Header = label, PlaceholderText = defaultValue is { } fallback ? $"Default · {fallback:N0}" : "Default",
            Description = AdvancedTransportText.NumberDescription(explanation, minimum, maximum),
            Value = double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Minimum = minimum, Maximum = maximum,
            // Out-of-range input snaps back to the last valid value instead of being dropped unseen.
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            NumberFormatter = WholeNumberFormatter(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        control.ValueChanged += (_, _) =>
        {
            if (loadingAdvancedOptions) return;
            var number = control.Value;
            // A typed fraction is rounded on screen to the whole number that is stored.
            if (AdvancedTransportText.WholeNumberFor(number) is { } whole)
            {
                control.Value = whole;
                return;
            }

            if (!double.IsNaN(number) && (!double.IsFinite(number) || number < int.MinValue || number > int.MaxValue)) return;
            advancedOptions = write(advancedOptions, double.IsNaN(number) ? null : (int)number);
            ConfigurationEdited(AdvancedTransportExpander);
        };
        advancedOptionReloaders.Add(() => control.Value = read(advancedOptions) is { } number ? number : double.NaN);
        advancedNumberInputs.Add(control);
        AdvancedOptionsPanel.Children.Add(control);
        return control;
    }

    private static DecimalFormatter WholeNumberFormatter() => new()
    {
        IntegerDigits = 1,
        FractionDigits = 0,
        IsGrouped = false,
        NumberRounder = new IncrementNumberRounder
        {
            Increment = 1,
            RoundingAlgorithm = RoundingAlgorithm.RoundHalfAwayFromZero,
        },
    };

    private ComboBox AddAdvancedChoice(string label, string explanation, IReadOnlyList<string> choices,
        Func<PaqetAdvancedOptions, string> read, Func<PaqetAdvancedOptions, string, PaqetAdvancedOptions> write,
        Func<string, string>? display = null, Func<string, Task<bool>>? confirm = null)
    {
        // Items keep the stored value in Tag, so the label shown can differ from it.
        var items = choices.Select(choice => new ComboBoxItem { Content = display?.Invoke(choice) ?? choice, Tag = choice }).ToList();
        var control = new ComboBox
        {
            Header = label, Description = explanation, ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        void Reload() => control.SelectedItem = items.FirstOrDefault(
            item => string.Equals(item.Tag as string, read(advancedOptions), StringComparison.OrdinalIgnoreCase));
        control.SelectionChanged += async (_, _) =>
        {
            if (loadingAdvancedOptions || (control.SelectedItem as ComboBoxItem)?.Tag is not string selected) return;
            if (confirm is not null && !await confirm(selected))
            {
                loadingAdvancedOptions = true;
                try { Reload(); }
                finally { loadingAdvancedOptions = false; }
                return;
            }

            advancedOptions = write(advancedOptions, selected);
            ConfigurationEdited(AdvancedTransportExpander);
        };
        advancedOptionReloaders.Add(Reload);
        AdvancedOptionsPanel.Children.Add(control);
        return control;
    }

    /// <summary>Turning transport encryption off is confirmed; any other algorithm applies at once.</summary>
    private async Task<bool> ConfirmEncryptionChoiceAsync(string block)
    {
        if (!AdvancedTransportText.IsUnencrypted(block) || AdvancedTransportText.IsUnencrypted(advancedOptions.KcpBlock))
        {
            return true;
        }

        if (Content?.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = "Turn off transport encryption?",
            Content = new TextBlock { Text = AdvancedTransportText.NoEncryptionWarning, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Turn off encryption",
            CloseButtonText = "Keep encryption",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private TextBox AddAdvancedText(string label, string explanation,
        Func<PaqetAdvancedOptions, string?> read, Func<PaqetAdvancedOptions, string, PaqetAdvancedOptions> write)
    {
        var control = new TextBox { Header = label, Description = explanation, PlaceholderText = "Not configured", MaxLength = 256 };
        control.TextChanged += (_, _) =>
        {
            if (loadingAdvancedOptions) return;
            advancedOptions = write(advancedOptions, control.Text);
            ConfigurationEdited(AdvancedTransportExpander);
        };
        advancedOptionReloaders.Add(() => control.Text = read(advancedOptions) ?? string.Empty);
        AdvancedOptionsPanel.Children.Add(control);
        return control;
    }

    private void ResetAdvancedOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadAdvancedOptions(new());
        KcpModeBox.SelectedIndex = 1;
        LocalFlagsBox.Text = RemoteFlagsBox.Text = "PA";
        ConfigurationEdited(AdvancedTransportExpander);
    }
}
