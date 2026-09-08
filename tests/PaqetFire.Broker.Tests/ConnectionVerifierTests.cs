using PaqetFire.Broker.Diagnostics;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ConnectionVerifierTests
{
    [Fact]
    public async Task ConnectedRoute_ReportsEveryEnabledCheck()
    {
        var probe = new RecordingRouteProbe();
        var verifier = new ConnectionVerifier(probe);

        var report = await verifier.VerifyAsync(ConnectedStatus(), new PaqetFireSettings(), CancellationToken.None);

        Assert.True(report.IsHealthy);
        Assert.Equal(6, report.PassedCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(VerificationCheckStatus.Warning,
            report.Checks.Single(check => check.Kind == VerificationCheckKind.ApplicationCapture).Status);
        Assert.Equal(1, probe.LocalCalls);
        Assert.Equal(1, probe.TcpCalls);
        Assert.Equal(1, probe.UdpCalls);
        Assert.Equal(1, probe.Ipv6Calls);
        Assert.Equal("198.51.100.10", report.Checks.Single(check => check.Kind == VerificationCheckKind.PublicAddress).ObservedValue);
    }

    [Fact]
    public async Task DisconnectedRoute_DoesNotContactExternalChecks()
    {
        var probe = new RecordingRouteProbe();
        var verifier = new ConnectionVerifier(probe);
        var stopped = new EngineStatus(EngineKind.Paqet, EngineState.Stopped);
        var status = new ConnectionStatus(
            ConnectionState.Disconnected,
            stopped,
            new EngineStatus(EngineKind.Xray, EngineState.Stopped),
            new EngineStatus(EngineKind.ProxiFyre, EngineState.Stopped));

        var report = await verifier.VerifyAsync(status, new PaqetFireSettings(), CancellationToken.None);

        Assert.False(report.IsHealthy);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(5, report.Checks.Count(check => check.Status == VerificationCheckStatus.Skipped));
        Assert.Equal(0, probe.TotalCalls);
    }

    [Fact]
    public async Task DisabledFamilies_AreExplicitlySkipped()
    {
        var probe = new RecordingRouteProbe();
        var verifier = new ConnectionVerifier(probe);
        var settings = new PaqetFireSettings { RouteUdp = false, RouteIpv6 = false };

        var report = await verifier.VerifyAsync(ConnectedStatus(), settings, CancellationToken.None);

        Assert.Equal(VerificationCheckStatus.Skipped,
            report.Checks.Single(check => check.Kind == VerificationCheckKind.UdpDns).Status);
        Assert.Equal(VerificationCheckStatus.Skipped,
            report.Checks.Single(check => check.Kind == VerificationCheckKind.Ipv6).Status);
        Assert.Equal(0, probe.UdpCalls);
        Assert.Equal(0, probe.Ipv6Calls);
    }

    private static ConnectionStatus ConnectedStatus() => new(
        ConnectionState.Connected,
        new EngineStatus(EngineKind.Paqet, EngineState.Running),
        new EngineStatus(EngineKind.Xray, EngineState.Running),
        new EngineStatus(EngineKind.ProxiFyre, EngineState.Running));

    private sealed class RecordingRouteProbe : IRouteProbe
    {
        public int LocalCalls { get; private set; }
        public int TcpCalls { get; private set; }
        public int UdpCalls { get; private set; }
        public int Ipv6Calls { get; private set; }
        public int TotalCalls => LocalCalls + TcpCalls + UdpCalls + Ipv6Calls;

        public ValueTask<RouteProbeResult> CheckLocalRouteAsync(CancellationToken cancellationToken)
        {
            LocalCalls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "local ok", 1));
        }

        public ValueTask<RouteProbeResult> CheckTcpIpv4Async(CancellationToken cancellationToken)
        {
            TcpCalls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "tcp ok", 2, "198.51.100.10"));
        }

        public ValueTask<RouteProbeResult> CheckUdpDnsAsync(CancellationToken cancellationToken)
        {
            UdpCalls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "udp ok", 3));
        }

        public ValueTask<RouteProbeResult> CheckIpv6Async(CancellationToken cancellationToken)
        {
            Ipv6Calls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "ipv6 ok", 4, "2001:db8::10"));
        }
    }
}
