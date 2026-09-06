using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace PaqetFire.Desktop.Services;

public sealed class PrerequisiteInstallerService : IDisposable
{
    private static readonly IReadOnlyDictionary<string, InstallerPackage> Packages =
        new Dictionary<string, InstallerPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["winpkfilter"] = new(
                "Windows Packet Filter",
                new Uri("https://github.com/wiresock/ndisapi/releases/download/v3.6.2/Windows.Packet.Filter.3.6.2.1.x64.msi"),
                "Windows.Packet.Filter.3.6.2.1.x64.msi",
                819_200,
                "9C388C0B7F189F7FA98720BAE2CAECF7D64F30910838B80B438ECF8956B8502C",
                "msiexec.exe",
                path => $"/i \"{path}\" /passive /norestart"),
            ["vcredist-x64"] = new(
                "Microsoft Visual C++ runtime",
                new Uri("https://download.visualstudio.microsoft.com/download/pr/0b44c2d1-8944-4834-a01a-c9a225f8088a/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe"),
                "VC_redist.x64.exe",
                25_635_768,
                "CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B",
                null,
                path => $"/install /passive /norestart"),
        };

    private readonly HttpClient httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public static bool CanInstallAutomatically(string prerequisiteId) =>
        Packages.ContainsKey(prerequisiteId);

    public static void OpenOfficialPage(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public async Task<PrerequisiteInstallResult> InstallAsync(
        string prerequisiteId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Packages.TryGetValue(prerequisiteId, out var package))
        {
            throw new InvalidOperationException("This prerequisite must be installed from its official download page.");
        }

        var downloadDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaqetFire",
            "Prerequisites",
            Guid.NewGuid().ToString("N"));
        var downloadPath = Path.Combine(downloadDirectory, package.FileName);

        try
        {
            Directory.CreateDirectory(downloadDirectory);
            progress?.Report($"Downloading {package.DisplayName}…");
            await DownloadVerifiedAsync(package, downloadPath, cancellationToken).ConfigureAwait(false);

            progress?.Report($"Waiting for permission to install {package.DisplayName}…");
            var executable = package.ExecutableName ?? downloadPath;
            var arguments = package.Arguments(downloadPath);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = downloadDirectory,
            }) ?? throw new InvalidOperationException($"Could not start the {package.DisplayName} installer.");

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode switch
            {
                0 or 1638 => new PrerequisiteInstallResult(false),
                3010 => new PrerequisiteInstallResult(true),
                _ => throw new InvalidOperationException(
                    $"The {package.DisplayName} installer exited with code {process.ExitCode}."),
            };
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The administrator permission prompt was cancelled.", exception);
        }
        finally
        {
            TryDeleteDownload(downloadDirectory);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private async Task DownloadVerifiedAsync(
        InstallerPackage package,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            package.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength && contentLength != package.Size)
        {
            throw new InvalidDataException($"The {package.DisplayName} download size did not match the verified package.");
        }

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        var fileInfo = new FileInfo(destination);
        if (fileInfo.Length != package.Size)
        {
            throw new InvalidDataException($"The {package.DisplayName} download was incomplete.");
        }

        await using var stream = File.OpenRead(destination);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {package.DisplayName} download failed its security check.");
        }
    }

    private static void TryDeleteDownload(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The OS can finish cleaning its temporary folder if an installer still holds the file.
        }
    }

    private sealed record InstallerPackage(
        string DisplayName,
        Uri DownloadUri,
        string FileName,
        long Size,
        string Sha256,
        string? ExecutableName,
        Func<string, string> Arguments);
}

public sealed record PrerequisiteInstallResult(bool RestartRequired);
