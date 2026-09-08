using PaqetFire.Core.Ipc;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Desktop.Ipc;

public interface IBrokerClient
{
    event Action<BrokerEvent>? EventReceived;

    Task OpenAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> GetSnapshotAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> SaveSettingsAsync(
        PaqetFireSettings settings,
        bool connectAfterSave,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> SetConnectionStateAsync(
        bool connected,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> VerifyConnectionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> ManageProfilesAsync(
        ProfileAction action,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<string> ExportProfilesAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
