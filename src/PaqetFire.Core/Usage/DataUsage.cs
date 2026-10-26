using System.Text.Json.Serialization;

namespace PaqetFire.Core.Usage;

/// <summary>Bytes one profile moved through Paqet transport on one local calendar day.</summary>
public sealed record DailyDataUsage(DateOnly Date, long DownloadBytes, long UploadBytes)
{
    [JsonIgnore]
    public long TotalBytes => DownloadBytes + UploadBytes;
}

public sealed record ProfileDataUsage(Guid ProfileId, IReadOnlyList<DailyDataUsage> Days);

/// <summary>
/// Recorded data usage per profile and day. Only traffic that left through Paqet
/// transport, or the exit proxy riding on it, is counted; direct routes are not.
/// </summary>
/// <param name="Today">The broker's local calendar day when the report was made.</param>
public sealed record DataUsageReport(DateOnly Today, IReadOnlyList<ProfileDataUsage> Profiles);

/// <summary>Which recorded usage to clear: one profile's, or every profile's when <paramref name="ProfileId"/> is null.</summary>
public sealed record DataUsageReset(Guid? ProfileId);

/// <summary>
/// Daily download and upload totals per profile, kept for <see cref="RetentionDays"/>
/// days. Not thread-safe; the owner serializes access.
/// </summary>
public sealed class DataUsageLedger
{
    public const int RetentionDays = 90;

    private readonly Dictionary<Guid, SortedDictionary<DateOnly, (long Download, long Upload)>> profiles = [];

    public DataUsageLedger()
    {
    }

    public DataUsageLedger(IEnumerable<ProfileDataUsage> recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        foreach (var profile in recorded)
        {
            foreach (var day in profile.Days)
            {
                Add(profile.ProfileId, day.Date, day.DownloadBytes, day.UploadBytes);
            }
        }
    }

    public bool IsEmpty => profiles.Count == 0;

    public void Add(Guid profileId, DateOnly date, long downloadBytes, long uploadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(downloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(uploadBytes);
        if (downloadBytes == 0 && uploadBytes == 0)
        {
            return;
        }

        if (!profiles.TryGetValue(profileId, out var days))
        {
            days = [];
            profiles[profileId] = days;
        }

        days.TryGetValue(date, out var current);
        days[date] = (Saturate(current.Download, downloadBytes), Saturate(current.Upload, uploadBytes));
    }

    /// <summary>Drops days older than the retention window ending on <paramref name="today"/>.</summary>
    /// <returns>True when anything was removed.</returns>
    public bool Prune(DateOnly today)
    {
        var oldest = today.AddDays(-(RetentionDays - 1));
        var changed = false;
        foreach (var (profileId, days) in profiles.ToArray())
        {
            foreach (var date in days.Keys.TakeWhile(date => date < oldest).ToArray())
            {
                days.Remove(date);
                changed = true;
            }

            if (days.Count == 0)
            {
                profiles.Remove(profileId);
            }
        }

        return changed;
    }

    /// <summary>Clears one profile's usage, or every profile's when <paramref name="profileId"/> is null.</summary>
    /// <returns>True when anything was removed.</returns>
    public bool Reset(Guid? profileId)
    {
        if (profileId is { } id)
        {
            return profiles.Remove(id);
        }

        var changed = profiles.Count > 0;
        profiles.Clear();
        return changed;
    }

    public IReadOnlyList<ProfileDataUsage> ToProfiles() => profiles
        .Select(profile => new ProfileDataUsage(
            profile.Key,
            profile.Value.Select(day => new DailyDataUsage(day.Key, day.Value.Download, day.Value.Upload)).ToArray()))
        .ToArray();

    private static long Saturate(long current, long added) =>
        added > long.MaxValue - current ? long.MaxValue : current + added;
}
