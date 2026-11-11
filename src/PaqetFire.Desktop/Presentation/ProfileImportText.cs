namespace PaqetFire.Desktop.Presentation;

/// <summary>
/// Wording for importing a profile export. Replacing every saved profile is destructive and
/// the broker refuses it while the route is up, so it is offered only while disconnected and
/// confirmed separately.
/// </summary>
public static class ProfileImportText
{
    /// <summary>Up to this many profiles are listed by name.</summary>
    public const int NamedProfileLimit = 5;

    public static string Contents(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count switch
        {
            0 => "The file contains no profiles.",
            1 => $"The file contains 1 profile: {names[0]}.",
            <= NamedProfileLimit => $"The file contains {names.Count} profiles: {string.Join(", ", names)}.",
            _ => $"The file contains {names.Count} profiles.",
        };
    }

    public static string ChoiceMessage(IReadOnlyList<string> names, bool canReplace)
    {
        var choice = canReplace
            ? " Add them beside your current profiles, or replace every saved profile with them."
            : " They are added beside your current profiles. Replacing all profiles is available only while the route is disconnected.";
        return Contents(names) + choice +
            " Transport keys are restored from portable exports; older redacted exports need keys entered again. Your SOCKS username and password are kept either way.";
    }

    public static string ReplaceTitle(int existingCount) =>
        existingCount == 1 ? "Replace your 1 existing profile?" : $"Replace all {existingCount} existing profiles?";

    public static string ReplaceMessage(int importedCount, bool hasPendingChanges)
    {
        var message = $"Every saved profile on this PC, with its transport key, is deleted and replaced by the {(importedCount == 1 ? "1 profile" : $"{importedCount} profiles")} in the file.";
        if (hasPendingChanges)
        {
            message += " The unsaved changes on this page are discarded.";
        }

        return message + " This cannot be undone.";
    }
}
