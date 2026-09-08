using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using PaqetFire.Core.Applications;

namespace PaqetFire.Desktop.Services.ApplicationDiscovery;

public sealed class WindowsInstalledApplicationSource : IApplicationSource
{
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<DiscoverableApplication> GetApplications(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var applications = new List<DiscoverableApplication>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadAppPaths(hive, view, applications, cancellationToken);
                ReadUninstallEntries(hive, view, applications, cancellationToken);
            }
        }

        return applications;
    }

    private static void ReadAppPaths(
        RegistryHive hive,
        RegistryView view,
        ICollection<DiscoverableApplication> applications,
        CancellationToken cancellationToken)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var appPaths = TryOpenSubKey(baseKey, AppPathsKey);
        if (appPaths is null)
        {
            return;
        }

        foreach (var subKeyName in TryGetSubKeyNames(appPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var appKey = TryOpenSubKey(appPaths, subKeyName);
            var path = TryGetExecutablePath(TryGetStringValue(appKey, null));
            AddIfUsable(applications, path, displayName: null);
        }
    }

    private static void ReadUninstallEntries(
        RegistryHive hive,
        RegistryView view,
        ICollection<DiscoverableApplication> applications,
        CancellationToken cancellationToken)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = TryOpenSubKey(baseKey, UninstallKey);
        if (uninstall is null)
        {
            return;
        }

        foreach (var subKeyName in TryGetSubKeyNames(uninstall))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var appKey = TryOpenSubKey(uninstall, subKeyName);
            var path = TryGetExecutablePath(TryGetStringValue(appKey, "DisplayIcon"));
            var displayName = TryGetStringValue(appKey, "DisplayName");
            AddIfUsable(applications, path, displayName);
        }
    }

    private static void AddIfUsable(
        ICollection<DiscoverableApplication> applications,
        string? path,
        string? displayName)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        applications.Add(new DiscoverableApplication(
            string.IsNullOrWhiteSpace(displayName) ? GetDisplayName(path) : displayName.Trim(),
            path,
            IsInstalled: true,
            IsRunning: false));
    }

    internal static string? TryGetExecutablePath(string? registryValue)
    {
        if (string.IsNullOrWhiteSpace(registryValue))
        {
            return null;
        }

        var value = Environment.ExpandEnvironmentVariables(registryValue.Trim());
        string candidate;
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            candidate = closingQuote > 1 ? value[1..closingQuote] : value.Trim('"');
        }
        else
        {
            var iconIndex = value.LastIndexOf(',');
            candidate = iconIndex > 0 && int.TryParse(value[(iconIndex + 1)..].Trim(), out _)
                ? value[..iconIndex].Trim()
                : value;
        }

        candidate = candidate.Trim().Trim('"');
        return string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static string GetDisplayName(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new[] { version.ProductName, version.FileDescription }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                ?? Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or Win32Exception)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
    }

    private static RegistryKey? TryOpenSubKey(RegistryKey key, string name)
    {
        try
        {
            return key.OpenSubKey(name, writable: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return null;
        }
    }

    private static string[] TryGetSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return [];
        }
    }

    private static string? TryGetStringValue(RegistryKey? key, string? name)
    {
        if (key is null)
        {
            return null;
        }

        try
        {
            return key.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return null;
        }
    }
}
