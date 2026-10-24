using System.Globalization;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Desktop.Presentation;

public enum ProgressStepState
{
    Pending,
    Active,
    Done,
}

// Get-only properties: the XAML compiler generates setters for init accessors used by x:Bind templates.
public sealed record ConnectionProgressStep
{
    public ConnectionProgressStep(string label, ProgressStepState state)
    {
        Label = label;
        State = state;
    }

    public string Label { get; }

    public ProgressStepState State { get; }

    public string Glyph => State switch
    {
        ProgressStepState.Done => "✓",
        ProgressStepState.Active => "●",
        _ => "○",
    };

    public double Opacity => State == ProgressStepState.Pending ? 0.5 : 1;

    public string AccessibleName => $"{Label}: {State switch
    {
        ProgressStepState.Done => "done",
        ProgressStepState.Active => "in progress",
        _ => "pending",
    }}";
}

public static class ConnectionProgressText
{
    // The order the broker walks through on an ordinary connect.
    private static readonly ConnectionStage[] ConnectStages =
    [
        ConnectionStage.Preparing,
        ConnectionStage.VerifyingPayload,
        ConnectionStage.StartingTransport,
        ConnectionStage.StartingPolicy,
        ConnectionStage.StartingCapture,
        ConnectionStage.ProbingTunnel,
    ];

    // A profile with an exit proxy starts it between the policy and capture.
    private static readonly ConnectionStage[] ExitConnectStages =
    [
        ConnectionStage.Preparing,
        ConnectionStage.VerifyingPayload,
        ConnectionStage.StartingTransport,
        ConnectionStage.StartingPolicy,
        ConnectionStage.StartingExit,
        ConnectionStage.StartingCapture,
        ConnectionStage.ProbingTunnel,
    ];

    private static ConnectionStage[] StagesFor(ConnectionProgress progress, bool withExit) =>
        withExit || progress.Stage == ConnectionStage.StartingExit ? ExitConnectStages : ConnectStages;

    public static string GetStageLabel(ConnectionStage stage, int attempt = 1) => stage switch
    {
        ConnectionStage.Preparing => "Preparing",
        ConnectionStage.VerifyingPayload => "Checking engine files",
        ConnectionStage.StartingTransport => "Starting Paqet transport",
        ConnectionStage.StartingPolicy => "Starting Xray policy",
        ConnectionStage.StartingExit => "Connecting the exit proxy",
        ConnectionStage.StartingCapture => "Starting application capture",
        ConnectionStage.ProbingTunnel => "Testing tunnel",
        ConnectionStage.RecoveringTransport => string.Create(
            CultureInfo.InvariantCulture,
            $"Reconnecting transport (attempt {Math.Max(1, attempt)})"),
        _ => "Working",
    };

    public static IReadOnlyList<ConnectionProgressStep> CreateSteps(ConnectionProgress? progress, bool withExit = false)
    {
        if (progress is null)
        {
            return [];
        }

        if (progress.Stage == ConnectionStage.RecoveringTransport)
        {
            return [new(GetStageLabel(progress.Stage, progress.Attempt), ProgressStepState.Active)];
        }

        var stages = StagesFor(progress, withExit);
        var current = Array.IndexOf(stages, progress.Stage);
        return stages
            .Select((stage, index) => new ConnectionProgressStep(
                GetStageLabel(stage),
                index < current ? ProgressStepState.Done :
                index == current ? ProgressStepState.Active :
                ProgressStepState.Pending))
            .ToArray();
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalMinutes < 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalSeconds} s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds:00} s");
    }

    /// <summary>One line for the status card, InfoBar, and tray tooltip, e.g. "Step 3 of 6 · Starting Paqet transport · 12 s".</summary>
    public static string CreateSummary(ConnectionProgress? progress, DateTimeOffset now, bool withExit = false)
    {
        if (progress is null)
        {
            return string.Empty;
        }

        var label = GetStageLabel(progress.Stage, progress.Attempt);
        var elapsed = FormatElapsed(now - progress.StartedAt);
        var stages = StagesFor(progress, withExit);
        var index = Array.IndexOf(stages, progress.Stage);
        return index < 0
            ? $"{label} · {elapsed}"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Step {index + 1} of {stages.Length} · {label} · {elapsed}");
    }

    /// <summary>A short form that fits the 127-character notification-area tooltip.</summary>
    public static string CreateTrayText(ConnectionProgress progress, DateTimeOffset now) =>
        $"{GetStageLabel(progress.Stage, progress.Attempt)} ({FormatElapsed(now - progress.StartedAt)})";
}
