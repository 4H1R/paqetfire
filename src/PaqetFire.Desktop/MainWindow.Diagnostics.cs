using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop;

/// <summary>The Diagnostics page: verification results, the diagnostic report, and the activity log.</summary>
public sealed partial class MainWindow
{
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
