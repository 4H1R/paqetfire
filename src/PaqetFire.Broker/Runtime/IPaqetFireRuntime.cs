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

    ValueTask<BrokerSnapshot> DisconnectAsync(CancellationToken cancellationToken);

    ValueTask StopEnginesAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> VerifyConnectionAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> ManageProfilesAsync(ProfileAction action, CancellationToken cancellationToken);

    ValueTask<string> ExportProfilesAsync(CancellationToken cancellationToken);
}
