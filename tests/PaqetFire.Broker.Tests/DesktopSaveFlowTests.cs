using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Usage;
using PaqetFire.Desktop.Ipc;
using PaqetFire.Desktop.ViewModels;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.RoutingData;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class DesktopSaveFlowTests
{
    [Fact]
    public async Task RepeatedSaveIsRejectedWithoutQueueingAnotherBrokerMutation()
    {
        var client = new SaveClient();
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher());
        var first = model.SaveSettingsAsync(new PaqetFireSettings());
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(model.IsBusy);
        Assert.False(await model.SaveSettingsAsync(new PaqetFireSettings()).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, client.SaveCount);
        client.Completion.SetResult(Snapshot());
        Assert.True(await first);
        Assert.False(model.IsBusy);
        Assert.Equal(1, client.SaveCount);
    }

    [Fact]
    public async Task RejectionClearsBusyAndRetryClearsPriorError()
    {
        var client = new SaveClient();
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher());
        client.Completion.SetException(new BrokerRequestException(
            new BrokerError(BrokerErrorCode.ConfigurationInvalid, "Rejected")));
        Assert.False(await model.SaveSettingsAsync(new PaqetFireSettings()));
        Assert.True(model.HasError);
        Assert.False(model.IsBusy);
        client.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = model.SaveSettingsAsync(new PaqetFireSettings());
        Assert.False(model.HasError);
        client.Completion.SetResult(Snapshot());
        Assert.True(await retry);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
        Assert.Equal(2, client.SaveCount);
    }

    [Fact]
    public async Task SavedConfigurationWithConnectionWarningRemainsSuccessfulButVisible()
    {
        var client = new SaveClient();
        await using var model = new ConnectionViewModel(client, new ImmediateDispatcher());
        client.Completion.SetResult(Snapshot() with { OperationWarning = "Connection could not start." });
        Assert.True(await model.SaveSettingsAsync(new PaqetFireSettings(), connectAfterSave: true));
        Assert.Equal("Connection could not start.", model.ErrorMessage);
        Assert.False(model.IsBusy);
    }

    private static BrokerSnapshot Snapshot() => new([], false, false, DateTimeOffset.UtcNow, IsConfigured: true);

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }

    private sealed class SaveClient : IBrokerClient
    {
        public event Action<BrokerEvent>? EventReceived { add { } remove { } }
        public event Action? ConnectionLost { add { } remove { } }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BrokerSnapshot> Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCount { get; private set; }
        public Task OpenAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask<BrokerSnapshot> GetSnapshotAsync(TimeSpan timeout, CancellationToken cancellationToken) => new(Snapshot());
        public ValueTask<BrokerSnapshot> SetConnectionStateAsync(bool connected, TimeSpan timeout, CancellationToken cancellationToken) => new(Snapshot());
        public ValueTask<BrokerSnapshot> VerifyConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken) => new(Snapshot());
        public ValueTask<BrokerSnapshot> ManageProfilesAsync(ProfileAction action, TimeSpan timeout, CancellationToken cancellationToken) => new(Snapshot());
        public ValueTask<string> ExportProfilesAsync(TimeSpan timeout, CancellationToken cancellationToken) => new("{}");

        public ValueTask<BrokerSnapshot> ManageRoutingDataAsync(RoutingDataAction action, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> ManageExitEngineAsync(ExitEngineAction action, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<LiveConnectionStats> GetLiveStatsAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            new(new LiveConnectionStats(DateTimeOffset.UtcNow, IsConnected: false));
        public ValueTask<string> GetDiagnosticsAsync(TimeSpan timeout, CancellationToken cancellationToken) => new("diagnostics");
        public ValueTask<DataUsageReport> GetDataUsageAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            new(new DataUsageReport(DateOnly.MinValue, []));
        public ValueTask<DataUsageReport> ResetDataUsageAsync(DataUsageReset reset, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(new DataUsageReport(DateOnly.MinValue, []));
        public ValueTask<BrokerSnapshot> SaveSettingsAsync(PaqetFireSettings settings, bool connectAfterSave, TimeSpan timeout, CancellationToken cancellationToken)
        {
            SaveCount++;
            Started.TrySetResult();
            return new(StoredAsync(settings));
        }

        // A real broker answers with the profile it stored.
        private async Task<BrokerSnapshot> StoredAsync(PaqetFireSettings settings)
        {
            var snapshot = await Completion.Task;
            return snapshot.Settings is null
                ? snapshot with { Settings = PaqetFireSettingsView.FromSettings(settings) }
                : snapshot;
        }
    }
}
