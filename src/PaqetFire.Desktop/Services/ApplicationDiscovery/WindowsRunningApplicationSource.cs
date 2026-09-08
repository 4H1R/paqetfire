using System.ComponentModel;
using System.Diagnostics;
using PaqetFire.Core.Applications;

namespace PaqetFire.Desktop.Services.ApplicationDiscovery;

public sealed class WindowsRunningApplicationSource : IApplicationSource
{
    public IReadOnlyList<DiscoverableApplication> GetApplications(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var applications = new List<DiscoverableApplication>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    applications.Add(new DiscoverableApplication(
                        GetDisplayName(path, process.ProcessName),
                        path,
                        IsInstalled: false,
                        IsRunning: true));
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected and short-lived processes are expected during enumeration.
                }
            }
        }

        return applications;
    }

    private static string GetDisplayName(string path, string fallback)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return FirstNonEmpty(version.ProductName, version.FileDescription, fallback);
        }
        catch (Exception exception) when (exception is FileNotFoundException or Win32Exception)
        {
            return fallback;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
