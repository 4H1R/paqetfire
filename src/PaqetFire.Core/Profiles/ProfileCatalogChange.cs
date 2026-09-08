using PaqetFire.Core.Configuration;

namespace PaqetFire.Core.Profiles;

/// <summary>
/// Describes one atomic catalog edit. Supplying identifiers at the call site keeps
/// edits reproducible and lets UI and broker callers choose their own ID source.
/// </summary>
public abstract record ProfileCatalogChange
{
    private ProfileCatalogChange()
    {
    }

    public sealed record Add(Guid Id, PaqetFireSettings Settings, bool MakeActive = false)
        : ProfileCatalogChange;

    public sealed record Update(Guid Id, PaqetFireSettings Settings)
        : ProfileCatalogChange;

    public sealed record Rename(Guid Id, string Name)
        : ProfileCatalogChange;

    public sealed record Duplicate(Guid SourceId, Guid NewId, string? Name = null, bool MakeActive = true)
        : ProfileCatalogChange;

    public sealed record Delete(Guid Id)
        : ProfileCatalogChange;

    public sealed record Activate(Guid Id)
        : ProfileCatalogChange;

    public sealed record MakeDefault(Guid Id)
        : ProfileCatalogChange;
}
