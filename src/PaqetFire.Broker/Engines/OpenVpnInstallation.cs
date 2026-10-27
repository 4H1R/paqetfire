using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace PaqetFire.Broker.Engines;

/// <param name="Installation">The usable installation, or null.</param>
/// <param name="Problem">Why no installation can be used; null when one can.</param>
public sealed record OpenVpnLookup(OpenVpnInstallation? Installation, string? Problem);

/// <summary>
/// The user's own OpenVPN Community installation, which the OpenVPN exit runs. PaqetFire
/// ships neither OpenVPN nor its drivers. The broker runs openvpn.exe as SYSTEM, so it only
/// accepts an installation standard users cannot change: the executable, its folder, and
/// the folder above may be modified only by SYSTEM, Administrators, or TrustedInstaller.
/// </summary>
/// <param name="AdapterToolPath">tapctl.exe, which creates the exit's TAP adapter.</param>
public sealed record OpenVpnInstallation(string ExecutablePath, string AdapterToolPath, Version Version)
{
    // Renaming, deleting, or re-permissioning a folder would let its path be rebuilt.
    private const FileSystemRights ReplaceRights =
        FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const FileSystemRights ModifyRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // GENERIC_ALL and GENERIC_WRITE, which inheritable entries may carry unmapped.
    private const int GenericModifyRights = 0x10000000 | 0x40000000;

    private static readonly SecurityIdentifier[] TrustedWriters =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
    ];

    /// <summary>tapctl and the TAP-Windows6 driver ship with every 2.6 installer.</summary>
    public static Version MinimumVersion { get; } = new(2, 6, 0);

    /// <summary>
    /// OpenVPN 2.7 checks the SOCKS5 login reply for version 5 instead of RFC 1929's 1, so
    /// it reads Paqet's correct acceptance as a refusal and the exit never connects.
    /// </summary>
    public static Version FirstBrokenVersion { get; } = new(2, 7, 0);

    /// <summary>
    /// The newest 2.6 installer. openvpn.net's download page offers only 2.7, so linking it
    /// would send users to a version that cannot be the exit.
    /// </summary>
    public static Uri Download { get; } = new("https://swupdate.openvpn.org/community/releases/OpenVPN-2.6.23-I002-amd64.msi");

    /// <summary>Looks where the official installer records itself, then in its default folder.</summary>
    public static OpenVpnLookup Find()
    {
        var candidates = new List<string>();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\OpenVPN");
            if (key?.GetValue("exe_path") is string recorded && Path.IsPathFullyQualified(recorded))
            {
                candidates.Add(recorded);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Fall back to the default folder.
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe"));

        OpenVpnLookup? rejected = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var lookup = Inspect(candidate);
            if (lookup.Installation is not null)
            {
                return lookup;
            }

            rejected ??= lookup;
        }

        return rejected ?? new OpenVpnLookup(
            null,
            $"OpenVPN is not installed. Install OpenVPN Community {MinimumVersion.ToString(2)} to use the OpenVPN exit.");
    }

    internal static OpenVpnLookup Inspect(string executablePath)
    {
        try
        {
            var executable = new FileInfo(Path.GetFullPath(executablePath));
            var folder = executable.Directory!;
            var tool = Path.Combine(folder.FullName, "tapctl.exe");
            var info = FileVersionInfo.GetVersionInfo(executable.FullName);
            var version = new Version(Math.Max(info.FileMajorPart, 0), Math.Max(info.FileMinorPart, 0), Math.Max(info.FileBuildPart, 0));
            if (CheckVersion(version) is { } versionProblem)
            {
                return new(null, versionProblem);
            }

            if (!File.Exists(tool))
            {
                return new(null, "The OpenVPN installation has no tapctl.exe. Reinstall OpenVPN Community with its default components.");
            }

            if (!IsAdministratorOnly(executable) || !IsAdministratorOnly(new FileInfo(tool)) ||
                !IsAdministratorOnly(folder) || !AncestorsAreFixed(folder))
            {
                return new(null,
                    $"PaqetFire does not run OpenVPN from {folder.FullName}: users other than administrators can change that folder. Install OpenVPN in Program Files.");
            }

            return new(new OpenVpnInstallation(executable.FullName, tool, version), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, $"The OpenVPN installation could not be checked: {exception.Message}");
        }
    }

    /// <summary>Why this OpenVPN version cannot be the exit; null when it can.</summary>
    internal static string? CheckVersion(Version version)
    {
        if (version < MinimumVersion)
        {
            return $"OpenVPN {version.ToString(3)} is too old. Install OpenVPN Community {MinimumVersion.ToString(2)}.";
        }

        if (version >= FirstBrokenVersion)
        {
            return $"OpenVPN {version.ToString(3)} cannot be the exit: OpenVPN {FirstBrokenVersion.ToString(2)} rejects Paqet's SOCKS5 login. Uninstall it and install OpenVPN Community {MinimumVersion.ToString(2)}.";
        }

        return null;
    }

    /// <summary>Whether only trusted accounts own or may modify the file or folder.</summary>
    internal static bool IsAdministratorOnly(FileSystemInfo entry) => IsLimitedToTrusted(entry, ModifyRights);

    /// <summary>
    /// Whether every folder above can neither be replaced nor have its children replaced by
    /// anyone untrusted. New folders may still be created beside them, as in C:\.
    /// </summary>
    internal static bool AncestorsAreFixed(DirectoryInfo folder)
    {
        for (var current = folder; current.Parent is { } parent; current = parent)
        {
            if (!IsLimitedToTrusted(current, ReplaceRights) || !IsLimitedToTrusted(parent, FileSystemRights.DeleteSubdirectoriesAndFiles, requireTrustedOwner: false))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLimitedToTrusted(FileSystemInfo entry, FileSystemRights rights, bool requireTrustedOwner = true)
    {
        if (!entry.Exists || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (requireTrustedOwner && !IsTrusted(security.GetOwner(typeof(SecurityIdentifier))))
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            // Entries that only apply to children are checked on the children themselves.
            if (rule.AccessControlType != AccessControlType.Allow ||
                entry is DirectoryInfo && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            var grants = (rule.FileSystemRights & rights) != 0 || ((int)rule.FileSystemRights & GenericModifyRights) != 0;
            if (grants && !IsTrusted(rule.IdentityReference))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTrusted(IdentityReference? identity) =>
        identity is SecurityIdentifier sid && TrustedWriters.Contains(sid);
}
