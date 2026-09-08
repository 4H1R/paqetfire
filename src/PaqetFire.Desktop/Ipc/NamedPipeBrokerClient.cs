using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Desktop.Ipc;

public sealed class NamedPipeBrokerClient : IBrokerClient, IAsyncDisposable
{
    private readonly string pipeName;

    public NamedPipeBrokerClient() : this(IpcProtocol.PipeName) { }

    internal NamedPipeBrokerClient(string pipeName)
    {
        this.pipeName = pipeName;
    }

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<BrokerResponse>> pendingRequests = new();
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim writeLock = new(1, 1);

    private NamedPipeClientStream? pipe;
    private CancellationTokenSource? connectionCancellation;
    private Task? receiveTask;
    private bool disposed;

    public event Action<BrokerEvent>? EventReceived;

    public async Task OpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ValidateTimeout(timeout);
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pipe is { IsConnected: true })
            {
                return;
            }

            await ResetConnectionAsync().ConfigureAwait(false);

            var candidate = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCancellation.CancelAfter(timeout);

            try
            {
                await candidate.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            pipe = candidate;
            connectionCancellation = new CancellationTokenSource();
            receiveTask = ReceiveLoopAsync(candidate, connectionCancellation.Token);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ResetConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask<BrokerSnapshot> GetSnapshotAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerCommand.GetSnapshot, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<BrokerSnapshot> SetConnectionStateAsync(
        bool connected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await SendAsync(
                connected ? BrokerCommand.Connect : BrokerCommand.Disconnect,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<BrokerSnapshot> SaveSettingsAsync(
        PaqetFireSettings settings,
        bool connectAfterSave,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return await SendAsync(
                BrokerCommand.SaveSettings,
                timeout,
                cancellationToken,
                settings,
                connectAfterSave)
            .ConfigureAwait(false);
    }

    public async ValueTask<BrokerSnapshot> VerifyConnectionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await SendAsync(BrokerCommand.VerifyConnection, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<BrokerSnapshot> ManageProfilesAsync(
        ProfileAction action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return await SendAsync(
                BrokerCommand.ManageProfiles,
                timeout,
                cancellationToken,
                profileAction: action)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ExportProfilesAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var response = await SendResponseAsync(
                BrokerCommand.ExportProfiles,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        return response.ExportedProfiles ??
            throw new InvalidDataException("The broker profile export was empty.");
    }

    private async ValueTask<BrokerSnapshot> SendAsync(
        BrokerCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        PaqetFireSettings? settings = null,
        bool connectAfterSave = false,
        ProfileAction? profileAction = null)
    {
        var response = await SendResponseAsync(
                command,
                timeout,
                cancellationToken,
                settings,
                connectAfterSave,
                profileAction)
            .ConfigureAwait(false);
        return response.Snapshot ??
            throw new InvalidDataException("The broker snapshot response was empty.");
    }

    private async ValueTask<BrokerResponse> SendResponseAsync(
        BrokerCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        PaqetFireSettings? settings = null,
        bool connectAfterSave = false,
        ProfileAction? profileAction = null)
    {
        ValidateTimeout(timeout);
        ObjectDisposedException.ThrowIf(disposed, this);

        var currentPipe = pipe;
        if (currentPipe is not { IsConnected: true })
        {
            throw new InvalidOperationException("The broker client is not connected.");
        }

        var request = new BrokerRequest(
            Guid.NewGuid(),
            IpcProtocol.Version,
            command,
            settings,
            connectAfterSave,
            profileAction);
        var completion = new TaskCompletionSource<BrokerResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(request.RequestId, completion))
        {
            throw new InvalidOperationException("The broker request ID is already pending.");
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            BrokerResponse response;
            try
            {
                await WriteRequestAsync(currentPipe, request, deadline.Token).ConfigureAwait(false);
                response = await completion.Task.WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation may interrupt a frame after its prefix was sent.
                // Never send another request on that potentially truncated stream.
                await ResetIfCurrentAsync(currentPipe).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The broker request exceeded its deadline.");
            }

            if (!response.Success)
            {
                throw new BrokerRequestException(
                    response.Error ?? new BrokerError(
                        BrokerErrorCode.InternalError,
                        "The broker rejected the request without an error."));
            }

            return response;
        }
        finally
        {
            pendingRequests.TryRemove(request.RequestId, out _);
        }
    }

    private async Task ResetIfCurrentAsync(NamedPipeClientStream expectedPipe)
    {
        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(pipe, expectedPipe))
            {
                await ResetConnectionAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await ResetConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleLock.Release();
            lifecycleLock.Dispose();
            writeLock.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(
        NamedPipeClientStream connectedPipe,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && connectedPipe.IsConnected)
            {
                var message = await ReadMessageAsync(connectedPipe, cancellationToken)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    failure = new IOException("The broker closed the IPC connection.");
                    break;
                }

                ValidateServerMessage(message);
                if (message.MessageType == BrokerMessageType.Response)
                {
                    var response = message.Response!;
                    if (pendingRequests.TryRemove(response.RequestId, out var completion))
                    {
                        completion.TrySetResult(response);
                    }
                }
                else
                {
                    RaiseEvent(message.Event!);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException)
        {
            failure = exception;
        }
        finally
        {
            FailPendingRequests(failure ?? new IOException("The broker connection ended."));
        }
    }

    private async ValueTask WriteRequestAsync(
        NamedPipeClientStream connectedPipe,
        BrokerRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        if (payload.Length > IpcProtocol.MaxMessageSizeBytes)
        {
            throw new InvalidDataException("The IPC request exceeds the maximum message size.");
        }

        var lengthBuffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connectedPipe.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
            await connectedPipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await connectedPipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async ValueTask<BrokerServerMessage?> ReadMessageAsync(
        NamedPipeClientStream connectedPipe,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        var bytesRead = await ReadPrefixAsync(connectedPipe, lengthBuffer, cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead == 0)
        {
            return null;
        }

        if (bytesRead != lengthBuffer.Length)
        {
            throw new InvalidDataException("The IPC message length prefix was incomplete.");
        }

        var messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (messageLength is <= 0 or > IpcProtocol.MaxMessageSizeBytes)
        {
            throw new InvalidDataException("The IPC message size is invalid.");
        }

        var payload = new byte[messageLength];
        await connectedPipe.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BrokerServerMessage>(payload, SerializerOptions)
            ?? throw new JsonException("The IPC server message was empty.");
    }

    private static async ValueTask<int> ReadPrefixAsync(
        PipeStream connectedPipe,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await connectedPipe.ReadAsync(buffer[totalRead..], cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private static void ValidateServerMessage(BrokerServerMessage message)
    {
        var valid = message.MessageType switch
        {
            BrokerMessageType.Response => message.Response is
            { ProtocolVersion: IpcProtocol.Version } && message.Event is null,
            BrokerMessageType.Event => message.Event is
            { ProtocolVersion: IpcProtocol.Version } && message.Response is null,
            _ => false,
        };

        if (!valid)
        {
            throw new InvalidDataException("The broker returned an invalid message envelope.");
        }
    }

    private async Task ResetConnectionAsync()
    {
        var cancellation = connectionCancellation;
        var currentPipe = pipe;
        var currentReceiveTask = receiveTask;

        connectionCancellation = null;
        pipe = null;
        receiveTask = null;

        cancellation?.Cancel();
        if (currentPipe is not null)
        {
            await currentPipe.DisposeAsync().ConfigureAwait(false);
        }

        if (currentReceiveTask is not null)
        {
            try
            {
                await currentReceiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal disconnect.
            }
        }

        cancellation?.Dispose();
        FailPendingRequests(new IOException("The broker client disconnected."));
    }

    private void RaiseEvent(BrokerEvent brokerEvent)
    {
        var handlers = EventReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<BrokerEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(brokerEvent);
            }
            catch
            {
                // Subscriber failures must not terminate the transport receive loop.
            }
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var (requestId, completion) in pendingRequests.ToArray())
        {
            if (pendingRequests.TryRemove(requestId, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "A finite positive timeout is required.");
        }
    }
}
