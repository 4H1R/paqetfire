using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Desktop.ViewModels;

namespace PaqetFire.Desktop.Presentation;

/// <summary>The desktop's additions to the broker's diagnostic report: a summary first, the activity log last.</summary>
public static class DiagnosticReportText
{
    /// <summary>One line on what most bug reports need: the route, missing software, and failed checks.</summary>
    public static string Summary(
        string connectionState,
        IReadOnlyList<PrerequisiteStatus>? prerequisites,
        ConnectionVerificationReport? verification)
    {
        var missing = prerequisites?.Where(item => !item.IsInstalled).Select(item => item.DisplayName).ToArray();
        var software = missing is null ? "required software not reported"
            : missing.Length == 0 ? "required software installed"
            : $"missing {string.Join(", ", missing)}";
        var failed = verification?.Checks
            .Where(check => check.Status == VerificationCheckStatus.Failed)
            .Select(check => check.Name)
            .ToArray();
        var checks = verification is null ? "route not verified"
            : failed!.Length > 0 ? $"failed checks: {string.Join(", ", failed)}"
            : $"last verification {verification.Summary}";
        return $"Summary: {connectionState} · {software} · {checks}";
    }

    /// <summary>
    /// The full report. Engine lines are left out of the activity section: the broker
    /// report already lists them under its recent engine logs.
    /// </summary>
    public static string Compose(string summary, string brokerReport, IEnumerable<ActivityLogEntry> activity)
    {
        var desktopActivity = string.Join(Environment.NewLine, activity
            .Where(entry => !entry.IsEngineLine)
            .Select(entry => $"{entry.Time} [{entry.Level}] {entry.Message}"));
        return DiagnosticRedactor.Redact(summary, []) + Environment.NewLine +
            brokerReport + Environment.NewLine +
            "[Desktop activity]" + Environment.NewLine +
            DiagnosticRedactor.Redact(desktopActivity, []);
    }
}
