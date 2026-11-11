using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Configuration;

/// <summary>What the broker must remember across its own restarts, independent of profiles.</summary>
public sealed record BrokerRuntimeState
{
    /// <summary>The user's last explicit choice was to connect, so a restarted broker reconnects.</summary>
    public bool DesiredConnected { get; init; }

    public Guid? DesiredProfileId { get; init; }

    /// <summary>Server host name to the IPv4 address that last resolved for it.</summary>
    public IReadOnlyDictionary<string, string> ResolvedServers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Server endpoint to whether its Paqet server reached an IPv6 destination.</summary>
    public IReadOnlyDictionary<string, bool> Ipv6Support { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The engines' loopback ports, kept so a restarted broker reuses them: a ProxiFyre left
    /// running for the kill switch still sends captured traffic to the same Xray port.
    /// </summary>
    public LocalEnginePorts? LocalPorts { get; init; }
}

public interface IBrokerStateStore
{
    BrokerRuntimeState Load();

    ValueTask SaveAsync(BrokerRuntimeState state, CancellationToken cancellationToken);
}

public sealed class InMemoryBrokerStateStore : IBrokerStateStore
{
    private BrokerRuntimeState state = new();

    public BrokerRuntimeState Load() => state;

    public ValueTask SaveAsync(BrokerRuntimeState state, CancellationToken cancellationToken)
    {
        this.state = state;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Persists <see cref="BrokerRuntimeState"/> beside the other machine settings. The whole
/// document is machine-scope DPAPI ciphertext in a SYSTEM/Administrators-only file because
/// it names the servers this machine connects to.
/// </summary>
public sealed class BrokerStateStore(string path, ProtectedDirectory? directory = null) : IBrokerStateStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PaqetFire.BrokerState.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BrokerRuntimeState Load()
    {
        try
        {
            ProtectedDirectory.EnsureFor(directory, _path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BrokerRuntimeState();
        }

        if (!File.Exists(_path))
        {
            return new BrokerRuntimeState();
        }

        try
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
            try
            {
                var stored = JsonSerializer.Deserialize<StoredState>(clear, JsonOptions);
                return stored is null
                    ? new BrokerRuntimeState()
                    : new BrokerRuntimeState
                    {
                        DesiredConnected = stored.DesiredConnected,
                        DesiredProfileId = stored.DesiredProfileId,
                        ResolvedServers = new Dictionary<string, string>(
                            stored.ResolvedServers ?? [], StringComparer.OrdinalIgnoreCase),
                        Ipv6Support = new Dictionary<string, bool>(
                            stored.Ipv6Support ?? [], StringComparer.OrdinalIgnoreCase),
                        LocalPorts = stored.LocalPorts is { IsValid: true } ports ? ports : null,
                    };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Losing this state only skips one automatic reconnect or a cached address.
            return new BrokerRuntimeState();
        }
    }

    public async ValueTask SaveAsync(BrokerRuntimeState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var clear = JsonSerializer.SerializeToUtf8Bytes(new StoredState(
            state.DesiredConnected,
            state.DesiredProfileId,
            new Dictionary<string, string>(state.ResolvedServers),
            new Dictionary<string, bool>(state.Ipv6Support),
            state.LocalPorts), JsonOptions);
        byte[] protectedState;
        try
        {
            protectedState = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProtectedDirectory.EnsureFor(directory, _path);
            var temporary = _path + $".{Guid.NewGuid():N}.new";
            try
            {
                await using (var stream = ProtectedConfigurationFile.CreateNew(temporary))
                {
                    await stream.WriteAsync(protectedState, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, _path, overwrite: true);
                ProtectedConfigurationFile.Harden(_path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record StoredState(
        bool DesiredConnected,
        Guid? DesiredProfileId,
        Dictionary<string, string>? ResolvedServers,
        Dictionary<string, bool>? Ipv6Support,
        LocalEnginePorts? LocalPorts = null);
}
