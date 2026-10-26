using System.Text.Json;
using PaqetFire.Core.Usage;

namespace PaqetFire.Broker.Configuration;

public interface IDataUsageStore
{
    IReadOnlyList<ProfileDataUsage> Load();

    ValueTask SaveAsync(IReadOnlyList<ProfileDataUsage> usage, CancellationToken cancellationToken);
}

public sealed class InMemoryDataUsageStore : IDataUsageStore
{
    private IReadOnlyList<ProfileDataUsage> usage = [];

    public int Saves { get; private set; }

    public IReadOnlyList<ProfileDataUsage> Load() => usage;

    public ValueTask SaveAsync(IReadOnlyList<ProfileDataUsage> usage, CancellationToken cancellationToken)
    {
        this.usage = usage;
        Saves++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Persists daily data usage beside the other machine settings in a SYSTEM/Administrators-only
/// file. It holds only profile IDs and byte counts, so it is not DPAPI-protected.
/// </summary>
public sealed class DataUsageStore(string path, ProtectedDirectory? directory = null) : IDataUsageStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<ProfileDataUsage> Load()
    {
        try
        {
            ProtectedDirectory.EnsureFor(directory, _path);
            if (!File.Exists(_path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<StoredUsage>(File.ReadAllBytes(_path), JsonOptions);
            return stored is { Version: CurrentVersion, Profiles: { } profiles }
                ? profiles.Where(profile => profile.Days is not null).ToArray()
                : [];
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Losing usage history is not worth refusing to start over.
            return [];
        }
    }

    public async ValueTask SaveAsync(IReadOnlyList<ProfileDataUsage> usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var content = JsonSerializer.SerializeToUtf8Bytes(new StoredUsage(CurrentVersion, usage), JsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProtectedDirectory.EnsureFor(directory, _path);
            var temporary = _path + $".{Guid.NewGuid():N}.new";
            try
            {
                await using (var stream = ProtectedConfigurationFile.CreateNew(temporary))
                {
                    await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, _path, overwrite: true);
                ProtectedConfigurationFile.Harden(_path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record StoredUsage(int Version, IReadOnlyList<ProfileDataUsage>? Profiles);
}
