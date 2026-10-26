namespace PaqetFire.Desktop.ViewModels;

/// <summary>Why the route cannot be started yet, independent of the display text.</summary>
public enum ReadinessIssue
{
    None,
    PrerequisiteMissing,
    RepairRequired,
    ProfileRequired,
}

/// <summary>The follow-up an error offers, chosen from the broker error code.</summary>
public enum FailureAction
{
    None,
    OpenRequiredSoftware,
    CopyDiagnostics,
    ReviewProfile,
}

public enum PrimaryConnectionAction
{
    None,
    Connect,
    Disconnect,
    RetryBroker,
    SetUpProfile,
    OpenRequiredSoftware,
    OpenDiagnostics,
}

/// <summary>What the last configuration save left behind, so the form knows whether to clear its edits.</summary>
public enum SaveOutcome
{
    None,
    /// <summary>The broker stored the profile.</summary>
    Saved,
    /// <summary>The profile was stored, then the user cancelled the connection that followed.</summary>
    SavedConnectionCancelled,
    /// <summary>The save was cancelled before the broker stored it.</summary>
    Cancelled,
    /// <summary>Rejected, failed, unconfirmed, or refused because another operation was running.</summary>
    NotStored,
}

public sealed record ConnectionViewModelOptions
{
    public TimeSpan LiveStatsInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Snapshot poll that catches a silently dead pipe or missed events.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public IReadOnlyList<TimeSpan> ReconnectBackoff { get; init; } =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>Recorded data usage, or why it could not be read.</summary>
public sealed record DataUsageResult(PaqetFire.Core.Usage.DataUsageReport? Report, string? Error);
