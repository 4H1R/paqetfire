namespace PaqetFire.Core.Applications;

public sealed class ApplicationDiscoveryService(IEnumerable<IApplicationSource> sources)
    : IApplicationDiscoveryService
{
    private readonly IReadOnlyList<IApplicationSource> _sources = sources?.ToArray()
        ?? throw new ArgumentNullException(nameof(sources));

    public Task<IReadOnlyList<DiscoverableApplication>> DiscoverAsync(
        string? searchText = null,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DiscoverableApplication>>(
            () => Discover(searchText, cancellationToken),
            cancellationToken);

    private IReadOnlyList<DiscoverableApplication> Discover(
        string? searchText,
        CancellationToken cancellationToken)
    {
        var applications = new Dictionary<string, DiscoverableApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in source.GetApplications(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryNormalize(candidate, out var normalized))
                {
                    continue;
                }

                if (applications.TryGetValue(normalized.ExecutablePath, out var existing))
                {
                    applications[normalized.ExecutablePath] = Merge(existing, normalized);
                }
                else
                {
                    applications.Add(normalized.ExecutablePath, normalized);
                }
            }
        }

        var terms = (searchText ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return applications.Values
            .Where(application => Matches(application, terms))
            .OrderByDescending(application => application.IsRunning)
            .ThenBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(application => application.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryNormalize(
        DiscoverableApplication candidate,
        out DiscoverableApplication normalized)
    {
        normalized = null!;
        if (candidate is null ||
            string.IsNullOrWhiteSpace(candidate.ExecutablePath) ||
            !Path.IsPathFullyQualified(candidate.ExecutablePath))
        {
            return false;
        }

        string path;
        try
        {
            path = Path.GetFullPath(candidate.ExecutablePath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fallbackName = Path.GetFileNameWithoutExtension(path);
        var displayName = string.IsNullOrWhiteSpace(candidate.DisplayName)
            ? fallbackName
            : candidate.DisplayName.Trim();
        normalized = candidate with
        {
            DisplayName = displayName,
            ExecutablePath = path,
        };
        return true;
    }

    private static DiscoverableApplication Merge(
        DiscoverableApplication existing,
        DiscoverableApplication candidate)
    {
        var displayName = existing.DisplayName;
        if ((!existing.IsInstalled && candidate.IsInstalled) || IsFallbackName(existing))
        {
            displayName = candidate.DisplayName;
        }

        return new DiscoverableApplication(
            displayName,
            existing.ExecutablePath,
            existing.IsInstalled || candidate.IsInstalled,
            existing.IsRunning || candidate.IsRunning);
    }

    private static bool IsFallbackName(DiscoverableApplication application) =>
        string.Equals(
            application.DisplayName,
            Path.GetFileNameWithoutExtension(application.ExecutablePath),
            StringComparison.OrdinalIgnoreCase);

    private static bool Matches(DiscoverableApplication application, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return true;
        }

        return terms.All(term =>
            application.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            application.ExecutablePath.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
