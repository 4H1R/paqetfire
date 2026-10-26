using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public async Task WatchdogRestartsTheTransportOnlyAfterRepeatedConnectedFailures()
    {
        var controller = new StatusController(ConnectionState.Connected);
        var probe = new ScriptedProbe(false, false, false, false, true);
        var runtime = new RestartRuntime();
        var tracker = new TunnelHealthTracker();
        var watchdog = new TunnelWatchdog(controller, probe, tracker, runtime, NullLogger<TunnelWatchdog>.Instance);

        for (var index = 0; index < 4; index++) await watchdog.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, runtime.Restarts);
        Assert.Equal(TunnelHealthState.Stalled, tracker.Report.State);
        await watchdog.CheckOnceAsync(CancellationToken.None);
        Assert.Equal(TunnelHealthState.Healthy, tracker.Report.State);

        controller.State = ConnectionState.Disconnected;
        await watchdog.CheckOnceAsync(CancellationToken.None);
        Assert.Equal(TunnelHealthReport.Unknown, tracker.Report);
        Assert.Equal(5, probe.Calls);
    }

    [Fact]
    public async Task TransportRestartTouchesOnlyPaqet()
    {
        var paqet = new RecordingEngine(EngineKind.Paqet);
        var xray = new RecordingEngine(EngineKind.Xray);
        var proxiFyre = new RecordingEngine(EngineKind.ProxiFyre);
        using var controller = new ConnectionController(paqet, xray, proxiFyre);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.RestartTransportAsync(CancellationToken.None);

        Assert.Equal(["start", "stop", "start"], paqet.Calls);
        Assert.Equal(["start"], xray.Calls);
        Assert.Equal(["start"], proxiFyre.Calls);
        Assert.Equal(ConnectionState.Connected, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public void DnsAnswersAreParsedThroughNamePointersAndCnames()
    {
        var query = DirectDnsResolver.CreateQuery(0x1234, "example.com", 1);
        var response = query.ToList();
        response[2] = 0x81;
        response[3] = 0x80;
        response[7] = 2;
        // CNAME to an unrelated name, then an A record, both with compressed owner names.
        response.AddRange([0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 5, 3, (byte)'c', (byte)'d', (byte)'n', 0]);
        response.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 93, 184, 216, 34]);

        Assert.True(DirectDnsResolver.TryReadAddresses(response.ToArray(), 0x1234, out var addresses));
        Assert.Equal([IPAddress.Parse("93.184.216.34")], addresses);
        Assert.False(DirectDnsResolver.TryReadAddresses(response.ToArray(), 0x4321, out _));
        Assert.False(DirectDnsResolver.TryReadAddresses(query, 0x1234, out _));
    }

    [Fact]
    public async Task OrphanCleanupStopsOnlyExecutablesAtTheInstalledPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-orphan-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var system = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var installed = Path.Combine(directory, "ping.exe");
        File.Copy(system, installed);
        using var orphan = StandInProcess.Start(installed);
        using var unrelated = StandInProcess.Start(system);
        try
        {
            var stopped = await OrphanedEngineCleanup.TerminateAsync([installed], CancellationToken.None);

            Assert.Equal(["ping.exe"], stopped);
            Assert.True(orphan.HasExited);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill();
            unrelated.Kill();
            await unrelated.WaitForExitAsync();
            await orphan.WaitForExitAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EnginesCanBeBoundToTheBrokerJob()
    {
        using var child = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), "-t 127.0.0.1") { CreateNoWindow = true })!;
        try
        {
            EngineProcessJob.Assign(child);
            Assert.True(IsProcessInJob(child.SafeHandle, IntPtr.Zero, out var inJob));
            Assert.True(inJob);
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    [Fact]
    public void DiagnosticBundleSummarizesStateWithoutSecretsOrPublicAddresses()
    {
        var settings = new PaqetFireSettings
        {
            ServerEndpoint = "vpn.example.net:8443",
            TransportKey = "transport-secret",
            LanSocksPassword = "lan-secret",
            FakeDns = true,
        };
        var paqet = new EngineStatus(EngineKind.Paqet, EngineState.Running, "v1.0.0-alpha.21", "127.0.0.1:1080");
        var report = new ConnectionVerificationReport(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        [
            new(VerificationCheckKind.PublicAddress, VerificationCheckStatus.Passed, "Proxied public address", "Exit 203.0.113.7"),
        ]);

        var text = DiagnosticBundle.Build(new DiagnosticBundleInput(
                settings,
                2,
                new ConnectionStatus(ConnectionState.Connected, paqet,
                    new EngineStatus(EngineKind.Xray, EngineState.Running), new EngineStatus(EngineKind.ProxiFyre, EngineState.Running)),
                new TunnelHealthReport(TunnelHealthState.Stalled, null, 4, 1, null),
                "Ready · verified",
                [new PrerequisiteStatus("npcap", "Npcap", true, "1.80")],
                new DiagnosticNetwork("Wi-Fi", "192.168.100.20", true, ["192.168.100.1"]),
                report,
                ["12:00:00  ERR  Paqet · dial vpn.example.net (198.51.100.4): transport-secret rejected"],
                [settings.TransportKey, settings.LanSocksPassword, "engine-secret"]),
            DateTimeOffset.UtcNow);

        Assert.Contains("Tunnel: Stalled · consecutive failures 4 · restarts 1", text);
        Assert.Contains("Server: [server]:8443 (host name)", text);
        Assert.Contains("FakeDNS on", text);
        Assert.Contains("192.168.100.1", text);
        Assert.Contains("Paqet · dial [server] ([public-ip]): [secret] rejected", text);
        foreach (var leaked in new[] { "transport-secret", "lan-secret", "vpn.example.net", "203.0.113.7", "198.51.100.4" })
        {
            Assert.DoesNotContain(leaked, text, StringComparison.Ordinal);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(Microsoft.Win32.SafeHandles.SafeProcessHandle process, IntPtr job, out bool result);

    private sealed class ScriptedProbe(params bool[] results) : ITunnelProbe
    {
        public int Calls { get; private set; }
        public ValueTask<bool> CheckReachabilityAsync(CancellationToken cancellationToken) => new(results[Calls++]);
        public ValueTask<int?> MeasureLatencyAsync(CancellationToken cancellationToken) => new((int?)null);
        public ValueTask<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken) => new((IPAddress?)null);
    }

    private sealed class StatusController(ConnectionState state) : IConnectionController
    {
        public ConnectionState State { get; set; } = state;
        public ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken) => new(new ConnectionStatus(State,
            new EngineStatus(EngineKind.Paqet, EngineState.Running),
            new EngineStatus(EngineKind.Xray, EngineState.Running),
            new EngineStatus(EngineKind.ProxiFyre, EngineState.Running)));
        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask GuardAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RestartTransportAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RestartTransportAsync(bool includePolicy, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingEngine(EngineKind kind) : IEngineAdapter
    {
        private EngineState state = EngineState.Stopped;
        public List<string> Calls { get; } = [];
        public EngineKind Kind => kind;
        public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken) => new(new EngineStatus(kind, state));
        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            Calls.Add("start");
            state = EngineState.Running;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            state = EngineState.Stopped;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RestartRuntime : IPaqetFireRuntime
    {
        public int Restarts { get; private set; }
        public ValueTask RestartTransportAsync(CancellationToken cancellationToken)
        {
            Restarts++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> SuperviseAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public Guid? ActiveProfileId => null;

        public DateTimeOffset? NextSupervisionDue => null;
        public ValueTask HandleNetworkChangeAsync(bool resumed, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> SaveSettingsAsync(PaqetFireSettings settings, bool connectAfterSave,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> DisconnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask StopEnginesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> VerifyConnectionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> ManageProfilesAsync(ProfileAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string> ExportProfilesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string> GetDiagnosticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
