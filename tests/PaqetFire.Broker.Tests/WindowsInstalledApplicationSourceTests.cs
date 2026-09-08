using PaqetFire.Desktop.Services.ApplicationDiscovery;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class WindowsInstalledApplicationSourceTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Acme\acme.exe", @"C:\Program Files\Acme\acme.exe")]
    [InlineData("\"C:\\Program Files\\Acme\\acme.exe\"", @"C:\Program Files\Acme\acme.exe")]
    [InlineData("\"C:\\Program Files\\Acme\\acme.exe\",0", @"C:\Program Files\Acme\acme.exe")]
    [InlineData(@"C:\Program Files\Acme\acme.exe, -12", @"C:\Program Files\Acme\acme.exe")]
    public void RegistryExecutablePathParserHandlesAppPathAndDisplayIconFormats(
        string value,
        string expected)
    {
        Assert.Equal(expected, WindowsInstalledApplicationSource.TryGetExecutablePath(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\Acme\icon.dll,0")]
    [InlineData(@"C:\Program Files\Acme\readme.txt")]
    public void RegistryExecutablePathParserRejectsNonExecutables(string? value)
    {
        Assert.Null(WindowsInstalledApplicationSource.TryGetExecutablePath(value));
    }
}
