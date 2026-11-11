using PaqetFire.Core.Deployment;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Desktop.Presentation;

public enum SetupStep
{
    RequiredSoftware,
    ServerProfile,
    Connect,
}

// Get-only properties: the XAML compiler generates setters for init accessors used by x:Bind templates.
public sealed record SetupChecklistItem
{
    public SetupChecklistItem(SetupStep step, string title, string detail, bool isDone, bool isCurrent)
    {
        Step = step;
        Title = title;
        Detail = detail;
        IsDone = isDone;
        IsCurrent = isCurrent;
    }

    public SetupStep Step { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool IsDone { get; }

    public bool IsCurrent { get; }

    public string Glyph => IsDone ? "✓" : IsCurrent ? "→" : "○";

    public string StatusText => IsDone ? "Done" : IsCurrent ? "Next" : "Waiting";

    /// <summary>"Required software, done": the row's name for screen readers, with its status.</summary>
    public string AccessibleName => $"{Title}, {StatusText.ToLowerInvariant()}";
}

/// <summary>First-run progress: required software, then a server profile, then the first connection.</summary>
public sealed record SetupChecklist(IReadOnlyList<SetupChecklistItem> Items, IReadOnlyList<PrerequisiteStatus> MissingPrerequisites)
{
    public bool IsComplete => Items.All(item => item.IsDone);

    /// <summary>The card is for first-run setup; it disappears once a profile exists and nothing is missing.</summary>
    public bool ShouldShow => Items.Take(2).Any(item => !item.IsDone);

    public SetupStep? CurrentStep => Items.FirstOrDefault(item => item.IsCurrent)?.Step;

    public static SetupChecklist Create(BrokerSnapshot? snapshot, bool isConnected)
    {
        var missing = snapshot?.Prerequisites?.Where(item => !item.IsInstalled).ToArray() ?? [];
        var softwareDone = snapshot?.Prerequisites is not null && missing.Length == 0;
        var profileDone = snapshot?.IsConfigured == true;
        var connectDone = isConnected;
        var current = !softwareDone ? SetupStep.RequiredSoftware
            : !profileDone ? SetupStep.ServerProfile
            : !connectDone ? SetupStep.Connect
            : (SetupStep?)null;

        return new SetupChecklist(
            [
                new(SetupStep.RequiredSoftware, "Required software",
                    snapshot?.Prerequisites is null ? "Waiting for the broker to check this PC."
                    : missing.Length == 0 ? "Npcap, Windows Packet Filter, and runtimes are installed."
                    : $"Missing: {string.Join(", ", missing.Select(item => item.DisplayName))}.",
                    softwareDone, current == SetupStep.RequiredSoftware),
                new(SetupStep.ServerProfile, "Server profile",
                    profileDone ? "A server endpoint and transport key are saved." : "Enter your Paqet server endpoint and transport key, then save.",
                    profileDone, current == SetupStep.ServerProfile),
                new(SetupStep.Connect, "Connect",
                    connectDone ? "The route is active." : "Start the route and check the live session.",
                    connectDone, current == SetupStep.Connect),
            ],
            missing);
    }
}
