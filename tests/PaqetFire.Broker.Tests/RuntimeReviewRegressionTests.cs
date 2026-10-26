using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;
using PaqetFire.Desktop.Ipc;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class RuntimeReviewRegressionTests
{
    [Fact]
    public async Task DisconnectCancellingQueuedBackgroundWorkIsANormalOutcome()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        fixture.Controller.ConnectGate = Task.Delay(Timeout.Infinite);
        var connecting = fixture.Runtime.ConnectAsync(CancellationToken.None).AsTask();
        await fixture.Controller.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Both queue behind the running connect; the Disconnect cancels them while they wait.
        var networkChange = fixture.Runtime.HandleNetworkChangeAsync(resumed: true, CancellationToken.None).AsTask();
        var restart = fixture.Runtime.RestartTransportAsync(CancellationToken.None).AsTask();
        await fixture.Runtime.DisconnectAsync(CancellationToken.None);

        await networkChange.WaitAsync(TimeSpan.FromSeconds(10));
        await restart.WaitAsync(TimeSpan.FromSeconds(10));
        await connecting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(fixture.Controller.Calls, call => call.StartsWith("restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NetworkHandlingSurvivesAnAdjustmentThatWasCancelled()
    {
        var runtime = new NetworkRuntime();
        var monitor = new NetworkChangeMonitor(runtime, NullLogger<NetworkChangeMonitor>.Instance);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = monitor.RunAsync(stopping.Token);

        monitor.NotifyChanged();
        await runtime.FirstCall.Task.WaitAsync(stopping.Token);
        monitor.NotifyResumed();
        await runtime.SecondCall.Task.WaitAsync(stopping.Token);

        Assert.True(runtime.SecondCallResumed);
        await stopping.CancelAsync();
        await running;
    }

    [Fact]
    public void ABusyRuntimeDefersAnOverdueRecoveryInsteadOfSpinning()
    {
        var now = DateTimeOffset.UtcNow;
        var tick = TimeSpan.FromSeconds(3);

        Assert.Equal(BrokerWorker.BusyRetryDelay, BrokerWorker.NextWait(tick, now.AddSeconds(-1), now, supervisionBusy: true));
        Assert.Equal(TimeSpan.Zero, BrokerWorker.NextWait(tick, now.AddSeconds(-1), now, supervisionBusy: false));
        Assert.Equal(TimeSpan.FromSeconds(1), BrokerWorker.NextWait(tick, now.AddSeconds(1), now, supervisionBusy: true));
        Assert.Equal(tick, BrokerWorker.NextWait(tick, null, now, supervisionBusy: true));
        Assert.Equal(TimeSpan.FromMilliseconds(20),
            BrokerWorker.NextWait(TimeSpan.FromMilliseconds(20), now.AddSeconds(-1), now, supervisionBusy: true));
    }

    [Fact]
    public async Task AFailedBackgroundReconnectWithoutKillSwitchStopsTheLeftoverCapture()
    {
        var (state, catalog) = await WantedRouteAsync();
        await using var fixture = await RuntimeFixture.StartAsync(state: state, catalog: catalog);
        fixture.Controller.FailConnect = true;
        using var leftover = StartStandIn(fixture.Paths.ProxiFyreExecutablePath);
        try
        {
            await fixture.Runtime.SuperviseAsync(CancellationToken.None);

            Assert.Equal(["connect"], fixture.Controller.Calls);
            Assert.True(leftover.WaitForExit(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Stop(leftover);
        }
    }

    [Fact]
    public async Task AFailedExplicitConnectNeverKillsTheBrokersOwnCapture()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        // Running with other settings (no recorded route), so the connect gets past the
        // "already connected" check and then fails on a corrupted payload.
        fixture.Controller.Paqet = fixture.Controller.Xray = fixture.Controller.ProxiFyre = EngineState.Running;
        var engine = Path.Combine(fixture.Paths.PayloadRoot, "engines", "engine.bin");
        File.WriteAllBytes(engine, [9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(engine, DateTime.UtcNow.AddMinutes(1));
        using var own = StartStandIn(fixture.Paths.ProxiFyreExecutablePath);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.ConnectAsync(CancellationToken.None).AsTask());

            Assert.False(own.WaitForExit(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            Stop(own);
        }
    }

    [Fact]
    public async Task APlainSaveDuringReconnectBackoffDoesNotForceAnEarlyReconnect()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        await fixture.Runtime.ConnectAsync(CancellationToken.None);
        fixture.Controller.Paqet = fixture.Controller.Xray = fixture.Controller.ProxiFyre = EngineState.Stopped;
        fixture.Controller.FailConnect = true;
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Controller.Calls.Count(call => call == "connect"));
        fixture.Controller.FailConnect = false;

        await fixture.Runtime.SaveSettingsAsync(
            fixture.Catalog.Catalog!.ActiveProfile.Settings with { ProfileName = "Renamed" },
            connectAfterSave: false,
            CancellationToken.None);
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Controller.Calls.Count(call => call == "connect"));

        // The route is still wanted; it is restored when the backoff ends.
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        Assert.Equal(3, fixture.Controller.Calls.Count(call => call == "connect"));
    }

    [Fact]
    public async Task SelectingAnotherProfileWhileReconnectingEndsTheWishToReconnect()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        await fixture.Runtime.ConnectAsync(CancellationToken.None);
        fixture.Controller.Paqet = fixture.Controller.Xray = fixture.Controller.ProxiFyre = EngineState.Stopped;
        fixture.Controller.FailConnect = true;
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);
        fixture.Controller.FailConnect = false;

        await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.Duplicate), CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Runtime.SuperviseAsync(CancellationToken.None);

        Assert.Equal(2, fixture.Controller.Calls.Count(call => call == "connect"));
        Assert.False(fixture.State.Load().DesiredConnected);
    }

    [Fact]
    public async Task CancellingSaveAndConnectStillStoresTheEditsAndSaysSo()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        fixture.Controller.ConnectGate = Task.Delay(Timeout.Infinite);
        var edited = fixture.Catalog.Catalog!.ActiveProfile.Settings with { ServerEndpoint = "203.0.113.20:8443" };

        var saving = fixture.Runtime.SaveSettingsAsync(edited, connectAfterSave: true, CancellationToken.None).AsTask();
        await fixture.Controller.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        var saved = await saving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("saved", saved.OperationWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancelled", saved.OperationWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("203.0.113.20:8443", saved.Settings!.ServerEndpoint);
        Assert.True(saved.Settings.HasTransportKey);
        Assert.Equal("203.0.113.20:8443", fixture.Catalog.Catalog!.ActiveProfile.Settings.ServerEndpoint);
        Assert.False(fixture.State.Load().DesiredConnected);
    }

    [Fact]
    public async Task ASaveQueuedBehindAConnectIsStoredEvenWhenADisconnectFollows()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        fixture.Controller.ConnectGate = Task.Delay(Timeout.Infinite);
        var connecting = fixture.Runtime.ConnectAsync(CancellationToken.None).AsTask();
        await fixture.Controller.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var edited = fixture.Catalog.Catalog!.ActiveProfile.Settings with { ServerEndpoint = "203.0.113.30:8443" };

        var saving = fixture.Runtime.SaveSettingsAsync(edited, connectAfterSave: false, CancellationToken.None).AsTask();
        await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        var saved = await saving.WaitAsync(TimeSpan.FromSeconds(10));
        await connecting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("203.0.113.30:8443", saved.Settings!.ServerEndpoint);
        Assert.Equal("203.0.113.30:8443", fixture.Catalog.Catalog!.ActiveProfile.Settings.ServerEndpoint);
    }

    [Fact]
    public async Task AnOlderStateWriteCannotUndoANewerDisconnect()
    {
        var store = new GatedStateStore();
        await using var fixture = await RuntimeFixture.StartAsync(state: store, ipv6Probe: new UnsupportedIpv6());
        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        // The IPv6 result is recorded off the runtime gate and its write stalls.
        fixture.Tunnel.Observe(true, DateTimeOffset.UtcNow);
        await store.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disconnecting = fixture.Runtime.DisconnectAsync(CancellationToken.None).AsTask();
        store.Release.SetResult();
        await disconnecting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(store.Load().DesiredConnected);
        Assert.False(store.Load().Ipv6Support[RuntimeFixture.Server]);
    }

    [Fact]
    public async Task AReconnectingDesktopIsServedWhileTheLastClientsConnectStillRuns()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        var handler = new SlowConnectHandler();
        var server = new NamedPipeBrokerServer(handler, NullLogger<NamedPipeBrokerServer>.Instance, name);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = server.RunAsync(stopping.Token);
        try
        {
            await using (var first = new NamedPipeBrokerClient(name))
            {
                await first.OpenAsync(TimeSpan.FromSeconds(10), stopping.Token);
                _ = first.SetConnectionStateAsync(true, TimeSpan.FromSeconds(20), stopping.Token).AsTask()
                    .ContinueWith(_ => { }, TaskScheduler.Default);
                await handler.ConnectStarted.Task.WaitAsync(stopping.Token);
            }

            await using var second = new NamedPipeBrokerClient(name);
            await second.OpenAsync(TimeSpan.FromSeconds(10), stopping.Token);
            var snapshot = await second.GetSnapshotAsync(TimeSpan.FromSeconds(10), stopping.Token);

            Assert.Equal("snapshot", snapshot.StatusMessage);
            Assert.False(handler.ConnectFinished);
        }
        finally
        {
            handler.Release.TrySetResult();
            await stopping.CancelAsync();
            await serving;
        }
    }

    private static async Task<(InMemoryBrokerStateStore State, CatalogStore Catalog)> WantedRouteAsync()
    {
        var profileId = Guid.NewGuid();
        var catalog = new CatalogStore
        {
            Catalog = ProfileCatalog.Create(profileId, new PaqetFireSettings
            {
                ProfileName = "Test",
                ServerEndpoint = RuntimeFixture.Server,
                TransportKey = "test-key",
                LanSocksPassword = "local-password",
            }),
        };
        var state = new InMemoryBrokerStateStore();
        await state.SaveAsync(new BrokerRuntimeState { DesiredConnected = true, DesiredProfileId = profileId }, CancellationToken.None);
        return (state, catalog);
    }

    /// <summary>A long-running process at an engine's installed path, standing in for ProxiFyre.</summary>
    private static Process StartStandIn(string path)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), path, overwrite: true);
        return Process.Start(new ProcessStartInfo(path, "-t 127.0.0.1") { CreateNoWindow = true })!;
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    private sealed class SlowConnectHandler : IBrokerRequestHandler
    {
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ConnectFinished { get; private set; }

        public async ValueTask<BrokerResponse> HandleAsync(BrokerRequest request, CancellationToken cancellationToken)
        {
            if (request.Command == BrokerCommand.Connect)
            {
                ConnectStarted.TrySetResult();
                await Release.Task;
                ConnectFinished = true;
            }

            return BrokerResponse.Succeeded(request.RequestId,
                new BrokerSnapshot([], false, false, DateTimeOffset.UtcNow, StatusMessage: "snapshot"));
        }
    }

    private sealed class UnsupportedIpv6 : IIpv6CapabilityProbe
    {
        public ValueTask<Ipv6Capability> ProbeAsync(CancellationToken cancellationToken) => new(Ipv6Capability.Unsupported);
    }

    private sealed class GatedStateStore : IBrokerStateStore
    {
        private readonly InMemoryBrokerStateStore inner = new();

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BrokerRuntimeState Load() => inner.Load();

        public async ValueTask SaveAsync(BrokerRuntimeState state, CancellationToken cancellationToken)
        {
            if (state.Ipv6Support.Count > 0 && !Release.Task.IsCompleted)
            {
                Blocked.TrySetResult();
                await Release.Task;
            }

            await inner.SaveAsync(state, cancellationToken);
        }
    }

    /// <summary>A runtime whose first network adjustment is cancelled, as by a Disconnect.</summary>
    private sealed class NetworkRuntime : IPaqetFireRuntime
    {
        private int calls;

        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SecondCallResumed { get; private set; }

        public ValueTask HandleNetworkChangeAsync(bool resumed, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                FirstCall.TrySetResult();
                throw new OperationCanceledException();
            }

            SecondCallResumed = resumed;
            SecondCall.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public Guid? ActiveProfileId => null;

        public DateTimeOffset? NextSupervisionDue => null;
        public ValueTask<bool> SuperviseAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
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
        public ValueTask RestartTransportAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string> GetDiagnosticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
