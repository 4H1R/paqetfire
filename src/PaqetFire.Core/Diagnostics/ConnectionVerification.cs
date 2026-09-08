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
}

public enum VerificationCheckStatus
{
    Passed,
    Warning,
    Failed,
    Skipped,
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

    public string Summary => $"{PassedCount} of {Checks.Count} checks passed";
}
