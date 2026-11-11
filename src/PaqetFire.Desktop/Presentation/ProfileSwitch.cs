using PaqetFire.Desktop.ViewModels;

namespace PaqetFire.Desktop.Presentation;

/// <summary>
/// The broker restarts a live route whenever the active profile changes: switching,
/// duplicating (the copy becomes active) and deleting the active profile. Those actions are
/// confirmed first, and the wording here says what will happen to the route.
/// </summary>
public static class ProfileSwitch
{
    /// <summary>States in which a change of active profile makes the broker restart the route.</summary>
    public static bool RestartsRoute(BrokerConnectionState state) =>
        state is BrokerConnectionState.Connecting or
            BrokerConnectionState.Connected or
            BrokerConnectionState.Degraded or
            BrokerConnectionState.Guarded;

    public static string SwitchTitle(string name) => $"Switch to '{name}'?";

    public static string SwitchMessage(bool restartsRoute, bool hasPendingChanges) => (restartsRoute, hasPendingChanges) switch
    {
        (true, true) => "The route reconnects with the new profile, and the unsaved changes on this page are discarded.",
        (true, false) => "The route reconnects with the new profile.",
        _ => "Switching profiles replaces the unsaved fields currently shown.",
    };

    public static string SwitchAction(bool restartsRoute, bool hasPendingChanges) =>
        hasPendingChanges ? "Discard and switch" : restartsRoute ? "Switch and reconnect" : "Switch";

    public static string DuplicateTitle(string name) => $"Duplicate '{name}'?";

    public static string DuplicateMessage(bool restartsRoute, bool hasPendingChanges)
    {
        var message = "The copy becomes the active profile.";
        if (restartsRoute)
        {
            message += " The route reconnects with it.";
        }

        if (hasPendingChanges)
        {
            message += " The copy is made from the saved settings, so the unsaved changes on this page are discarded.";
        }

        return message;
    }

    public static string DuplicateAction(bool restartsRoute, bool hasPendingChanges) =>
        hasPendingChanges ? "Discard and duplicate" : restartsRoute ? "Duplicate and reconnect" : "Duplicate";

    /// <summary>
    /// The profile that becomes active when <paramref name="deletedId"/> is deleted, by the
    /// catalog's rule: the default profile, or the first remaining one when the default itself
    /// is deleted. Null when the deleted profile is the last one (an empty profile replaces it).
    /// </summary>
    public static ProfileDisplayItem? ActiveAfterDelete(IReadOnlyList<ProfileDisplayItem> profiles, Guid deletedId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var remaining = profiles.Where(profile => profile.Id != deletedId).ToList();
        if (remaining.Count == 0)
        {
            return null;
        }

        var active = profiles.FirstOrDefault(profile => profile.IsActive);
        if (active is not null && active.Id != deletedId)
        {
            return active;
        }

        var @default = profiles.FirstOrDefault(profile => profile.IsDefault);
        return @default is not null && @default.Id != deletedId ? @default : remaining[0];
    }

    public static string DeleteMessage(bool deletesActive, bool restartsRoute, ProfileDisplayItem? next)
    {
        const string removal = "This removes the profile and its transport key. If it is the last profile, a fresh empty profile will appear. Your SOCKS username and password are kept.";
        if (!deletesActive || !restartsRoute)
        {
            return removal;
        }

        return next is null
            ? removal + " Disconnect the route first: the last profile cannot be replaced while the route is up."
            : removal + $" The route reconnects with '{next.Name}'.";
    }

    /// <summary>The last profile cannot be deleted while the route is up; the broker refuses it.</summary>
    public static bool CanDelete(bool deletesActive, bool restartsRoute, ProfileDisplayItem? next) =>
        !(deletesActive && restartsRoute && next is null);
}
