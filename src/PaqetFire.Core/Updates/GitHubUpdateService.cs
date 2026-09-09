using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaqetFire.Core.Updates;

public sealed record SoftwareRelease(
    Version Version,
    string TagName,
    Uri ReleasePageUri,
    Uri InstallerUri,
    Uri ChecksumUri);

public sealed record SoftwareUpdateCheck(Version CurrentVersion, SoftwareRelease LatestRelease)
{
    public bool IsUpdateAvailable =>
        GitHubUpdateService.NormalizeVersion(LatestRelease.Version)
            .CompareTo(GitHubUpdateService.NormalizeVersion(CurrentVersion)) > 0;
}

public sealed record SoftwareUpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Min(100, BytesReceived * 100d / TotalBytes.Value)
        : null;
}

public sealed class GitHubUpdateService
{
    public const string RepositoryOwner = "4H1R";
    public const string RepositoryName = "paqetfire";

    private static readonly Uri LatestReleaseApiUri = new(
        $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    private readonly HttpClient httpClient;
    private readonly string downloadDirectory;

    public GitHubUpdateService(HttpClient httpClient, string? downloadDirectory = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.downloadDirectory = downloadDirectory ?? Path.Combine(
            Path.GetTempPath(),
            "PaqetFire",
            "updates");
    }

    public async Task<SoftwareUpdateCheck> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("PaqetFire-updater");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var release = ParseRelease(document.RootElement);
        return new SoftwareUpdateCheck(currentVersion, release);
    }

    public async Task<string> DownloadVerifiedInstallerAsync(
        SoftwareRelease release,
        IProgress<SoftwareUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        var expectedHash = await DownloadChecksumAsync(release.ChecksumUri, cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(downloadDirectory);

        var installerName = $"PaqetFire-{FormatVersion(release.Version)}-x64.msi";
        var installerPath = Path.Combine(downloadDirectory, installerName);
        var partialPath = installerPath + $".{Guid.NewGuid():N}.partial";

        try
        {
            using var response = await httpClient.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, release.InstallerUri),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var destination = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    received += read;
                    progress?.Report(new SoftwareUpdateDownloadProgress(received, totalBytes));
                }
            }

            string actualHash;
            await using (var installer = File.OpenRead(partialPath))
            {
                actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(installer, cancellationToken).ConfigureAwait(false));
            }
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The downloaded installer did not match the checksum published with the GitHub release.");
            }

            File.Move(partialPath, installerPath, overwrite: true);
            return installerPath;
        }
        catch
        {
            File.Delete(partialPath);
            throw;
        }
    }

    public static string FormatVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    internal static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static SoftwareRelease ParseRelease(JsonElement root)
    {
        var tagName = GetRequiredString(root, "tag_name");
        if (!Version.TryParse(tagName.Trim().TrimStart('v', 'V'), out var version))
        {
            throw new InvalidDataException($"GitHub returned an unsupported release tag: {tagName}.");
        }

        var formattedVersion = FormatVersion(version);
        var installerName = $"PaqetFire-{formattedVersion}-x64.msi";
        var checksumName = installerName + ".sha256";
        Uri? installerUri = null;
        Uri? checksumUri = null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The latest GitHub release does not contain downloadable assets.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetRequiredString(asset, "name");
            var downloadUri = ParseGitHubUri(GetRequiredString(asset, "browser_download_url"));
            if (name.Equals(installerName, StringComparison.OrdinalIgnoreCase))
            {
                installerUri = downloadUri;
            }
            else if (name.Equals(checksumName, StringComparison.OrdinalIgnoreCase))
            {
                checksumUri = downloadUri;
            }
        }

        if (installerUri is null || checksumUri is null)
        {
            throw new InvalidDataException(
                $"The latest GitHub release is missing {installerName} or its checksum.");
        }

        var releasePageUri = ParseGitHubUri(GetRequiredString(root, "html_url"));
        return new SoftwareRelease(version, tagName, releasePageUri, installerUri, checksumUri);
    }

    private async Task<string> DownloadChecksumAsync(Uri checksumUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(checksumUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var value = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (value.Length > 4096)
        {
            throw new InvalidDataException("The published installer checksum is unexpectedly large.");
        }

        var hash = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The GitHub release contains an invalid SHA-256 checksum.");
        }

        return hash;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"GitHub omitted the release field '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static Uri ParseGitHubUri(string value)
    {
        var repositoryPath = $"/{RepositoryOwner}/{RepositoryName}/";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(repositoryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned a release address outside the PaqetFire repository.");
        }

        return uri;
    }
}
