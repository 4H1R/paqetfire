using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Engines;
using PaqetFire.Core.RoutingData;

namespace PaqetFire.Broker.Diagnostics;

public sealed record DiagnosticBundleInput(
    PaqetFireSettings? Settings,
    int ProfileCount,
    ConnectionStatus Connection,
    TunnelHealthReport Tunnel,
    string PayloadState,
    IReadOnlyList<PrerequisiteStatus> Prerequisites,
    DiagnosticNetwork? Network,
    ConnectionVerificationReport? Verification,
    IReadOnlyList<string> RecentLogs,
    IReadOnlyList<string?> Secrets,
    IReadOnlyList<string>? Notes = null,
    RoutingDataStatus? RoutingData = null);

public sealed record DiagnosticNetwork(
    string InterfaceName,
    string LocalIpv4Address,
    bool HasGatewayMac,
    IReadOnlyList<string> DnsServers,
    string? Error = null);

/// <summary>A plain-text report for bug reports. Every line passes through <see cref="DiagnosticRedactor"/>.</summary>
public static class DiagnosticBundle
{
    public static string Build(DiagnosticBundleInput input, DateTimeOffset now)
    {
        var text = new StringBuilder(8 * 1024);
        var version = typeof(DiagnosticBundle).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
        text.AppendLine("PaqetFire diagnostics");
        text.AppendLine(CultureInfo.InvariantCulture, $"Generated: {now:yyyy-MM-dd HH:mm:ss}Z");
        text.AppendLine(CultureInfo.InvariantCulture, $"Broker: {version} · {RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}");
        text.AppendLine("Secrets, the server host, and public IP addresses are redacted.");

        Section(text, "Connection");
        text.AppendLine(CultureInfo.InvariantCulture, $"State: {input.Connection.State}");
        if (input.Connection.Detail is { } detail) text.AppendLine(CultureInfo.InvariantCulture, $"Detail: {detail}");
        var lastSuccess = input.Tunnel.LastSuccessAt is { } success
            ? string.Create(CultureInfo.InvariantCulture, $" · last success {success:HH:mm:ss}Z")
            : string.Empty;
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Tunnel: {input.Tunnel.State} · consecutive failures {input.Tunnel.ConsecutiveFailures} · restarts {input.Tunnel.RecoveryAttempts}{lastSuccess}");
        foreach (var engine in input.Connection.Engines)
        {
            var engineDetail = engine.Detail is null ? string.Empty : " · " + engine.Detail;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{engine.Engine}: {engine.State} · {engine.Version ?? "unknown version"} · {engine.Endpoint ?? "no endpoint"}{engineDetail}");
        }

        foreach (var note in input.Notes ?? [])
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Note: {note}");
        }

