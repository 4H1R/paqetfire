using PaqetFire.Core.Ipc;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.RoutingData;

namespace PaqetFire.Desktop.Ipc;

public interface IBrokerClient
{
    event Action<BrokerEvent>? EventReceived;

    /// <summary>Raised when an open pipe ends without the client closing it.</summary>
    event Action? ConnectionLost;

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

    /// <summary>Update now returns once the download has started; progress and the result arrive in snapshots.</summary>
    ValueTask<BrokerSnapshot> ManageRoutingDataAsync(
        RoutingDataAction action,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>A download returns once it has started; progress and the result arrive in snapshots.</summary>
    ValueTask<BrokerSnapshot> ManageExitEngineAsync(
        ExitEngineAction action,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<string> ExportProfilesAsync(TimeSpan timeout, CancellationToken cancellationToken);

    ValueTask<LiveConnectionStats> GetLiveStatsAsync(TimeSpan timeout, CancellationToken cancellationToken);

    ValueTask<string> GetDiagnosticsAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
