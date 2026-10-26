using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Broker.Runtime;

public interface IPaqetFireRuntime
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> SaveSettingsAsync(
        PaqetFireSettings settings,
        bool connectAfterSave,
        CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Disconnects, cancelling a connect or recovery that is still running.</summary>
    ValueTask<BrokerSnapshot> DisconnectAsync(CancellationToken cancellationToken);

    ValueTask StopEnginesAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> VerifyConnectionAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> ManageProfilesAsync(ProfileAction action, CancellationToken cancellationToken);

    ValueTask<string> ExportProfilesAsync(CancellationToken cancellationToken);

    /// <summary>Re-detects the network, rewrites engine configuration, and restarts Paqet while the route stays connected.</summary>
    ValueTask RestartTransportAsync(CancellationToken cancellationToken);

    ValueTask<string> GetDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads the engine chain under the runtime gate and repairs it: restarts Paqet in
    /// place, reconnects a route the user still wants, or restores the safe state. Returns
    /// at once when another operation holds the gate, since that operation re-checks the chain.
    /// </summary>
    /// <returns>False when another operation held the gate and nothing was evaluated.</returns>
    ValueTask<bool> SuperviseAsync(CancellationToken cancellationToken);

    /// <summary>The profile the route uses, or would use when connected; null before any profile exists.</summary>
    Guid? ActiveProfileId { get; }

    /// <summary>When <see cref="SuperviseAsync"/> next has scheduled work, if any.</summary>
    DateTimeOffset? NextSupervisionDue { get; }

    /// <summary>
    /// Adapts a connected route after a network change or resume, restarting only the
    /// engines whose inputs changed (Paqet unconditionally after resume).
    /// </summary>
    ValueTask HandleNetworkChangeAsync(bool resumed, CancellationToken cancellationToken);
}
