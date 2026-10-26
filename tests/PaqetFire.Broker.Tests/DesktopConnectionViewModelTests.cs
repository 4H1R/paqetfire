using PaqetFire.Core.Configuration;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Usage;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.Routing;
using PaqetFire.Core.RoutingData;
using PaqetFire.Desktop.Ipc;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;
using CoreConnectionState = PaqetFire.Core.Connections.ConnectionState;

namespace PaqetFire.Broker.Tests;

public sealed class DesktopConnectionViewModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(BrokerErrorCode.EngineFailure, FailureAction.CopyDiagnostics)]
    [InlineData(BrokerErrorCode.PrerequisiteMissing, FailureAction.OpenRequiredSoftware)]
    [InlineData(BrokerErrorCode.ConfigurationInvalid, FailureAction.ReviewProfile)]
    public async Task ConnectRejectionShowsTheBrokerMessageAndKeepsTheBrokerRunning(BrokerErrorCode code, FailureAction action)
    {
        var client = new FakeBrokerClient { Snapshot = Disconnected() };
        client.OnConnect = _ => throw new BrokerRequestException(new BrokerError(code, "Paqet exited: the transport key does not match."));
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());

        await model.ConnectAsync();

        Assert.Equal("Paqet exited: the transport key does not match.", model.ErrorMessage);
        Assert.Equal(action, model.FailureAction);
        Assert.Equal("Running", model.BrokerStatusText);
        Assert.True(model.IsBrokerReachable);
        Assert.NotEqual("BROKER UNAVAILABLE", model.StateLabel);
        Assert.Equal(PrimaryConnectionAction.Connect, model.PrimaryAction);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ProtocolErrorsKeepGenericText()
    {
        var client = new FakeBrokerClient { Snapshot = Disconnected() };
        client.OnConnect = _ => throw new BrokerRequestException(new BrokerError(BrokerErrorCode.InvalidRequest, "Connect-after-save is valid only for a settings request."));
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());

        await model.ConnectAsync();

        Assert.Equal("The broker could not understand the request.", model.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAndProfileRejectionsDoNotClaimTheBrokerIsUnavailable()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        client.OnVerify = () => throw new BrokerRequestException(new BrokerError(BrokerErrorCode.Busy, "A connection change is in progress."));
        client.OnManageProfiles = _ => throw new BrokerRequestException(new BrokerError(BrokerErrorCode.ConfigurationInvalid, "The route must be disconnected."));
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();

        Assert.Null(await model.VerifyConnectionAsync());
        Assert.Equal("A connection change is in progress.", model.ErrorMessage);
        Assert.Equal("Running", model.BrokerStatusText);

        Assert.False(await model.ManageProfileAsync(new ProfileAction(ProfileActionKind.Import, Payload: "{}")));
        Assert.Equal("The route must be disconnected.", model.ErrorMessage);
        Assert.Equal(BrokerConnectionState.Connected, model.ConnectionState);
    }

    [Fact]
    public async Task TransportFailureOffersRetryOfTheBroker()
    {
        var client = new FakeBrokerClient();
        client.OnOpen = () => throw new TimeoutException();
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());

        await model.RefreshAsync();

        Assert.Equal("BROKER UNAVAILABLE", model.StateLabel);
        Assert.False(model.IsBrokerReachable);
        Assert.Equal(PrimaryConnectionAction.RetryBroker, model.PrimaryAction);
        Assert.Equal("Retry", model.PrimaryActionText);
    }

    [Fact]
    public async Task EngineFaultMakesRetryPrimaryWithDiagnosticsAsShortcut()
    {
        var client = new FakeBrokerClient
        {
            Snapshot = Disconnected() with
            {
                Engines = [new EngineStatus(EngineKind.Paqet, EngineState.Faulted)],
            },
        };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());

        await model.RefreshAsync();

        Assert.Equal(BrokerConnectionState.Faulted, model.ConnectionState);
        Assert.Equal(PrimaryConnectionAction.Connect, model.PrimaryAction);
        Assert.Equal("Retry connection", model.PrimaryActionText);
        Assert.True(model.ShowDiagnosticsShortcut);
        Assert.Equal(FailureAction.CopyDiagnostics, model.FailureAction);
        Assert.True(model.HasError);
    }

    [Fact]
    public async Task ReadinessIsAnEnumNotDisplayText()
    {
        var client = new FakeBrokerClient { Snapshot = Disconnected() with { IsConfigured = false } };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());

        await model.RefreshAsync();
        Assert.Equal(ReadinessIssue.ProfileRequired, model.Readiness);
        Assert.Equal(PrimaryConnectionAction.SetUpProfile, model.PrimaryAction);

        client.Snapshot = Disconnected() with
        {
            Prerequisites = [new("npcap", "Npcap", false, "Missing")],
        };
        await model.RefreshAsync();
        Assert.Equal(ReadinessIssue.PrerequisiteMissing, model.Readiness);
        Assert.Equal(PrimaryConnectionAction.OpenRequiredSoftware, model.PrimaryAction);
    }

    [Fact]
    public async Task LiveStatsPollingDoesNotBlockUserOperations()
    {
        var statsGate = new TaskCompletionSource<LiveConnectionStats>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBrokerClient { Snapshot = Connected() };
        client.OnLiveStats = () => statsGate.Task;
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        await WaitUntilAsync(() => client.LiveStatsCalls > 0);

        // The stats request is still outstanding; saving must go straight through.
        Assert.True(await model.SaveSettingsAsync(new PaqetFireSettings()).WaitAsync(Wait));
        Assert.NotNull(await model.VerifyConnectionAsync().WaitAsync(Wait));
        Assert.Equal(1, client.SaveCount);
        statsGate.SetResult(new LiveConnectionStats(DateTimeOffset.UtcNow, IsConnected: true));
    }

    [Fact]
    public async Task PausedLiveStatsStopPollingWithoutResettingTheSession()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        await WaitUntilAsync(() => client.LiveStatsCalls > 1);

        model.SetLiveStatsPaused(true);
        await Task.Delay(50);
        var paused = client.LiveStatsCalls;
        await Task.Delay(100);
        Assert.Equal(paused, client.LiveStatsCalls);

        model.SetLiveStatsPaused(false);
        await WaitUntilAsync(() => client.LiveStatsCalls > paused);
    }

    [Fact]
    public async Task LostPipeIsReopenedWithBackoffAndResynced()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        var opensBefore = client.OpenCount;

        var failures = 2;
        client.OnOpen = () =>
        {
            if (failures-- > 0)
            {
                throw new IOException("Pipe not available.");
            }
        };
        client.RaiseConnectionLost();

        await WaitUntilAsync(() => client.OpenCount >= opensBefore + 3 && !model.IsReconnecting);
        Assert.Equal(BrokerConnectionState.Connected, model.ConnectionState);
        Assert.Equal("Running", model.BrokerStatusText);
    }

    [Fact]
    public async Task HeartbeatReconnectsWhenThePipeWasNeverOpen()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        var down = true;
        client.OnOpen = () =>
        {
            if (down)
            {
                throw new TimeoutException();
            }
        };
        client.OnSnapshot = () => client.IsOpen ? client.Snapshot : throw new InvalidOperationException("Not connected.");
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        Assert.Equal("BROKER UNAVAILABLE", model.StateLabel);

        down = false;
        await WaitUntilAsync(() => model.ConnectionState == BrokerConnectionState.Connected);
    }

    [Fact]
    public async Task ProgressDrivesTheStepperAndCancelSendsDisconnect()
    {
        var connectGate = new TaskCompletionSource<BrokerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBrokerClient { Snapshot = Disconnected() };
        client.OnConnect = connected => connected ? connectGate.Task : Task.FromResult(Disconnected());
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();

        var connect = model.ConnectAsync();
        // An intermediate "nothing started yet" snapshot must not flip the card back.
        client.Publish(Disconnected());
        Assert.Equal(BrokerConnectionState.Connecting, model.ConnectionState);

        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-12);
        client.Publish(Disconnected() with
        {
            Progress = new ConnectionProgress(ConnectionStage.StartingTransport, startedAt, startedAt),
        });
        Assert.True(model.HasProgress);
        Assert.Equal(6, model.ProgressSteps.Count);
        Assert.Equal(ProgressStepState.Done, model.ProgressSteps[1].State);
        Assert.Equal(ProgressStepState.Active, model.ProgressSteps[2].State);
        Assert.StartsWith("Step 3 of 6 · Starting Paqet transport", model.ProgressSummary);
        Assert.True(model.CanCancelConnect);

        await model.CancelConnectAsync().WaitAsync(Wait);
        Assert.Contains(false, client.ConnectionRequests);
        Assert.False(model.CanCancelConnect);

        connectGate.SetResult(Disconnected());
        await connect.WaitAsync(Wait);
        Assert.False(model.HasProgress);
        Assert.Equal(BrokerConnectionState.Disconnected, model.ConnectionState);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task TransportRecoveryKeepsTheRouteConnected()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();

        var now = DateTimeOffset.UtcNow;
        client.Snapshot = Connected() with { Progress = new ConnectionProgress(ConnectionStage.RecoveringTransport, now, now, 2) };
        client.Publish(client.Snapshot);

        Assert.Equal(BrokerConnectionState.Connected, model.ConnectionState);
        Assert.True(model.IsTunnelStalled);
        Assert.Equal("Reconnecting transport (attempt 2)", Assert.Single(model.ProgressSteps).Label);
        Assert.False(model.CanCancelConnect);
        Assert.True(model.CanDisconnect);
    }

    [Fact]
    public async Task RefreshDoesNotReplaceAnActiveRouteWithCheckingSystem()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        Assert.Equal("ROUTING ACTIVE", model.StateLabel);

        var gate = new TaskCompletionSource<BrokerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnSnapshot = () => gate.Task.Result;
        var refresh = Task.Run(() => model.RefreshAsync());
        await Task.Delay(50);
        Assert.Equal("ROUTING ACTIVE", model.StateLabel);
        Assert.Equal(BrokerConnectionState.Connected, model.ConnectionState);
        gate.SetResult(Connected());
        await refresh.WaitAsync(Wait);
    }

    [Fact]
    public async Task RoutingStatusReflectsTheSelectedApplicationScope()
    {
        var settings = new PaqetFireSettings
        {
            RoutingMode = RoutingMode.SelectedApplications,
            SelectedApplications = ["chrome.exe", "telegram.exe"],
            TransportKey = "k",
        };
        var client = new FakeBrokerClient { Snapshot = Connected() with { Settings = PaqetFireSettingsView.FromSettings(settings) } };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());

        await model.RefreshAsync();

        Assert.Equal("2 selected apps · loop exclusions locked", model.RoutingStatusText);
        Assert.True(model.CanVerify);
    }

    [Fact]
    public async Task VerifyIsUnavailableWhileDisconnected()
    {
        var client = new FakeBrokerClient { Snapshot = Disconnected() };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        Assert.False(model.CanVerify);
    }

    [Fact]
    public async Task BusyRejectionIsReportedSeparatelyFromBrokerErrors()
    {
        var gate = new TaskCompletionSource<BrokerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBrokerClient { Snapshot = Disconnected() };
        client.OnSave = () => gate.Task;
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        var first = model.SaveSettingsAsync(new PaqetFireSettings());

        Assert.False(await model.ManageProfileAsync(new ProfileAction(ProfileActionKind.Activate, Guid.NewGuid())));
        Assert.True(model.LastRequestWasBusy);
        Assert.False(model.HasError);

        gate.SetResult(Disconnected());
        Assert.True(await first);
    }

    [Fact]
    public async Task TransportRecoveryAsTheBrokerReportsItIsNotAFreshConnect()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();

        // Paqet is down, so the chain is not routing and the broker reports Connecting.
        var now = DateTimeOffset.UtcNow;
        client.Snapshot = Connected() with
        {
            Engines =
            [
                new EngineStatus(EngineKind.Paqet, EngineState.Starting),
                new EngineStatus(EngineKind.Xray, EngineState.Running),
                new EngineStatus(EngineKind.ProxiFyre, EngineState.Running),
            ],
            IsRouting = false,
            ConnectionState = CoreConnectionState.Connecting,
            Progress = new ConnectionProgress(ConnectionStage.RecoveringTransport, now, now, 3),
        };
        client.Publish(client.Snapshot);

        Assert.Equal(BrokerConnectionState.Connected, model.ConnectionState);
        Assert.Equal("RECONNECTING TRANSPORT", model.StateLabel);
        Assert.Equal("Reconnecting transport (attempt 3)", model.StatusText);
        Assert.True(model.IsTunnelStalled);
        Assert.False(model.CanCancelConnect);
        Assert.Equal(PrimaryConnectionAction.Disconnect, model.PrimaryAction);
        Assert.Equal("Disconnect", model.PrimaryActionText);

        // The tunnel test after the restart still belongs to the recovery.
        client.Snapshot = Connected() with { Progress = new ConnectionProgress(ConnectionStage.ProbingTunnel, now, now, 3) };
        client.Publish(client.Snapshot);

        Assert.Equal(BrokerConnectionState.Connected, model.ConnectionState);
        Assert.False(model.CanCancelConnect);
        Assert.Equal(PrimaryConnectionAction.Disconnect, model.PrimaryAction);
    }

    [Fact]
    public async Task BrokerNoticesAreInformationalAndStayDismissed()
    {
        const string paused = "Mobile hotspot is off, so the hotspot share is paused until it is turned back on.";
        var client = new FakeBrokerClient { Snapshot = Connected() with { OperationWarning = paused } };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();

        Assert.Equal(paused, model.Notice);
        Assert.False(model.HasError);

        model.DismissNotice();
        client.Publish(client.Snapshot);
        Assert.False(model.HasNotice);

        const string ipv6 = "The Paqet server has no IPv6 route, so IPv6 DNS answers are skipped and applications use IPv4.";
        client.Publish(client.Snapshot with { OperationWarning = ipv6 });
        Assert.Equal(ipv6, model.Notice);

        client.Publish(client.Snapshot with { OperationWarning = null });
        Assert.False(model.HasNotice);
        client.Publish(client.Snapshot with { OperationWarning = paused });
        Assert.Equal(paused, model.Notice);
    }

    [Fact]
    public async Task SavingWhileANoticeIsShownIsNotAFailure()
    {
        const string paused = "Mobile hotspot is off, so the hotspot share is paused until it is turned back on.";
        var client = new FakeBrokerClient { Snapshot = Connected() with { OperationWarning = paused } };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();

        Assert.True(await model.SaveSettingsAsync(new PaqetFireSettings()));

        Assert.False(model.HasError);
        Assert.Equal(paused, model.Notice);
    }

    [Fact]
    public async Task ARouteThatDidNotRestartAfterSavingIsAnError()
    {
        const string failed = "The profile was saved, but the route could not be activated. Paqet exited.";
        var client = new FakeBrokerClient { Snapshot = Connected() };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        client.OnSave = () => Task.FromResult(Disconnected() with { OperationWarning = failed });

        Assert.True(await model.SaveSettingsAsync(new PaqetFireSettings()));

        Assert.Equal(failed, model.ErrorMessage);
        Assert.False(model.HasNotice);
    }

    [Fact]
    public async Task RoutingDataActionsUpdateTheReportedStatus()
    {
        var bundled = new RoutingDataStatus(RoutingDataOrigin.Bundled, "Xray 26.3.27", null, null, true, false, false);
        var client = new FakeBrokerClient { Snapshot = Disconnected() with { RoutingData = bundled } };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        await model.RefreshAsync();
        Assert.Equal(bundled, model.RoutingData);

        client.OnManageRoutingData = _ => client.Snapshot with { RoutingData = bundled with { IsUpdating = true } };
        Assert.Null(await model.ManageRoutingDataAsync(RoutingDataAction.UpdateNow));
        Assert.True(model.RoutingData!.IsUpdating);
        Assert.Equal([RoutingDataAction.UpdateNow], client.RoutingDataRequests);

        client.OnManageRoutingData = _ => throw new BrokerRequestException(
            new BrokerError(BrokerErrorCode.EngineFailure, "Routing data updates are not available in this broker."));
        Assert.Equal("Routing data updates are not available in this broker.",
            await model.ManageRoutingDataAsync(RoutingDataAction.DisableAutomaticUpdates));
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task SaveWithoutTheStoredProfileInTheReplyIsNotTreatedAsStored()
    {
        var client = new FakeBrokerClient { Snapshot = Disconnected(), EchoSavedSettings = false };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());

        Assert.False(await model.SaveSettingsAsync(new PaqetFireSettings { TransportKey = "first-key" }));

        Assert.Equal(SaveOutcome.NotStored, model.LastSaveOutcome);
        Assert.Contains("still pending", model.ErrorMessage);
    }

    [Fact]
    public async Task CancellingTheConnectionAfterAStoredSaveKeepsTheSave()
    {
        var gate = new TaskCompletionSource<BrokerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBrokerClient { Snapshot = Disconnected() };
        client.OnSave = () => gate.Task;
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        var save = model.SaveSettingsAsync(new PaqetFireSettings { TransportKey = "first-key" }, connectAfterSave: true);
        Assert.True(model.CanCancelConnect);

        var cancel = model.CancelConnectAsync();
        gate.SetResult(Disconnected());
        await cancel.WaitAsync(Wait);

        Assert.True(await save.WaitAsync(Wait));
        Assert.Equal(SaveOutcome.SavedConnectionCancelled, model.LastSaveOutcome);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task ASaveCancelledBeforeItWasStoredKeepsThePendingChanges()
    {
        var gate = new TaskCompletionSource<BrokerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBrokerClient { Snapshot = Disconnected() };
        client.OnSave = () => gate.Task;
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), FastOptions());
        var save = model.SaveSettingsAsync(new PaqetFireSettings(), connectAfterSave: true);

        var cancel = model.CancelConnectAsync();
        gate.SetException(new BrokerRequestException(new BrokerError(BrokerErrorCode.Busy, "The save was cancelled.")));
        await cancel.WaitAsync(Wait);

        Assert.False(await save.WaitAsync(Wait));
        Assert.Equal(SaveOutcome.Cancelled, model.LastSaveOutcome);
        Assert.Equal("Save was cancelled — nothing was stored. Your changes are still pending.", model.ErrorMessage);
    }

    [Fact]
    public async Task HeartbeatStartsOnceHoweverOftenRefreshRuns()
    {
        var client = new FakeBrokerClient { Snapshot = Connected() };
        var options = FastOptions() with { HeartbeatInterval = TimeSpan.FromMilliseconds(200), LiveStatsInterval = TimeSpan.FromHours(1) };
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher(), options);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => model.RefreshAsync())));
        var afterRefresh = client.SnapshotCalls;

        await Task.Delay(500);

        // One loop ticks about twice in 500 ms; eight loops would tick about sixteen times.
        Assert.InRange(client.SnapshotCalls - afterRefresh, 1, 4);
    }

    private static ConnectionViewModelOptions FastOptions() => new()
    {
        LiveStatsInterval = TimeSpan.FromMilliseconds(10),
        HeartbeatInterval = TimeSpan.FromMilliseconds(20),
        ReconnectBackoff = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)],
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    internal static BrokerSnapshot Disconnected() => new(
        [
            new EngineStatus(EngineKind.Paqet, EngineState.Stopped),
            new EngineStatus(EngineKind.Xray, EngineState.Stopped),
            new EngineStatus(EngineKind.ProxiFyre, EngineState.Stopped),
        ],
        IsRouting: false,
        IsKillSwitchEnabled: false,
        DateTimeOffset.UtcNow,
        IsConfigured: true,
        ConnectionState: CoreConnectionState.Disconnected);

    internal static BrokerSnapshot Connected() => new(
        [
            new EngineStatus(EngineKind.Paqet, EngineState.Running),
            new EngineStatus(EngineKind.Xray, EngineState.Running),
            new EngineStatus(EngineKind.ProxiFyre, EngineState.Running),
        ],
        IsRouting: true,
        IsKillSwitchEnabled: false,
        DateTimeOffset.UtcNow,
        IsConfigured: true,
        ConnectionState: CoreConnectionState.Connected);

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        private readonly object sync = new();
        public bool HasThreadAccess => Monitor.IsEntered(sync);

        public bool TryEnqueue(Action action)
        {
            lock (sync)
            {
                action();
            }

            return true;
        }
    }

    private sealed class FakeBrokerClient : IBrokerClient
    {
        private int liveStatsCalls;
        private int openCount;
        private int snapshotCalls;

        public event Action<BrokerEvent>? EventReceived;
        public event Action? ConnectionLost;

        public BrokerSnapshot Snapshot { get; set; } = Disconnected();
        public Action? OnOpen { get; set; }
        public Func<BrokerSnapshot>? OnSnapshot { get; set; }
        public Func<bool, Task<BrokerSnapshot>>? OnConnect { get; set; }
        public Func<Task<BrokerSnapshot>>? OnSave { get; set; }
        public Func<BrokerSnapshot>? OnVerify { get; set; }
        public Func<ProfileAction, BrokerSnapshot>? OnManageProfiles { get; set; }
        public Func<RoutingDataAction, BrokerSnapshot>? OnManageRoutingData { get; set; }
        public List<RoutingDataAction> RoutingDataRequests { get; } = [];
        public Func<Task<LiveConnectionStats>>? OnLiveStats { get; set; }
        public List<bool> ConnectionRequests { get; } = [];
        public int SaveCount { get; private set; }
        public int LiveStatsCalls => Volatile.Read(ref liveStatsCalls);
        public int OpenCount => Volatile.Read(ref openCount);
        public bool IsOpen { get; private set; }

        public void RaiseConnectionLost()
        {
            IsOpen = false;
            ConnectionLost?.Invoke();
        }

        public void Publish(BrokerSnapshot snapshot) => EventReceived?.Invoke(new BrokerEvent(
            Guid.NewGuid(), IpcProtocol.Version, BrokerEventKind.SnapshotChanged, DateTimeOffset.UtcNow, snapshot));

        public Task OpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref openCount);
            OnOpen?.Invoke();
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public int SnapshotCalls => Volatile.Read(ref snapshotCalls);

        public ValueTask<BrokerSnapshot> GetSnapshotAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref snapshotCalls);
            return new(OnSnapshot?.Invoke() ?? Snapshot);
        }

        public ValueTask<BrokerSnapshot> SetConnectionStateAsync(bool connected, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (ConnectionRequests)
            {
                ConnectionRequests.Add(connected);
            }

            return OnConnect is null ? new(connected ? Connected() : Disconnected()) : new(OnConnect(connected));
        }

        /// <summary>False to answer a save without the stored profile (an unconfirmed save).</summary>
        public bool EchoSavedSettings { get; set; } = true;

        public ValueTask<BrokerSnapshot> SaveSettingsAsync(PaqetFireSettings settings, bool connectAfterSave, TimeSpan timeout, CancellationToken cancellationToken)
        {
            SaveCount++;
            return new(StoredAsync(settings));
        }

        private async Task<BrokerSnapshot> StoredAsync(PaqetFireSettings settings)
        {
            var snapshot = OnSave is null ? Snapshot : await OnSave();
            return EchoSavedSettings
                ? snapshot with { Settings = PaqetFireSettingsView.FromSettings(settings) }
                : snapshot with { Settings = null };
        }

        public ValueTask<BrokerSnapshot> VerifyConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            new(OnVerify?.Invoke() ?? Snapshot with
            {
                Verification = new PaqetFire.Core.Diagnostics.ConnectionVerificationReport(
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
            });

        public ValueTask<BrokerSnapshot> ManageProfilesAsync(ProfileAction action, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(OnManageProfiles?.Invoke(action) ?? Snapshot);

        public ValueTask<string> ExportProfilesAsync(TimeSpan timeout, CancellationToken cancellationToken) => new("{}");

        public ValueTask<BrokerSnapshot> ManageRoutingDataAsync(RoutingDataAction action, TimeSpan timeout, CancellationToken cancellationToken)
        {
            RoutingDataRequests.Add(action);
            return new(OnManageRoutingData?.Invoke(action) ?? Snapshot);
        }

        public List<ExitEngineAction> ExitEngineRequests { get; } = [];

        public Func<ExitEngineAction, BrokerSnapshot>? OnManageExitEngine { get; set; }

        public ValueTask<BrokerSnapshot> ManageExitEngineAsync(ExitEngineAction action, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ExitEngineRequests.Add(action);
            return new(OnManageExitEngine?.Invoke(action) ?? Snapshot);
        }

        public ValueTask<LiveConnectionStats> GetLiveStatsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref liveStatsCalls);
            return OnLiveStats is null
                ? new(new LiveConnectionStats(DateTimeOffset.UtcNow, IsConnected: true))
                : new(OnLiveStats());
        }

        public ValueTask<string> GetDiagnosticsAsync(TimeSpan timeout, CancellationToken cancellationToken) => new("diagnostics");
        public ValueTask<DataUsageReport> GetDataUsageAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            new(new DataUsageReport(DateOnly.MinValue, []));
        public ValueTask<DataUsageReport> ResetDataUsageAsync(DataUsageReset reset, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(new DataUsageReport(DateOnly.MinValue, []));
    }
}
