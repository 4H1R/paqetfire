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

/// <summary>The span of days the Data usage page totals and lists; the broker keeps 90 days.</summary>
public enum DataUsagePeriod
{
    Last30Days,
    ThisMonth,
    LastMonth,
    Last90Days,
}

/// <param name="Period">Totals for the selected period, which the daily list also covers.</param>
public sealed record DataUsageSummary(
    DataUsageTotals Today,
    DataUsageTotals ThisMonth,
    DataUsageTotals Period,
    IReadOnlyList<DataUsageDayRow> Days);

public static class DataUsageText
{
    /// <summary>The first and last day, inclusive, of a period that ends no later than <paramref name="today"/>.</summary>
    public static (DateOnly First, DateOnly Last) Range(DataUsagePeriod period, DateOnly today)
    {
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        return period switch
        {
            DataUsagePeriod.ThisMonth => (firstOfMonth, today),
            DataUsagePeriod.LastMonth => (firstOfMonth.AddMonths(-1), firstOfMonth.AddDays(-1)),
            DataUsagePeriod.Last90Days => (today.AddDays(-(DataUsageLedger.RetentionDays - 1)), today),
            _ => (today.AddDays(-29), today),
        };
    }

    public static string PeriodName(DataUsagePeriod period) => period switch
    {
        DataUsagePeriod.ThisMonth => "This month",
        DataUsagePeriod.LastMonth => "Last month",
        DataUsagePeriod.Last90Days => "Last 90 days",
        _ => "Last 30 days",
    };

    public static string EmptyText(DataUsagePeriod period) => period switch
    {
        DataUsagePeriod.ThisMonth => "No traffic went through Paqet this month.",
        DataUsagePeriod.LastMonth => "No traffic went through Paqet last month.",
        DataUsagePeriod.Last90Days => "No traffic went through Paqet in the last 90 days.",
        _ => "No traffic went through Paqet in the last 30 days.",
    };

    /// <summary>Totals for one profile, or for every recorded profile when <paramref name="profileId"/> is null.</summary>
    public static DataUsageSummary Summarize(
        DataUsageReport report,
        Guid? profileId,
        DataUsagePeriod period = DataUsagePeriod.Last30Days)
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

        var (first, last) = Range(period, today);
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var selected = days.Where(day => day.Date >= first && day.Date <= last && day.TotalBytes > 0).ToArray();
        var busiest = selected.Length == 0 ? 0 : selected.Max(day => day.TotalBytes);
        return new DataUsageSummary(
            Total(days.Where(day => day.Date == today)),
            Total(days.Where(day => day.Date >= firstOfMonth)),
            Total(selected),
            selected.Select(day => new DataUsageDayRow(
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
