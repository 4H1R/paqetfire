using PaqetFire.Core.Deployment;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class PrerequisiteCompatibilityTests
{
    [Theory]
    [InlineData(461807, false)]
    [InlineData(461808, true)]
    [InlineData(533320, true)]
    public void DotNetFrameworkReleaseUses472AsMinimum(int release, bool expected) =>
        Assert.Equal(expected, PrerequisiteCompatibility.IsSupportedDotNetFrameworkRelease(release));

    [Theory]
    [InlineData("14.44.35210.0", false)]
    [InlineData("14.44.35211.0", true)]
    [InlineData("14.50.0.0", true)]
    public void VisualCppRuntimeUsesProxiFyreMinimum(string version, bool expected) =>
        Assert.Equal(
            expected,
            PrerequisiteCompatibility.IsSupportedVisualCppRuntimeVersion(Version.Parse(version)));

    [Theory]
    [InlineData("3.6.0.9", false)]
    [InlineData("3.6.1.0", true)]
    [InlineData("3.9.9.9", true)]
    [InlineData("4.0.0.0", false)]
    public void WindowsPacketFilterRequiresCompatible3xDriver(string version, bool expected) =>
        Assert.Equal(
            expected,
            PrerequisiteCompatibility.IsSupportedWindowsPacketFilterVersion(Version.Parse(version)));
}
