namespace PaqetFire.Core.Deployment;

public static class PrerequisiteCompatibility
{
    public const int MinimumDotNetFrameworkRelease = 461808;

    public static Version MinimumVisualCppRuntimeVersion { get; } = new(14, 44, 35211, 0);

    public static Version MinimumUniversalCrtVersion { get; } = new(10, 0, 10240, 0);

    public static Version MinimumWindowsPacketFilterVersion { get; } = new(3, 6, 1, 0);

    public static bool IsSupportedDotNetFrameworkRelease(int release) =>
        release >= MinimumDotNetFrameworkRelease;

    public static bool IsSupportedVisualCppRuntimeVersion(Version? version) =>
        version is not null && version >= MinimumVisualCppRuntimeVersion;

    public static bool IsSupportedUniversalCrtVersion(Version? version) =>
        version is not null && version >= MinimumUniversalCrtVersion;

    public static bool IsSupportedWindowsPacketFilterVersion(Version? version) =>
        version is not null &&
        version >= MinimumWindowsPacketFilterVersion &&
        version.Major < 4;
}
