using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class HealthMonitorTests
{
    [Fact]
    public async Task HealthChecksContinueAndCoalesceWhenPublisherDoesNotConsume()
    {
        var runtime = new ChangingRuntime();
        using var worker = new BrokerWorker(NullLogger<BrokerWorker>.Instance, null!, runtime, null!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitoring = worker.MonitorAsync(cancellation.Token, TimeSpan.FromMilliseconds(20));
        await runtime.FiveChecks.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await monitoring;

        var channel = (Channel<BrokerEvent>)typeof(BrokerWorker)
            .GetField("publications", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(worker)!;
        Assert.True(channel.Reader.TryRead(out var newest));
        Assert.True(int.Parse(newest.Snapshot!.StatusMessage!) >= 5);
        Assert.False(channel.Reader.TryRead(out _));
    }

    private sealed class ChangingRuntime : IPaqetFireRuntime
    {
        private int checks;
        public TaskCompletionSource FiveChecks { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<BrokerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref checks);
            if (count == 5) FiveChecks.TrySetResult();
            return ValueTask.FromResult(new BrokerSnapshot([], false, false, DateTimeOffset.UtcNow,
                StatusMessage: count.ToString()));
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<BrokerSnapshot> ConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> DisconnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> VerifyConnectionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> ManageProfilesAsync(ProfileAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string> ExportProfilesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<BrokerSnapshot> SaveSettingsAsync(PaqetFireSettings settings, bool connectAfterSave,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
