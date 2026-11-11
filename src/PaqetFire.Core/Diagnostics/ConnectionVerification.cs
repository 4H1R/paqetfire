namespace PaqetFire.Core.Diagnostics;

public enum VerificationCheckKind
{
    EngineChain,
    LocalRoute,
    ApplicationCapture,
    TcpIpv4,
    UdpDns,
    Ipv6,
    PublicAddress,
    DnsLeak,
}

public enum VerificationCheckStatus
{
    Passed,
    Warning,
    Failed,
    Skipped,

    /// <summary>
    /// A caveat rather than a test result: it never fails the route and is left out of the
    /// passed count. Added last so earlier values keep their names and ordinals over IPC.
    /// </summary>
    Info,
}

public sealed record ConnectionVerificationCheck(
    VerificationCheckKind Kind,
    VerificationCheckStatus Status,
    string Name,
    string Detail,
    long DurationMilliseconds = 0,
    string? ObservedValue = null);

public sealed record ConnectionVerificationReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ConnectionVerificationCheck> Checks)
{
    public int PassedCount => Checks.Count(check => check.Status == VerificationCheckStatus.Passed);

    public int FailedCount => Checks.Count(check => check.Status == VerificationCheckStatus.Failed);

    public bool IsHealthy => FailedCount == 0 &&
        Checks.Any(check => check.Status == VerificationCheckStatus.Passed);

    public int WarningCount => Checks.Count(check => check.Status == VerificationCheckStatus.Warning);

    /// <summary>Checks that test something; informational notes are not counted.</summary>
    public int CountedCheckCount => Checks.Count(check => check.Status != VerificationCheckStatus.Info);

    public string Summary => $"{PassedCount} of {CountedCheckCount} checks passed";
}
