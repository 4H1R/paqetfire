using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop;

/// <summary>The Diagnostics page: verification results, the diagnostic report, and the activity log.</summary>
public sealed partial class MainWindow
{
    private ConnectionVerificationReport? appliedVerification;

    /// <summary>A report is identified by when it ran; snapshots carry the same one until the next run.</summary>
    private static bool IsSameVerification(ConnectionVerificationReport report, ConnectionVerificationReport? applied) =>
        applied is not null &&
        applied.StartedAt == report.StartedAt &&
        applied.CompletedAt == report.CompletedAt &&
        applied.Checks.SequenceEqual(report.Checks);

    /// <summary>The theme brush for a status tone; used by list templates through x:Bind.</summary>
    public static Brush ToneBrush(StatusTone tone)
    {
        var resourceKey = tone switch
        {
            StatusTone.Critical => "SystemFillColorCriticalBrush",
            StatusTone.Caution => "SystemFillColorCautionBrush",
            StatusTone.Success => "SystemFillColorSuccessBrush",
            StatusTone.Accent => "PaqetFireAccentBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        return (Brush)Application.Current.Resources[resourceKey];
    }
}
