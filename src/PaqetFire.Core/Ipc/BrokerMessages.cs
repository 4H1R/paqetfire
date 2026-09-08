using PaqetFire.Core.Engines;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Core.Ipc;

public enum BrokerCommand
{
    GetSnapshot,
    SaveSettings,
    Connect,
    Disconnect,
    VerifyConnection,
    ManageProfiles,
    ExportProfiles,
}

public enum BrokerMessageType
{
    Response,
    Event,
}

public enum BrokerEventKind
{
    SnapshotChanged,
    Faulted,
}

public enum BrokerErrorCode
{
    IncompatibleProtocol,
    InvalidRequest,
    Unauthorized,
    Busy,
    ConfigurationInvalid,
    PrerequisiteMissing,
    EngineFailure,
    InternalError,
}

public sealed record BrokerRequest(
    Guid RequestId,
    int ProtocolVersion,
    BrokerCommand Command,
    PaqetFireSettings? Settings = null,
    bool ConnectAfterSave = false,
    ProfileAction? ProfileAction = null);

public sealed record BrokerResponse(
    Guid RequestId,
    int ProtocolVersion,
    bool Success,
    BrokerSnapshot? Snapshot = null,
    BrokerError? Error = null,
    string? ExportedProfiles = null)
{
    public static BrokerResponse Succeeded(Guid requestId, BrokerSnapshot? snapshot = null) =>
        new(requestId, IpcProtocol.Version, true, snapshot);

    public static BrokerResponse Failed(
        Guid requestId,
        BrokerErrorCode code,
        string message) =>
        new(requestId, IpcProtocol.Version, false, Error: new BrokerError(code, message));
}

public sealed record BrokerEvent(
    Guid EventId,
    int ProtocolVersion,
    BrokerEventKind Kind,
    DateTimeOffset OccurredAt,
    BrokerSnapshot? Snapshot = null,
    BrokerError? Error = null);

public sealed record BrokerServerMessage(
    BrokerMessageType MessageType,
    BrokerResponse? Response = null,
    BrokerEvent? Event = null)
{
    public static BrokerServerMessage FromResponse(BrokerResponse response) =>
        new(BrokerMessageType.Response, Response: response);

    public static BrokerServerMessage FromEvent(BrokerEvent brokerEvent) =>
        new(BrokerMessageType.Event, Event: brokerEvent);
}

public sealed record BrokerSnapshot(
    IReadOnlyList<EngineStatus> Engines,
    bool IsRouting,
    bool IsKillSwitchEnabled,
    DateTimeOffset CapturedAt,
    bool IsConfigured = false,
    PaqetFireSettingsView? Settings = null,
    IReadOnlyList<PrerequisiteStatus>? Prerequisites = null,
    IReadOnlyList<string>? RecentLogs = null,
    string? StatusMessage = null,
    ConnectionState ConnectionState = ConnectionState.Disconnected,
    string? OperationWarning = null,
    ConnectionVerificationReport? Verification = null,
    ProfileCatalogView? ProfileCatalog = null);

public sealed record BrokerError(BrokerErrorCode Code, string Message);
