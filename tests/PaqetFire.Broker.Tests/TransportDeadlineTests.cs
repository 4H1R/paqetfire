using System.IO.Pipes;
using PaqetFire.Broker.Ipc;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Configuration;
using PaqetFire.Desktop.Ipc;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class TransportDeadlineTests
{
    [Fact]
    public async Task CancelledInFlightFrameCannotBeReused()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        await using var server = CreateServer(name);
        await using var client = new NamedPipeBrokerClient(name);
        // Named-pipe setup can be delayed by cold or contended Windows runners.
        // Keep infrastructure timing separate from the request cancellation below.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.OpenAsync(TimeSpan.FromSeconds(10), deadline.Token);
        await connected;
        using var cancellation = new CancellationTokenSource();
        var request = client.SaveSettingsAsync(
            new PaqetFireSettings { ProfileName = new string('x', 48000) }, false,
            TimeSpan.FromSeconds(5), cancellation.Token).AsTask();
        // Observe the frame prefix, then stop reading its oversized payload.
        await server.ReadExactlyAsync(new byte[4], deadline.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetSnapshotAsync(TimeSpan.FromSeconds(1), deadline.Token));
    }

    [Fact]
    public async Task SlowReaderIsDisconnectedWithinWriteDeadline()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        await using var server = CreateServer(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await connected;
        await using var connection = new NamedPipeBrokerServer.ClientConnection(server, TimeSpan.FromMilliseconds(150));
        var serverHandle = server.SafePipeHandle;
        var message = BrokerServerMessage.FromEvent(new BrokerEvent(
            Guid.NewGuid(), IpcProtocol.Version, BrokerEventKind.Faulted, DateTimeOffset.UtcNow,
            Error: new BrokerError(BrokerErrorCode.InternalError, new string('x', 48000))));

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            // Exceed even a generously sized pipe buffer without reading the client.
            for (var index = 0; index < 100; index++)
                await connection.WriteAsync(message, deadline.Token);
        });
        Assert.True(serverHandle.IsClosed);
    }

    [Fact]
    public async Task RequestDeadlineIncludesWaitingForWriteLockAndResetsConnection()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        await using var server = CreateServer(name);
        await using var client = new NamedPipeBrokerClient(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.OpenAsync(TimeSpan.FromSeconds(10), deadline.Token);
        await connected;

        // Simulate an earlier write holding the serialization gate. This previously
        // bypassed the caller's timeout entirely, before any response was awaited.
        var gate = (SemaphoreSlim)typeof(NamedPipeBrokerClient)
            .GetField("writeLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;
        await gate.WaitAsync(deadline.Token);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await client.GetSnapshotAsync(TimeSpan.FromMilliseconds(150), deadline.Token));
        }
        finally
        {
            gate.Release();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetSnapshotAsync(TimeSpan.FromSeconds(1), deadline.Token));
    }

    private static NamedPipeServerStream CreateServer(string name) => new(
        name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
        1024, 1024);
}
