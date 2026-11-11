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

    /// <summary>The session length for screen readers, to the minute: "Connected for 1 hour 5 minutes".</summary>
    public static string DescribeElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "Connected for less than a minute";
        }

        var parts = new List<string>();
        AddUnit(parts, elapsed.Days, "day");
        AddUnit(parts, elapsed.Hours, "hour");
        AddUnit(parts, elapsed.Minutes, "minute");
        return "Connected for " + string.Join(" ", parts);
    }

    /// <summary>"Download 5.6 KB per second, total 16.5 KB": a rate as a screen reader should say it.</summary>
    public static string DescribeTraffic(string direction, string rateText, string totalText) =>
        $"{direction} {rateText.Replace("/s", " per second", StringComparison.Ordinal)}, {totalText.Replace("Total ", "total ", StringComparison.Ordinal)}";

    public static string DescribeLatency(string latencyText, LatencyQuality quality) => quality switch
    {
        LatencyQuality.Unknown => "Latency not measured yet",
        LatencyQuality.Unreachable => "Latency: the Paqet server did not respond",
        _ => $"Latency {latencyText.Replace(" ms", " milliseconds", StringComparison.Ordinal)}, {GetQualityLabel(quality).ToLowerInvariant()}",
    };

    private static void AddUnit(List<string> parts, int value, string unit)
    {
        if (value > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{value} {unit}{(value == 1 ? string.Empty : "s")}"));
        }
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
