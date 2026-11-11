using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Core.RoutingData;

namespace PaqetFire.Broker.RoutingData;

/// <summary>What the broker remembers about downloaded routing data.</summary>
public sealed record RoutingDataState
{
    public string? InstalledTag { get; init; }

    /// <summary>The release directory's name under the routing data root.</summary>
    public string? InstalledDirectory { get; init; }

    public DateTimeOffset? InstalledAt { get; init; }

    public IReadOnlyList<RoutingDataFile> Files { get; init; } = [];

    /// <summary>When GitHub last answered a check, whether or not it brought a new release.</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }

    public bool AutomaticUpdates { get; init; } = true;

    public string? LastError { get; init; }

    /// <summary>
    /// The installed release Xray could not start with in time. Connects use the bundled data
    /// instead until the next check finds that release current or installs another, so a
    /// slow start does not cost every connect the same wait before it falls back.
    /// </summary>
    public string? LaunchTimedOutTag { get; init; }

    [JsonIgnore]
    public bool IsSkippedForLaunch => IsInstalled && string.Equals(LaunchTimedOutTag, InstalledTag, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsInstalled => InstalledTag is not null && InstalledDirectory is not null;

    public RoutingDataState WithoutInstallation() =>
        this with { InstalledTag = null, InstalledDirectory = null, InstalledAt = null, Files = [], LaunchTimedOutTag = null };
}

/// <summary>
/// Owns <c>%ProgramData%\PaqetFire\routing-data</c>: the state file, one directory per
/// installed release, and short-lived staging directories. The root is restricted to
/// SYSTEM and Administrators, and reparse points are refused anywhere inside it.
/// </summary>
public sealed partial class RoutingDataStore
{
    private const string StateFileName = "state.json";
    private const string StagingPrefix = "staging-";
    private const string ReleasePrefix = "release-";
    private const int MaximumStateBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ProtectedDirectory root;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    /// <param name="policy">Who owns the root and its contents; SYSTEM and Administrators unless a test runs it.</param>
    public RoutingDataStore(string root, ProtectedDirectoryPolicy? policy = null)
    {
        this.root = new ProtectedDirectory(root, policy ?? ProtectedDirectoryPolicy.BrokerPrivate);
    }

    public string Root => root.Path;

    public ProtectedDirectoryPolicy Policy => root.Policy;

    private string StatePath => Path.Combine(Root, StateFileName);

    /// <summary>Re-validates the root before its contents are used.</summary>
    /// <returns>True when the root had to be created or replaced, so earlier contents are gone.</returns>
    public bool EnsureRoot() => root.Ensure();

    public static string CreateReleaseDirectoryName(string tag) =>
        RoutingDataRelease.IsValidTag(tag)
            ? $"{ReleasePrefix}{tag}-{Guid.NewGuid().ToString("N")[..8]}"
            : throw new ArgumentException("The routing data release tag is invalid.", nameof(tag));

    public string ReleasePath(string directoryName) =>
        ReleaseDirectoryPattern().IsMatch(directoryName)
            ? Path.Combine(Root, directoryName)
            : throw new InvalidDataException("The routing data directory name is invalid.");

    /// <summary>
    /// Secures the root, creating it if needed, and reads the state file. A root that had to
    /// be created or replaced, and a state file nobody trusted wrote, both mean no state.
    /// </summary>
    public RoutingDataState Load()
    {
        try
        {
            if (root.Ensure() || SecureFileSystem.IsAbsent(StatePath))
            {
                return new RoutingDataState();
            }

            using var stream = SecureFileSystem.OpenTrustedFileForRead(StatePath, Policy);
            if (stream.Length > MaximumStateBytes)
            {
                return new RoutingDataState();
            }

            var stored = JsonSerializer.Deserialize<RoutingDataState>(stream, JsonOptions) ?? new RoutingDataState();
            return IsConsistent(stored) ? stored : stored.WithoutInstallation();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Losing this state falls back to the bundled data until the next update.
            return new RoutingDataState();
        }
    }

    public async ValueTask SaveAsync(RoutingDataState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            root.Ensure();
            var temporary = Path.Combine(Root, $".state-{Guid.NewGuid():N}.tmp");
            try
            {
                // The root's protected DACL is inherited, so the file is never readable by others.
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                if (!SecureFileSystem.IsAbsent(StatePath) && SecureFileSystem.Inspect(StatePath) is { IsReparsePoint: true } or { IsDirectory: true })
                {
                    SecureFileSystem.DeleteWithoutFollowing(StatePath);
                }

                File.Move(temporary, StatePath, overwrite: true);
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
            writeGate.Release();
        }
    }

    /// <summary>Creates an empty staging directory for one download.</summary>
    public string CreateStagingDirectory()
    {
        root.Ensure();
        var path = Path.Combine(Root, StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        RequirePlainDirectory(path);
        return path;
    }

    /// <summary>Moves a validated staging directory into place as a release directory.</summary>
    public void Promote(string stagingDirectory, string releaseDirectoryName)
    {
        var target = ReleasePath(releaseDirectoryName);
        root.Ensure();
        RequirePlainDirectory(stagingDirectory);
        Directory.Move(stagingDirectory, target);
    }

    /// <summary>
    /// Deletes staging directories and every release not in <paramref name="keep"/>. Links are
    /// removed as links; nothing is followed. A directory still in use is left for a later cleanup.
    /// </summary>
    public void DeleteReleasesExcept(IEnumerable<string?> keep)
    {
        var kept = keep.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            root.Ensure();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(Root))
        {
            var name = Path.GetFileName(entry);
            if (!kept.Contains(name) &&
                (name.StartsWith(StagingPrefix, StringComparison.Ordinal) || name.StartsWith(ReleasePrefix, StringComparison.Ordinal)))
            {
                TryDeleteDirectory(entry);
            }
        }
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            SecureFileSystem.DeleteWithoutFollowing(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // In use or already gone; the next cleanup retries.
        }
    }

    private static void RequirePlainDirectory(string path)
    {
        if (SecureFileSystem.Inspect(path) is not { IsDirectory: true, IsReparsePoint: false })
        {
            throw new InvalidDataException("A routing data directory was replaced by a link.");
        }
    }

    private static bool IsConsistent(RoutingDataState state)
    {
        if (state.InstalledTag is null && state.InstalledDirectory is null)
        {
            return true;
        }

        return RoutingDataRelease.IsValidTag(state.InstalledTag) &&
               state.InstalledDirectory is { } directory &&
               ReleaseDirectoryPattern().IsMatch(directory) &&
               state.Files is { Count: 2 } files &&
               RoutingDataRelease.FileNames.All(name => files.Count(file => file.Name == name) == 1) &&
               files.All(file => file.Size is > 0 and <= RoutingDataRelease.MaximumFileBytes &&
                                 Sha256Pattern().IsMatch(file.Sha256));
    }

    [GeneratedRegex("^release-[0-9]{12}-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseDirectoryPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
