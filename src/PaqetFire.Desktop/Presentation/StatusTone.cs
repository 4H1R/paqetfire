using PaqetFire.Core.Diagnostics;

namespace PaqetFire.Desktop.Presentation;

/// <summary>The colour a status is shown in; the window maps each tone to a theme brush.</summary>
public enum StatusTone
{
    Neutral,
    Success,
    Caution,
    Critical,
    Accent,
}

public static class StatusToneText
{
    public static StatusTone ForVerification(VerificationCheckStatus status) => status switch
    {
        VerificationCheckStatus.Failed => StatusTone.Critical,
        VerificationCheckStatus.Warning => StatusTone.Caution,
        VerificationCheckStatus.Passed => StatusTone.Success,
        _ => StatusTone.Neutral,
    };

    /// <summary>Errors and warnings stand out; ordinary and engine lines keep the accent colour.</summary>
    public static StatusTone ForActivityLevel(string? level) => level switch
    {
        "ERROR" => StatusTone.Critical,
        "WARN" => StatusTone.Caution,
        _ => StatusTone.Accent,
    };
}
