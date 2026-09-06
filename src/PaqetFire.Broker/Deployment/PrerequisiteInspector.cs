using System.Diagnostics;
using Microsoft.Win32;
using PaqetFire.Core.Deployment;

namespace PaqetFire.Broker.Deployment;

public sealed class PrerequisiteInspector
{
    private static readonly Uri NpcapHelp = new("https://npcap.com/#download");
    private static readonly Uri WinpkFilterHelp = new("https://github.com/wiresock/ndisapi/releases/tag/v3.6.2");
    private static readonly Uri VisualCppHelp = new("https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist");
    private static readonly Uri DotNetFrameworkHelp = new("https://dotnet.microsoft.com/en-us/download/dotnet-framework/net472");

    public IReadOnlyList<PrerequisiteStatus> Inspect() =>
    [
        InspectNpcap(),
        InspectWinpkFilter(),
        InspectVisualCppRuntime(),
        InspectDotNetFramework(),
    ];

    private static PrerequisiteStatus InspectNpcap()
    {
        var paths = new[]
        {
            Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"),
            Path.Combine(Environment.SystemDirectory, "wpcap.dll"),
        };
        var installed = paths.FirstOrDefault(File.Exists);
        return new PrerequisiteStatus(
            "npcap",
            "Npcap packet capture driver",
            installed is not null,
            installed is null
                ? "Required by Paqet. Install Npcap with WinPcap-compatible mode enabled."
                : $"Detected at {installed}.",
            NpcapHelp);
    }

    private static PrerequisiteStatus InspectWinpkFilter()
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "ndisrd.sys");
        var version = TryReadFileVersion(path);
        var installed = PrerequisiteCompatibility.IsSupportedWindowsPacketFilterVersion(version);
        return new PrerequisiteStatus(
            "winpkfilter",
            "Windows Packet Filter driver",
            installed,
            installed
                ? $"Compatible 3.x driver detected (version {version})."
                : version is null
                    ? "Required by ProxiFyre. Install the architecture-matched signed driver."
                    : $"Driver version {version} is incompatible. ProxiFyre requires version 3.6.1 or newer, but earlier than 4.0.",
            WinpkFilterHelp);
    }

    private static PrerequisiteStatus InspectVisualCppRuntime()
    {
        var requiredFiles = new[]
        {
            "msvcp140.dll",
            "msvcp140_atomic_wait.dll",
            "vcruntime140.dll",
            "vcruntime140_1.dll",
        };
        var versions = requiredFiles
            .Select(file => TryReadFileVersion(Path.Combine(Environment.SystemDirectory, file)))
            .ToArray();
        var universalCrtVersion = TryReadFileVersion(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll"));
        var installed = versions.All(PrerequisiteCompatibility.IsSupportedVisualCppRuntimeVersion) &&
            PrerequisiteCompatibility.IsSupportedUniversalCrtVersion(universalCrtVersion);
        var detected = versions.Where(version => version is not null).Min();

        return new PrerequisiteStatus(
            "vcredist-x64",
            "Microsoft Visual C++ runtime (x64)",
            installed,
            installed
                ? $"Compatible runtime detected (minimum file version {detected})."
                : "Required by ProxiFyre. Install the Microsoft Visual C++ 2015–2022 x64 runtime 14.44.35211.0 or newer.",
            VisualCppHelp);
    }

    private static PrerequisiteStatus InspectDotNetFramework()
    {
        var release = ReadDotNetFrameworkRelease(RegistryView.Registry64);
        if (Environment.Is64BitOperatingSystem)
        {
            release = Math.Max(release, ReadDotNetFrameworkRelease(RegistryView.Registry32));
        }

        var installed = PrerequisiteCompatibility.IsSupportedDotNetFrameworkRelease(release);
        return new PrerequisiteStatus(
            "dotnet-framework",
            ".NET Framework 4.7.2 or newer",
            installed,
            installed
                ? $"Compatible .NET Framework detected (release {release})."
                : "Required by ProxiFyre. Install .NET Framework 4.7.2 or newer.",
            DotNetFrameworkHelp);
    }

    private static Version? TryReadFileVersion(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var value = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return Version.TryParse(value, out var version) ? version : null;
    }

    private static int ReadDotNetFrameworkRelease(RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var fullKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            return fullKey?.GetValue("Release") is int release ? release : 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }
}
