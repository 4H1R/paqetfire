using System.Globalization;

namespace PaqetFire.Desktop.Presentation;

public enum LatencyQuality
{
    Unknown,
    Excellent,
    Good,
    Fair,
    Poor,
    Unreachable,
}

public static class LiveStatsText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0} {Units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}");
    }

    public static string FormatRate(double bytesPerSecond) =>
        FormatBytes((long)Math.Round(bytesPerSecond)) + "/s";

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var clock = string.Create(
            CultureInfo.InvariantCulture,
            $"{elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
        return elapsed.Days > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.Days}d {clock}")
            : clock;
    }

    public static LatencyQuality GetQuality(int? latencyMilliseconds) => latencyMilliseconds switch
    {
        null => LatencyQuality.Unreachable,
        < 100 => LatencyQuality.Excellent,
        < 250 => LatencyQuality.Good,
        < 500 => LatencyQuality.Fair,
        _ => LatencyQuality.Poor,
    };

    public static string GetQualityLabel(LatencyQuality quality) => quality switch
    {
        LatencyQuality.Excellent => "Excellent",
        LatencyQuality.Good => "Good",
        LatencyQuality.Fair => "Fair",
        LatencyQuality.Poor => "Poor",
        LatencyQuality.Unreachable => "No response",
        _ => "Measuring…",
    };

    /// <summary>Maps a latency sample to a 0..1 signal strength used for the ping bars.</summary>
    public static double GetStrength(int? latencyMilliseconds) => latencyMilliseconds is { } value
        ? Math.Clamp(1 - (value - 60) / 600.0, 0.15, 1)
        : 0;

    public static string FormatProtocol(string? kcpMode, string? kcpBlock)
    {
        var parts = new List<string> { "Paqet KCP" };
        if (!string.IsNullOrWhiteSpace(kcpMode))
        {
            parts.Add(kcpMode.Trim().ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(kcpBlock))
        {
            parts.Add(kcpBlock.Trim().ToLowerInvariant());
        }

        return string.Join(" · ", parts);
    }
}
