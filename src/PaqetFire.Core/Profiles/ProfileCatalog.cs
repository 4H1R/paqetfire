using PaqetFire.Core.Configuration;
using System.Collections.ObjectModel;

namespace PaqetFire.Core.Profiles;

/// <summary>
/// Owns the invariants and deterministic edit semantics for saved connection profiles.
/// The catalog is immutable: every edit returns a new catalog.
/// </summary>
public sealed class ProfileCatalog
{
    public const int MaximumProfileCount = 64;

    private readonly ConnectionProfile[] _profiles;
    private readonly ReadOnlyCollection<ConnectionProfile> _profilesView;

    private ProfileCatalog(
        IEnumerable<ConnectionProfile> profiles,
        Guid activeProfileId,
        Guid defaultProfileId)
    {
        _profiles = profiles.Select(NormalizeProfile).ToArray();
        Validate(_profiles, activeProfileId, defaultProfileId);
        _profilesView = Array.AsReadOnly(_profiles);
        ActiveProfileId = activeProfileId;
        DefaultProfileId = defaultProfileId;
    }

    public IReadOnlyList<ConnectionProfile> Profiles => _profilesView;

    public Guid ActiveProfileId { get; }

    public Guid DefaultProfileId { get; }

    public ConnectionProfile ActiveProfile => Find(ActiveProfileId);

    public ConnectionProfile DefaultProfile => Find(DefaultProfileId);

    public static ProfileCatalog Create(Guid profileId, PaqetFireSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ProfileCatalog([new ConnectionProfile(profileId, settings)], profileId, profileId);
    }

    /// <summary>
    /// Deterministically migrates the former single-settings model. The caller supplies
    /// the stable ID so repeated migration attempts produce the same persisted document.
    /// </summary>
    public static ProfileCatalog FromLegacySettings(Guid profileId, PaqetFireSettings settings) =>
        Create(profileId, settings);

    public ProfileCatalog Apply(ProfileCatalogChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change switch
        {
            ProfileCatalogChange.Add add => Add(add),
            ProfileCatalogChange.Update update => Update(update),
            ProfileCatalogChange.Rename rename => Rename(rename),
            ProfileCatalogChange.Duplicate duplicate => Duplicate(duplicate),
            ProfileCatalogChange.Delete delete => Delete(delete),
            ProfileCatalogChange.Activate activate => SetActive(activate.Id),
            ProfileCatalogChange.MakeDefault makeDefault => SetDefault(makeDefault.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
    }

    internal static ProfileCatalog Rehydrate(
        IEnumerable<ConnectionProfile> profiles,
        Guid activeProfileId,
        Guid defaultProfileId) =>
        new(profiles, activeProfileId, defaultProfileId);

    private ProfileCatalog Add(ProfileCatalogChange.Add change)
    {
        ArgumentNullException.ThrowIfNull(change.Settings);
        EnsureCapacity();
        EnsureNewId(change.Id);
        var profile = NormalizeProfile(new ConnectionProfile(change.Id, change.Settings));
        EnsureUniqueName(profile.Settings.ProfileName);
        return new ProfileCatalog(
            [.. _profiles, profile],
            change.MakeActive ? profile.Id : ActiveProfileId,
            DefaultProfileId);
    }

    private ProfileCatalog Update(ProfileCatalogChange.Update change)
    {
        ArgumentNullException.ThrowIfNull(change.Settings);
        var index = IndexOf(change.Id);
        var replacement = NormalizeProfile(new ConnectionProfile(change.Id, change.Settings));
        EnsureUniqueName(replacement.Settings.ProfileName, change.Id);
        var profiles = (ConnectionProfile[])_profiles.Clone();
        profiles[index] = replacement;
        return new ProfileCatalog(profiles, ActiveProfileId, DefaultProfileId);
    }

    private ProfileCatalog Rename(ProfileCatalogChange.Rename change)
    {
        var index = IndexOf(change.Id);
        var name = NormalizeName(change.Name);
        EnsureUniqueName(name, change.Id);
        var profiles = (ConnectionProfile[])_profiles.Clone();
        profiles[index] = profiles[index] with
        {
            Settings = profiles[index].Settings with { ProfileName = name },
        };
        return new ProfileCatalog(profiles, ActiveProfileId, DefaultProfileId);
    }

    private ProfileCatalog Duplicate(ProfileCatalogChange.Duplicate change)
    {
        EnsureCapacity();
        EnsureNewId(change.NewId);
        var source = Find(change.SourceId);
        var name = change.Name is null
            ? FindAvailableCopyName(source.Settings.ProfileName)
            : NormalizeName(change.Name);
        EnsureUniqueName(name);
        var duplicate = new ConnectionProfile(
            change.NewId,
            CloneSettings(source.Settings) with { ProfileName = name });
        return new ProfileCatalog(
            [.. _profiles, duplicate],
            change.MakeActive ? duplicate.Id : ActiveProfileId,
            DefaultProfileId);
    }

    private ProfileCatalog Delete(ProfileCatalogChange.Delete change)
    {
        _ = IndexOf(change.Id);
        if (_profiles.Length == 1)
        {
            throw new ProfileCatalogException("The last profile cannot be deleted.");
        }

        var profiles = _profiles.Where(profile => profile.Id != change.Id).ToArray();
        var active = ActiveProfileId;
        var @default = DefaultProfileId;
        if (@default == change.Id)
        {
            @default = active != change.Id ? active : profiles[0].Id;
        }

        if (active == change.Id)
        {
            active = @default;
        }
        return new ProfileCatalog(profiles, active, @default);
    }

    private ProfileCatalog SetActive(Guid id)
    {
        _ = Find(id);
        return new ProfileCatalog(_profiles, id, DefaultProfileId);
    }

    private ProfileCatalog SetDefault(Guid id)
    {
        _ = Find(id);
        return new ProfileCatalog(_profiles, ActiveProfileId, id);
    }

    private ConnectionProfile Find(Guid id)
    {
        var index = IndexOf(id);
        return _profiles[index];
    }

    private int IndexOf(Guid id)
    {
        var index = Array.FindIndex(_profiles, profile => profile.Id == id);
        if (index < 0)
        {
            throw new ProfileCatalogException($"Profile '{id:D}' does not exist.");
        }

        return index;
    }

    private void EnsureCapacity()
    {
        if (_profiles.Length >= MaximumProfileCount)
        {
            throw new ProfileCatalogException($"A catalog can contain at most {MaximumProfileCount} profiles.");
        }
    }

    private void EnsureNewId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ProfileCatalogException("A profile ID cannot be empty.");
        }

        if (_profiles.Any(profile => profile.Id == id))
        {
            throw new ProfileCatalogException($"Profile '{id:D}' already exists.");
        }
    }

