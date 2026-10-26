using PaqetFire.Core.Engines;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.RoutingData;

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
    GetLiveStats,
    GetDiagnostics,
    ManageRoutingData,
    ManageExitEngine,
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
    ProfileAction? ProfileAction = null,
    RoutingDataAction? RoutingDataAction = null,
    ExitEngineAction? ExitEngineAction = null);

public sealed record BrokerResponse(
    Guid RequestId,
    int ProtocolVersion,
    bool Success,
    BrokerSnapshot? Snapshot = null,
    BrokerError? Error = null,
    string? ExportedProfiles = null,
    LiveConnectionStats? LiveStats = null,
    string? Diagnostics = null)
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
    ProfileCatalogView? ProfileCatalog = null,
    TunnelHealthReport? Tunnel = null,
    ConnectionProgress? Progress = null,
    RoutingDataStatus? RoutingData = null)
{
    /// <summary>The exit engine's own view of its tunnel while it runs.</summary>
    public ExitProxyStatus? ExitProxy { get; init; }

    /// <summary>Whether each optional exit engine is on this computer, and how to get it.</summary>
    public IReadOnlyList<ExitEngineAvailability> ExitEngines { get; init; } = [];
}

/// <param name="ConnectedRegion">The country of the exit server in use, when the exit reports it.</param>
/// <param name="AvailableRegions">The countries the exit can serve right now; empty until it reports them.</param>
/// <param name="Server">The exit server in use, when the exit reports it.</param>
public sealed record ExitProxyStatus(
    ExitProxyKind Kind,
    bool TunnelActive,
    string? ConnectedRegion,
    IReadOnlyList<string> AvailableRegions,
    string? Server = null);

/// <summary>
/// Exit engines are not part of the installer: Psiphon is downloaded on request and
/// verified against a pinned digest, and OpenVPN is the user's own installation.
/// </summary>
/// <param name="Version">The installed version, when known.</param>
/// <param name="CanDownload">Whether the broker can download and install the engine itself.</param>
/// <param name="IsDownloading">A download or import is running; the result arrives in a later snapshot.</param>
/// <param name="Detail">Why the engine is unavailable, or the last download failure.</param>
/// <param name="DownloadUrl">Where the user can get the engine themselves.</param>
public sealed record ExitEngineAvailability(
    ExitProxyKind Kind,
    bool IsInstalled,
    string? Version = null,
    bool CanDownload = false,
    bool IsDownloading = false,
    string? Detail = null,
    string? DownloadUrl = null);

public enum ExitEngineOperation
{
    /// <summary>Download the pinned release: through Paqet while connected, directly otherwise.</summary>
    Download,

    /// <summary>Install a copy the user downloaded; it must match the same pinned digest.</summary>
    Import,

    Remove,
}

/// <param name="SourcePath">For <see cref="ExitEngineOperation.Import"/>, the file the user chose.</param>
public sealed record ExitEngineAction(
    ExitProxyKind Kind,
    ExitEngineOperation Operation,
    string? SourcePath = null);

public sealed record BrokerError(BrokerErrorCode Code, string Message);

/// <summary>The step a connect or transport recovery is performing; published while it runs.</summary>
public enum ConnectionStage
{
    Preparing,
    VerifyingPayload,
    StartingTransport,
    StartingPolicy,
    StartingExit,
    StartingCapture,
    ProbingTunnel,
    RecoveringTransport,
}

/// <param name="StartedAt">When the whole operation began, for an elapsed-time display.</param>
/// <param name="Attempt">The recovery attempt number; 1 for an ordinary connect.</param>
public sealed record ConnectionProgress(
    ConnectionStage Stage,
    DateTimeOffset StartedAt,
    DateTimeOffset StageStartedAt,
    int Attempt = 1);
