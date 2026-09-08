using System.Diagnostics;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Routing;

namespace PaqetFire.Broker.Diagnostics;

public interface IConnectionVerifier
{
    ValueTask<ConnectionVerificationReport> VerifyAsync(
        ConnectionStatus connection,
        PaqetFireSettings settings,
        CancellationToken cancellationToken);
}

public interface IRouteProbe
{
    ValueTask<RouteProbeResult> CheckLocalRouteAsync(CancellationToken cancellationToken);

    ValueTask<RouteProbeResult> CheckTcpIpv4Async(CancellationToken cancellationToken);

    ValueTask<RouteProbeResult> CheckUdpDnsAsync(CancellationToken cancellationToken);

    ValueTask<RouteProbeResult> CheckIpv6Async(CancellationToken cancellationToken);
}

public sealed record RouteProbeResult(
    bool Succeeded,
    string Detail,
    long DurationMilliseconds,
    string? ObservedValue = null,
    bool IsAvailable = true);

public sealed class ConnectionVerifier(IRouteProbe routeProbe, TimeProvider? timeProvider = null)
    : IConnectionVerifier
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<ConnectionVerificationReport> VerifyAsync(
        ConnectionStatus connection,
        PaqetFireSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        var startedAt = clock.GetUtcNow();
        var checks = new List<ConnectionVerificationCheck>();
        var enginesHealthy = connection.State == ConnectionState.Connected &&
            connection.Paqet.State == EngineState.Running &&
            connection.Xray.State == EngineState.Running &&
            connection.ProxiFyre.State == EngineState.Running;
        checks.Add(new ConnectionVerificationCheck(
            VerificationCheckKind.EngineChain,
            enginesHealthy ? VerificationCheckStatus.Passed : VerificationCheckStatus.Failed,
            "Engine chain",
            enginesHealthy
                ? "Paqet, Xray, and ProxiFyre report healthy running states."
                : "The complete Paqet → Xray → ProxiFyre chain is not connected."));

        checks.Add(new ConnectionVerificationCheck(
            VerificationCheckKind.ApplicationCapture,
            connection.ProxiFyre.State == EngineState.Running
                ? VerificationCheckStatus.Warning
                : VerificationCheckStatus.Failed,
            "Capture process",
            connection.ProxiFyre.State == EngineState.Running
                ? settings.RoutingMode == RoutingMode.AllApplications
                    ? "ProxiFyre is running with the all-applications policy, but this check does not prove that every process is captured."
                    : $"ProxiFyre is running with {settings.SelectedApplications.Count} selected application{(settings.SelectedApplications.Count == 1 ? string.Empty : "s")}; this check does not prove their traffic was captured."
                : "ProxiFyre is not running, so the configured application policy is not being captured."));

        if (!enginesHealthy)
        {
            AddSkippedRouteChecks(checks, settings, "Connect the complete route before running traffic checks.");
            return new ConnectionVerificationReport(startedAt, clock.GetUtcNow(), checks);
        }

        checks.Add(ToCheck(
            VerificationCheckKind.LocalRoute,
            "Local Xray route",
            await routeProbe.CheckLocalRouteAsync(cancellationToken).ConfigureAwait(false)));

        if (settings.RouteTcp && settings.RouteIpv4)
        {
            var tcp = await routeProbe.CheckTcpIpv4Async(cancellationToken).ConfigureAwait(false);
            checks.Add(ToCheck(VerificationCheckKind.TcpIpv4, "TCP over IPv4", tcp));
            checks.Add(new ConnectionVerificationCheck(
                VerificationCheckKind.PublicAddress,
                tcp.Succeeded ? VerificationCheckStatus.Passed : ToFailureStatus(tcp),
                "Proxied public address",
                tcp.Succeeded
                    ? "The public IPv4 address returned through the explicit local Xray route; no direct-route comparison was performed."
                    : tcp.Detail,
                tcp.DurationMilliseconds,
                tcp.ObservedValue));
        }
        else
        {
            checks.Add(Skipped(VerificationCheckKind.TcpIpv4, "TCP over IPv4", "TCP or IPv4 routing is disabled in this profile."));
            checks.Add(Skipped(VerificationCheckKind.PublicAddress, "Proxied public address", "The IPv4 traffic check is disabled in this profile."));
        }

        checks.Add(settings.RouteUdp && settings.RouteIpv4
            ? ToCheck(VerificationCheckKind.UdpDns, "Routed UDP/DNS", await routeProbe.CheckUdpDnsAsync(cancellationToken).ConfigureAwait(false))
            : Skipped(VerificationCheckKind.UdpDns, "Routed UDP/DNS", "UDP or IPv4 routing is disabled in this profile."));

        checks.Add(settings.RouteIpv6
            ? ToCheck(VerificationCheckKind.Ipv6, "IPv6 route", await routeProbe.CheckIpv6Async(cancellationToken).ConfigureAwait(false))
            : Skipped(VerificationCheckKind.Ipv6, "IPv6 route", "IPv6 routing is disabled in this profile."));

        return new ConnectionVerificationReport(startedAt, clock.GetUtcNow(), checks);
    }

    private static void AddSkippedRouteChecks(
        ICollection<ConnectionVerificationCheck> checks,
        PaqetFireSettings settings,
        string detail)
    {
        checks.Add(Skipped(VerificationCheckKind.LocalRoute, "Local Xray route", detail));
        checks.Add(Skipped(VerificationCheckKind.TcpIpv4, "TCP over IPv4", detail));
        checks.Add(Skipped(VerificationCheckKind.UdpDns, "Routed UDP/DNS", detail));
        checks.Add(Skipped(VerificationCheckKind.Ipv6, "IPv6 route", settings.RouteIpv6 ? detail : "IPv6 routing is disabled in this profile."));
        checks.Add(Skipped(VerificationCheckKind.PublicAddress, "Proxied public address", detail));
    }

    private static ConnectionVerificationCheck ToCheck(
        VerificationCheckKind kind,
        string name,
        RouteProbeResult result) => new(
            kind,
            result.Succeeded ? VerificationCheckStatus.Passed : ToFailureStatus(result),
            name,
            result.Detail,
            result.DurationMilliseconds,
            result.ObservedValue);

    private static VerificationCheckStatus ToFailureStatus(RouteProbeResult result) =>
        result.IsAvailable ? VerificationCheckStatus.Failed : VerificationCheckStatus.Warning;

    private static ConnectionVerificationCheck Skipped(
        VerificationCheckKind kind,
        string name,
        string detail) => new(kind, VerificationCheckStatus.Skipped, name, detail);
}
