using PaqetFire.Core.Applications;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class ApplicationDiscoveryTests
{
    [Fact]
    public async Task DiscoveryMergesInstalledAndRunningEntriesByFullPath()
    {
        var installed = new StaticApplicationSource([
            new DiscoverableApplication(
                "Acme Browser",
                @"C:\Program Files\Acme\browser.exe",
                IsInstalled: true,
                IsRunning: false),
        ]);
        var running = new StaticApplicationSource([
            new DiscoverableApplication(
                "browser",
                @"c:\program files\acme\browser.exe",
                IsInstalled: false,
                IsRunning: true),
        ]);

        var result = await new ApplicationDiscoveryService([running, installed]).DiscoverAsync();

        var application = Assert.Single(result);
        Assert.Equal("Acme Browser", application.DisplayName);
        Assert.True(application.IsInstalled);
        Assert.True(application.IsRunning);
        Assert.Equal(@"C:\program files\acme\browser.exe", application.ExecutablePath, ignoreCase: true);
    }

    [Fact]
    public async Task SearchRequiresEveryTermAndChecksNameAndPath()
    {
        var source = new StaticApplicationSource([
            new DiscoverableApplication(
                "Acme Browser",
                @"C:\Program Files\Acme\browser.exe",
                IsInstalled: true,
                IsRunning: false),
            new DiscoverableApplication(
                "Editor",
                @"C:\Tools\editor.exe",
                IsInstalled: true,
                IsRunning: false),
        ]);

        var result = await new ApplicationDiscoveryService([source])
            .DiscoverAsync("browser program");

        Assert.Equal("Acme Browser", Assert.Single(result).DisplayName);
    }

    [Fact]
    public async Task RunningApplicationsSortBeforeInstalledApplications()
    {
        var source = new StaticApplicationSource([
            new DiscoverableApplication("Alpha", @"C:\Apps\alpha.exe", true, false),
            new DiscoverableApplication("Zulu", @"C:\Apps\zulu.exe", true, true),
        ]);

        var result = await new ApplicationDiscoveryService([source]).DiscoverAsync();

        Assert.Equal(["Zulu", "Alpha"], result.Select(application => application.DisplayName));
    }

    [Theory]
    [InlineData(@"browser.exe")]
    [InlineData(@"C:\Apps\readme.txt")]
    [InlineData("")]
    public async Task DiscoveryRejectsEntriesThatCannotSelectAnExecutable(string path)
    {
        var source = new StaticApplicationSource([
            new DiscoverableApplication("Invalid", path, true, false),
        ]);

        var result = await new ApplicationDiscoveryService([source]).DiscoverAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoveryHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ApplicationDiscoveryService([]).DiscoverAsync(cancellationToken: cancellation.Token));
    }

    private sealed class StaticApplicationSource(
        IReadOnlyList<DiscoverableApplication> applications) : IApplicationSource
    {
        public IReadOnlyList<DiscoverableApplication> GetApplications(
            CancellationToken cancellationToken = default) => applications;
    }
}
