namespace PaqetFire.Desktop.Presentation;

public readonly record struct WindowBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Window sizes in effective pixels, converted to the physical pixels that AppWindow uses so
/// the window is as large at 150% display scale as it is at 100%.
/// </summary>
public static class WindowSizing
{
    public const int DefaultWidth = 1180;
    public const int DefaultHeight = 780;
    public const int MinimumWidth = 500;
    public const int MinimumHeight = 520;

    /// <summary>The first-launch window, scaled, shrunk to fit the work area, and centred on it.</summary>
    public static WindowBounds Initial(double scale, WindowBounds workArea)
    {
        var width = Math.Min(Scale(DefaultWidth, scale), workArea.Width);
        var height = Math.Min(Scale(DefaultHeight, scale), workArea.Height);
        return new WindowBounds(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height);
    }

    /// <summary>The smallest window the narrow layout still fits, never larger than the work area.</summary>
    public static (int Width, int Height) Minimum(double scale, WindowBounds workArea) =>
        (Math.Min(Scale(MinimumWidth, scale), workArea.Width), Math.Min(Scale(MinimumHeight, scale), workArea.Height));

    private static int Scale(int effectivePixels, double scale) =>
        (int)Math.Round(effectivePixels * (scale > 0 ? scale : 1));
}