    private void EnsureUniqueName(string name, Guid? exceptId = null)
    {
        if (_profiles.Any(profile =>
                profile.Id != exceptId &&
                string.Equals(profile.Settings.ProfileName, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProfileCatalogException($"A profile named '{name}' already exists.");
        }
    }

    private string FindAvailableCopyName(string sourceName)
    {
        var candidate = CreateCopyName(sourceName, suffix: null);
        if (!ContainsName(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix <= MaximumProfileCount + 1; suffix++)
        {
            candidate = CreateCopyName(sourceName, suffix);
            if (!ContainsName(candidate))
            {
                return candidate;
            }
        }

        throw new ProfileCatalogException("A unique copy name could not be generated.");
    }

    private bool ContainsName(string name) => _profiles.Any(profile =>
        string.Equals(profile.Settings.ProfileName, name, StringComparison.OrdinalIgnoreCase));

    private static string CreateCopyName(string sourceName, int? suffix)
    {
        var ending = suffix is null ? " copy" : $" copy ({suffix})";
        var prefixLength = Math.Min(sourceName.Length, 80 - ending.Length);
        return sourceName[..prefixLength].TrimEnd() + ending;
    }

    private static void Validate(
        IReadOnlyList<ConnectionProfile> profiles,
        Guid activeProfileId,
        Guid defaultProfileId)
    {
        if (profiles.Count is < 1 or > MaximumProfileCount)
        {
            throw new ProfileCatalogException($"A catalog must contain 1 to {MaximumProfileCount} profiles.");
        }

        if (profiles.Any(profile => profile.Id == Guid.Empty) ||
            profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Count)
        {
            throw new ProfileCatalogException("Every profile must have a unique, non-empty ID.");
        }

        if (profiles.Select(profile => profile.Settings.ProfileName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count)
        {
            throw new ProfileCatalogException("Every profile must have a unique name.");
        }

        if (!profiles.Any(profile => profile.Id == activeProfileId))
        {
            throw new ProfileCatalogException("The active profile must exist in the catalog.");
        }

        if (!profiles.Any(profile => profile.Id == defaultProfileId))
        {
            throw new ProfileCatalogException("The default profile must exist in the catalog.");
        }
    }

    private static ConnectionProfile NormalizeProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Settings);
        if (profile.Id == Guid.Empty)
        {
            throw new ProfileCatalogException("A profile ID cannot be empty.");
        }

        return profile with
        {
            Settings = CloneSettings(profile.Settings) with
            {
                ProfileName = NormalizeName(profile.Settings.ProfileName),
            },
        };
    }

    private static PaqetFireSettings CloneSettings(PaqetFireSettings settings) => settings with
    {
        SelectedApplications = AsReadOnly(settings.SelectedApplications),
        UserExclusions = AsReadOnly(settings.UserExclusions),
        DirectRouteDestinations = AsReadOnly(settings.DirectRouteDestinations),
        LocalTcpFlags = AsReadOnly(settings.LocalTcpFlags),
        RemoteTcpFlags = AsReadOnly(settings.RemoteTcpFlags),
    };

    private static IReadOnlyList<string> AsReadOnly(IReadOnlyList<string>? values) =>
        Array.AsReadOnly(values?.ToArray() ?? []);

    private static string NormalizeName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80 || name.Any(char.IsControl))
        {
            throw new ProfileCatalogException("A profile name must contain 1 to 80 printable characters.");
        }

        return name;
    }
}
