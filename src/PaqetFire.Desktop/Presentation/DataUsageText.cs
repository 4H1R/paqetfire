using System.Globalization;
using PaqetFire.Core.Usage;

namespace PaqetFire.Desktop.Presentation;

public sealed record DataUsageTotals(long DownloadBytes, long UploadBytes)
{
    public long TotalBytes => DownloadBytes + UploadBytes;

    public string TotalText => LiveStatsText.FormatBytes(TotalBytes);

    public string DetailText =>
        $"↓ {LiveStatsText.FormatBytes(DownloadBytes)}   ↑ {LiveStatsText.FormatBytes(UploadBytes)}";
}

/// <param name="Share">The day's total relative to the busiest day shown, from 0 to 100.</param>
public sealed record DataUsageDayRow(
    string DateText,
    string DownloadText,
    string UploadText,
    string TotalText,
    double Share);

public sealed record DataUsageSummary(
    DataUsageTotals Today,
    DataUsageTotals ThisMonth,
    DataUsageTotals RecentDays,
    IReadOnlyList<DataUsageDayRow> Days);

public static class DataUsageText
{
    /// <summary>How many days, ending today, the recent total and the daily list cover.</summary>
    public const int RecentDayCount = 30;

    /// <summary>Totals for one profile, or for every recorded profile when <paramref name="profileId"/> is null.</summary>
    public static DataUsageSummary Summarize(DataUsageReport report, Guid? profileId)
    {
        ArgumentNullException.ThrowIfNull(report);
        var today = report.Today;
        var days = report.Profiles
            .Where(profile => profileId is null || profile.ProfileId == profileId)
            .SelectMany(profile => profile.Days)
            .GroupBy(day => day.Date)
            .Select(group => new DailyDataUsage(
                group.Key,
                group.Sum(day => day.DownloadBytes),
                group.Sum(day => day.UploadBytes)))
            .Where(day => day.Date <= today)
            .OrderByDescending(day => day.Date)
            .ToArray();

        var firstRecent = today.AddDays(-(RecentDayCount - 1));
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var recent = days.Where(day => day.Date >= firstRecent && day.TotalBytes > 0).ToArray();
        var busiest = recent.Length == 0 ? 0 : recent.Max(day => day.TotalBytes);
        return new DataUsageSummary(
            Total(days.Where(day => day.Date == today)),
            Total(days.Where(day => day.Date >= firstOfMonth)),
            Total(recent),
            recent.Select(day => new DataUsageDayRow(
                FormatDay(day.Date, today),
                LiveStatsText.FormatBytes(day.DownloadBytes),
                LiveStatsText.FormatBytes(day.UploadBytes),
                LiveStatsText.FormatBytes(day.TotalBytes),
                busiest == 0 ? 0 : Math.Max(1, 100.0 * day.TotalBytes / busiest))).ToArray());
    }

    public static string FormatDay(DateOnly date, DateOnly today)
    {
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return date.Year == today.Year
            ? date.ToString("ddd, MMM d", CultureInfo.CurrentCulture)
            : date.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static DataUsageTotals Total(IEnumerable<DailyDataUsage> days)
    {
        long download = 0;
        long upload = 0;
        foreach (var day in days)
        {
            download += day.DownloadBytes;
            upload += day.UploadBytes;
        }

        return new DataUsageTotals(download, upload);
    }
}
