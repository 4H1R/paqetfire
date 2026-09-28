using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Configuration;

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
        AddAdvancedChoice("Log level", "log.level", ["none", "debug", "info", "warn", "error", "fatal"],
            value => value.LogLevel, (value, edited) => value with { LogLevel = edited });
        AddAdvancedNumber("IPv4 source port · 0 chooses automatically", "network.ipv4.addr", value => value.LocalIpv4Port,
            (value, edited) => value with { LocalIpv4Port = edited ?? 0 }, 0, 65535);
        AddAdvancedText("Local IPv6 endpoint", "network.ipv6.addr", value => value.LocalIpv6Endpoint,
            (value, edited) => value with { LocalIpv6Endpoint = string.IsNullOrWhiteSpace(edited) ? null : edited.Trim() });
        AddAdvancedText("IPv6 router MAC", "network.ipv6.router_mac", value => value.Ipv6RouterMac,
            (value, edited) => value with { Ipv6RouterMac = string.IsNullOrWhiteSpace(edited) ? null : edited.Trim() });
        AddAdvancedNumber("PCAP socket buffer (bytes)", "network.pcap.sockbuf", value => value.PcapSocketBufferBytes,
            (value, edited) => value with { PcapSocketBufferBytes = edited }, 1024, 104857600);
        AddAdvancedNumber("Parallel connections", "transport.conn", value => value.ConnectionCount,
            (value, edited) => value with { ConnectionCount = edited ?? 1 }, 1, 256);
        manualTuningControls.Add(AddAdvancedNumber("No-delay · 0 or 1", "transport.kcp.nodelay", value => value.KcpNoDelay,
            (value, edited) => value with { KcpNoDelay = edited }, 0, 1));
        manualTuningControls.Add(AddAdvancedNumber("Update interval (milliseconds)", "transport.kcp.interval", value => value.KcpIntervalMilliseconds,
            (value, edited) => value with { KcpIntervalMilliseconds = edited }, 10, 5000));
        manualTuningControls.Add(AddAdvancedNumber("Fast retransmit · 0, 1 or 2", "transport.kcp.resend", value => value.KcpResend,
            (value, edited) => value with { KcpResend = edited }, 0, 2));
        manualTuningControls.Add(AddAdvancedNumber("Disable congestion control · 0 or 1", "transport.kcp.nocongestion", value => value.KcpNoCongestion,
            (value, edited) => value with { KcpNoCongestion = edited }, 0, 1));
        manualTuningControls.Add(AddAdvancedChoice("Delay writes", "transport.kcp.wdelay", ["Default", "On", "Off"],
            value => value.KcpWriteDelay is null ? "Default" : value.KcpWriteDelay.Value ? "On" : "Off",
            (value, edited) => value with { KcpWriteDelay = edited == "Default" ? null : edited == "On" }));
        manualTuningControls.Add(AddAdvancedChoice("Immediate acknowledgments", "transport.kcp.acknodelay", ["Default", "On", "Off"],
            value => value.KcpAckNoDelay is null ? "Default" : value.KcpAckNoDelay.Value ? "On" : "Off",
            (value, edited) => value with { KcpAckNoDelay = edited == "Default" ? null : edited == "On" }));
        AddAdvancedNumber("MTU (bytes)", "transport.kcp.mtu", value => value.KcpMtu,
            (value, edited) => value with { KcpMtu = edited }, 50, 1500);
        AddAdvancedNumber("Receive window (packets)", "transport.kcp.rcvwnd", value => value.KcpReceiveWindow,
            (value, edited) => value with { KcpReceiveWindow = edited }, 1, 32768);
        AddAdvancedNumber("Send window (packets)", "transport.kcp.sndwnd", value => value.KcpSendWindow,
            (value, edited) => value with { KcpSendWindow = edited }, 1, 32768);
        AddAdvancedChoice("Encryption algorithm", "transport.kcp.block", ["aes", "aes-128", "aes-128-gcm", "aes-192", "salsa20", "blowfish", "twofish", "cast5", "3des", "tea", "xtea", "xor", "sm4", "none", "null"],
            value => value.KcpBlock, (value, edited) => value with { KcpBlock = edited });
        AdvancedOptionsPanel.Children.Add(new TextBlock
        {
            Text = "The none and null algorithms disable transport encryption and key authentication.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.68,
        });
        AddAdvancedNumber("Multiplexer receive buffer (bytes)", "transport.kcp.smuxbuf", value => value.SmuxBufferBytes,
            (value, edited) => value with { SmuxBufferBytes = edited }, 1024, 2147483647);
        AddAdvancedNumber("Per-stream buffer (bytes)", "transport.kcp.streambuf", value => value.StreamBufferBytes,
            (value, edited) => value with { StreamBufferBytes = edited }, 1024, 2147483647);
        AddAdvancedNumber("Keepalive interval (seconds)", "transport.kcp.smuxkalive", value => value.SmuxKeepAliveSeconds,
            (value, edited) => value with { SmuxKeepAliveSeconds = edited }, 1, 86400);
        AddAdvancedNumber("Keepalive timeout (seconds)", "transport.kcp.smuxktimeout", value => value.SmuxKeepAliveTimeoutSeconds,
            (value, edited) => value with { SmuxKeepAliveTimeoutSeconds = edited }, 1, 86400);
        AddAdvancedNumber("FEC data shards", "transport.kcp.dshard", value => value.FecDataShards,
            (value, edited) => value with { FecDataShards = edited }, 0, 255);
        AddAdvancedNumber("FEC parity shards", "transport.kcp.pshard", value => value.FecParityShards,
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

    private NumberBox AddAdvancedNumber(string label, string key,
        Func<PaqetAdvancedOptions, int?> read, Func<PaqetAdvancedOptions, int?, PaqetAdvancedOptions> write,
        int minimum, int maximum)
    {
        var control = new NumberBox
        {
            Header = label, PlaceholderText = "Default",
            Value = double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(control, $"{key} · {minimum:N0} to {maximum:N0}; leave blank for default");
        control.ValueChanged += (_, _) =>
        {
            if (loadingAdvancedOptions) return;
            var number = control.Value;
            if (!double.IsNaN(number) && (!double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)) return;
            advancedOptions = write(advancedOptions, double.IsNaN(number) ? null : (int)number);
            ConfigurationEdited();
        };
        advancedOptionReloaders.Add(() => control.Value = read(advancedOptions) is { } number ? number : double.NaN);
        advancedNumberInputs.Add(control);
        AdvancedOptionsPanel.Children.Add(control);
        return control;
    }

    private ComboBox AddAdvancedChoice(string label, string key, string[] choices,
        Func<PaqetAdvancedOptions, string> read, Func<PaqetAdvancedOptions, string, PaqetAdvancedOptions> write)
    {
        var control = new ComboBox { Header = label, ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTipService.SetToolTip(control, key);
        control.SelectionChanged += (_, _) =>
        {
            if (loadingAdvancedOptions || control.SelectedItem is not string selected) return;
            advancedOptions = write(advancedOptions, selected);
            ConfigurationEdited();
        };
        advancedOptionReloaders.Add(() => control.SelectedItem = choices.FirstOrDefault(
            choice => string.Equals(choice, read(advancedOptions), StringComparison.OrdinalIgnoreCase)));
        AdvancedOptionsPanel.Children.Add(control);
        return control;
    }

    private TextBox AddAdvancedText(string label, string key,
        Func<PaqetAdvancedOptions, string?> read, Func<PaqetAdvancedOptions, string, PaqetAdvancedOptions> write)
    {
        var control = new TextBox { Header = label, PlaceholderText = "Not configured", MaxLength = 256 };
        ToolTipService.SetToolTip(control, key);
        control.TextChanged += (_, _) =>
        {
            if (loadingAdvancedOptions) return;
            advancedOptions = write(advancedOptions, control.Text);
            ConfigurationEdited();
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
        ConfigurationEdited();
    }
}