        Section(text, "Installation");
        text.AppendLine(CultureInfo.InvariantCulture, $"Engine payload: {input.PayloadState}");
        foreach (var prerequisite in input.Prerequisites)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{prerequisite.DisplayName}: {(prerequisite.IsInstalled ? "installed" : "missing")} · {prerequisite.Detail}");
        }

        if (input.RoutingData is { } data)
        {
            var installed = data.InstalledAt is { } installedAt
                ? string.Create(CultureInfo.InvariantCulture, $" · installed {installedAt:yyyy-MM-dd HH:mm}Z")
                : string.Empty;
            var checkedAt = data.LastCheckedAt is { } lastChecked
                ? string.Create(CultureInfo.InvariantCulture, $"{lastChecked:yyyy-MM-dd HH:mm}Z")
                : "never";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Routing data: {data.Origin} · {data.Version}{installed} · last checked {checkedAt} · automatic updates {OnOff(data.AutomaticUpdates)}{(data.IsUpdating ? " · updating" : string.Empty)}");
            if (data.LastError is { } routingDataError)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Routing data error: {routingDataError}");
            }
        }

        Section(text, "Network");
        if (input.Network is { } network)
        {
            if (network.Error is { } error)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Detection failed: {error}");
            }
            else
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Interface: {network.InterfaceName} · {network.LocalIpv4Address}");
                text.AppendLine(CultureInfo.InvariantCulture, $"Gateway MAC: {(network.HasGatewayMac ? "detected" : "missing")}");
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"DNS servers: {(network.DnsServers.Count == 0 ? "none" : string.Join(", ", network.DnsServers))}");
            }
        }

        AppendListeners(text, input.Settings);

        Section(text, "Profile");
        if (input.Settings is { } settings)
        {
            AppendSettings(text, settings, input.ProfileCount);
        }
        else
        {
            text.AppendLine("No saved profile.");
        }

        Section(text, "Last verification");
        if (input.Verification is { } verification)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{verification.Summary} at {verification.CompletedAt:HH:mm:ss}Z");
            foreach (var check in verification.Checks)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{check.Status,-8} {check.Name}: {check.Detail}");
            }
        }
        else
        {
            text.AppendLine("Not run since the broker started.");
        }

        Section(text, "Recent engine logs");
        foreach (var line in input.RecentLogs)
        {
            text.AppendLine(line);
        }

        return DiagnosticRedactor.Redact(text.ToString(), input.Secrets, ServerHost(input.Settings?.ServerEndpoint));
    }

    private static void AppendSettings(StringBuilder text, PaqetFireSettings settings, int profileCount)
    {
        var endpoint = settings.ServerEndpoint;
        var port = endpoint.LastIndexOf(':') is var separator and > 0 ? endpoint[(separator + 1)..] : "?";
        var host = ServerHost(endpoint);
        text.AppendLine(CultureInfo.InvariantCulture, $"Profiles saved: {profileCount}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Server: {DiagnosticRedactor.ServerPlaceholder}:{port} ({(host is not null && IPAddress.TryParse(host, out _) ? "IP address" : "host name")})");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Routing: {settings.RoutingMode} · {settings.SelectedApplications.Count} selected · {settings.UserExclusions.Count} excluded · {settings.DirectRouteDestinations.Count} direct destinations");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Protocols: TCP {OnOff(settings.RouteTcp)} · UDP {OnOff(settings.RouteUdp)} · IPv4 {OnOff(settings.RouteIpv4)} · IPv6 {OnOff(settings.RouteIpv6)} · LAN bypass {OnOff(settings.BypassLan)}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Xray: {settings.RegionalPreset} · {settings.DomainStrategy} · DNS {settings.DnsResolver} · FakeDNS {OnOff(settings.FakeDns)} · ads blocked {OnOff(settings.BlockAds)} · malware blocked {OnOff(settings.BlockMalware)} · QUIC blocked {OnOff(settings.BlockQuic)} · BitTorrent direct {OnOff(settings.DirectBitTorrent)}");
        text.AppendLine(settings.ExitProxy switch
        {
            ExitProxyKind.Psiphon => string.Create(CultureInfo.InvariantCulture,
                $"Exit proxy: Psiphon · country {(settings.Psiphon?.EgressRegion is { Length: > 0 } region ? region : "best available")}"),
            ExitProxyKind.OpenVpn => DescribeOpenVpn(settings.OpenVpn),
            _ => "Exit proxy: none",
        });
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Kill switch: {OnOff(settings.KillSwitchEnabled)} · LAN share {OnOff(settings.ShareWithLan)} ({settings.LanSocksPort}) · hotspot share {OnOff(settings.ShareViaHotspot)} ({settings.HotspotSocksPort})");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Paqet: KCP {settings.KcpMode} · local flags {string.Join(",", settings.LocalTcpFlags)} · remote flags {string.Join(",", settings.RemoteTcpFlags)} · transport key {(string.IsNullOrEmpty(settings.TransportKey) ? "missing" : "set")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Advanced: {settings.Advanced}");
    }

    private static void AppendListeners(StringBuilder text, PaqetFireSettings? settings)
    {
        Section(text, "Local listeners");
        var ports = new List<(string Name, int Port)>
        {
            ("Paqet SOCKS5", XrayJsonConfigurationWriter.PaqetPort),
            ("Xray SOCKS5", XrayJsonConfigurationWriter.InboundPort),
            ("Xray statistics", XrayJsonConfigurationWriter.StatsApiPort),
        };
        if (settings?.ExitProxy == ExitProxyKind.Psiphon) ports.Add(("Psiphon SOCKS5", PsiphonJsonConfigurationWriter.LocalSocksPort));
        if (settings?.ShareWithLan == true) ports.Add(("LAN share", settings.LanSocksPort));
        if (settings?.ShareViaHotspot == true) ports.Add(("Hotspot share", settings.HotspotSocksPort));
        if (settings?.ExitProxy == ExitProxyKind.OpenVpn)
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, OpenVpnConfigurationWriter.AdapterName, StringComparison.OrdinalIgnoreCase));
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{OpenVpnConfigurationWriter.AdapterName} adapter: {(adapter is null ? "missing" : adapter.OperationalStatus.ToString())}");
        }

        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (NetworkInformationException exception)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Unavailable: {exception.Message}");
            return;
        }

        foreach (var (name, port) in ports)
        {
            var bound = listeners.Where(listener => listener.Port == port).Select(listener => listener.ToString()).ToArray();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{name} ({port}): {(bound.Length == 0 ? "not listening" : string.Join(", ", bound))}");
        }
    }

    /// <summary>Like the Paqet server, the VPN server is described without its address.</summary>
    private static string DescribeOpenVpn(OpenVpnExitOptions? options)
    {
        var configuration = OpenVpnConfiguration.Parse(options?.Configuration);
        var servers = configuration.Remote is { } remote
            ? $"{configuration.Remotes.Count} server(s), first on port {remote.Port}"
            : "no server";
        var account = !configuration.RequiresCredentials ? "not needed"
            : string.IsNullOrEmpty(options?.Password) ? "missing" : "set";
        var ignored = configuration.IgnoredOptions.Count == 0 ? "none" : string.Join(", ", configuration.IgnoredOptions);
        var errors = configuration.IsValid ? string.Empty : " · invalid: " + string.Join(" ", configuration.Errors);
        return string.Create(CultureInfo.InvariantCulture,
            $"Exit proxy: OpenVPN · {servers} · account {account} · ignored options {ignored}{errors}");
    }

    private static string? ServerHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var separator = endpoint.LastIndexOf(':');
        return separator > 0 ? endpoint[..separator].Trim('[', ']') : endpoint;
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private static void Section(StringBuilder text, string title) =>
        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"[{title}]");
}
