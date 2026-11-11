using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>The broker's redacted report with a summary line first and the desktop activity last.</summary>
    /// <returns>Null, after explaining why in <paramref name="feedback"/>, when the broker gave no report.</returns>
    private async Task<string?> BuildDiagnosticReportAsync(InfoBar feedback)
    {
        var diagnostics = await ViewModel.GetDiagnosticsAsync();
        if (diagnostics is null)
        {
            ShowInfo(feedback, InfoBarSeverity.Error, "Diagnostics unavailable",
                ViewModel.ErrorMessage ?? "The broker did not return a diagnostic report.");
            return null;
        }

        var summary = DiagnosticReportText.Summary(
            ViewModel.ConnectionState.ToString(),
            lastSnapshot?.Prerequisites,
            lastSnapshot?.Verification ?? appliedVerification);
        return DiagnosticReportText.Compose(summary, diagnostics, ActivityEntries);
    }

    private async void SaveDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (await BuildDiagnosticReportAsync(DiagnosticsReportInfoBar) is not { } report)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt",
            FileName = $"paqetfire-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, report);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowInfo(DiagnosticsReportInfoBar, InfoBarSeverity.Error, "Diagnostics were not saved", exception.Message);
            return;
        }

        ShowInfo(DiagnosticsReportInfoBar, InfoBarSeverity.Success, "Diagnostics saved",
            $"Saved to {Path.GetFileName(dialog.FileName)}. Review it before attaching it to a bug report; secrets, the server host, and public IP addresses are redacted.");
        AddActivity("Diagnostic report saved to a file.");
    }

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
