using PaqetFire.Core.Configuration;

namespace PaqetFire.Core.Routing;

public static class RoutingPolicyCompiler
{
    /// <summary>
    /// The Internet Connection Sharing service, which hands Mobile hotspot devices their
    /// address and answers their DNS. ProxiFyre filters the hotspot adapter too, so a
    /// catch-all would carry its replies to a phone into Xray and the phone would never
    /// join. ProxiFyre names a service-owned socket by its service, so this exclusion
    /// does not reach the other services sharing its svchost.exe.
    /// </summary>
    public const string HotspotServiceExclusion = "SharedAccess";

    public static ProxiFyreRoutePlan Compile(
        RoutingPolicy policy,
        string paqetExecutablePath,
        string proxiFyreExecutablePath,
        IEnumerable<string>? additionalLockedExclusions = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var applications = policy.Mode switch
        {
            RoutingMode.AllApplications => [string.Empty],
            RoutingMode.SelectedApplications => NormalizeEntries(policy.SelectedApplications),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), "Unknown routing mode."),
        };

        if (policy.Mode == RoutingMode.SelectedApplications && applications.Count == 0)
        {
            throw new InvalidOperationException(
                "Selected-applications mode requires at least one application.");
        }

        var lockedExclusions = CreateLockedExclusions(
            paqetExecutablePath,
            proxiFyreExecutablePath,
            additionalLockedExclusions);

        var exclusions = NormalizeEntries(policy.UserExclusions
                .Concat(lockedExclusions))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rule = new ProxyRule(
            policy.Mode == RoutingMode.AllApplications
                ? "All network traffic"
                : "Selected applications",
            applications,
            policy.LocalSocksEndpoint,
            policy.RouteTcp,
            policy.RouteUdp,
            policy.RouteIpv4,
            policy.RouteIpv6,
            policy.Username,
            policy.Password,
            policy.Transport,
            policy.TlsServerName,
            policy.TlsPinnedSha256,
            policy.TlsAllowInvalidCertificate);

        return new ProxiFyreRoutePlan([rule], exclusions, policy.BypassLan, policy.LogLevel);
    }

    public static IReadOnlyList<string> CreateLockedExclusions(
        string paqetExecutablePath,
        string proxiFyreExecutablePath,
        IEnumerable<string>? additionalLockedExclusions = null)
    {
        var candidates = new List<string>
        {
            NormalizeEnginePattern(paqetExecutablePath, nameof(paqetExecutablePath)),
            NormalizeEnginePattern(proxiFyreExecutablePath, nameof(proxiFyreExecutablePath)),
            HotspotServiceExclusion,
        };

        if (additionalLockedExclusions is not null)
        {
            candidates.AddRange(additionalLockedExclusions);
        }

        return NormalizeEntries(candidates)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeEntries(IEnumerable<string> entries)
    {
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeEnginePattern(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var trimmed = value.Trim();
        if (ConfigurationValuePolicy.ContainsControlCharacter(trimmed))
        {
            throw new ArgumentException(
                "An engine exclusion must not contain control characters.",
                parameterName);
        }

        // A full executable path is the narrowest ProxiFyre exclusion. Name-only
        // exclusions use permissive substring matching and can bypass unrelated apps.
        if (Path.IsPathFullyQualified(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An engine executable name is required.", parameterName);
        }

        return name;
    }
}
