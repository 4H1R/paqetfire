using PaqetFire.Core.Diagnostics;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop.ViewModels;

public sealed class VerificationDisplayItem
{
    public VerificationDisplayItem()
    {
    }

    public VerificationDisplayItem(string glyph, string name, string status, string detail, string duration)
    {
        Glyph = glyph;
        Name = name;
        Status = status;
        Detail = detail;
        Duration = duration;
    }

    public string Glyph { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public StatusTone Tone { get; set; }
    public string Remedy { get; set; } = string.Empty;
    public string ActionText { get; set; } = string.Empty;
    public string ActionDestination { get; set; } = string.Empty;

    public bool HasRemedy => Remedy.Length > 0;

    public bool HasAction => ActionDestination.Length > 0;

    /// <summary>Rows for a report, problems first, each with what to do about it.</summary>
    public static IReadOnlyList<VerificationDisplayItem> FromReport(ConnectionVerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Checks
            .OrderBy(check => VerificationRemedy.SortRank(check.Status))
            .Select(FromCheck)
            .ToArray();
    }

    public static VerificationDisplayItem FromCheck(ConnectionVerificationCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var (glyph, status) = check.Status switch
        {
            VerificationCheckStatus.Passed => ("✓", "Passed"),
            VerificationCheckStatus.Warning => ("!", "Warning"),
            VerificationCheckStatus.Failed => ("×", "Failed"),
            VerificationCheckStatus.Info => ("i", "Note"),
            _ => ("–", "Skipped"),
        };
        var detail = string.IsNullOrWhiteSpace(check.ObservedValue)
            ? check.Detail
            : $"{check.Detail} Observed: {check.ObservedValue}";
        var remedy = VerificationRemedy.For(check.Kind, check.Status);
        return new VerificationDisplayItem(
            glyph,
            check.Name,
            status,
            detail,
            check.DurationMilliseconds > 0 ? $"{check.DurationMilliseconds} ms" : string.Empty)
        {
            Tone = StatusToneText.ForVerification(check.Status),
            Remedy = remedy?.Text ?? string.Empty,
            ActionText = remedy?.ActionText ?? string.Empty,
            ActionDestination = remedy?.Destination ?? string.Empty,
        };
    }
}
