using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.RoutingData;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class RoutingDataServiceTests : IDisposable
{
    private const string Tag = "202610220935";
    private readonly string root = Path.Combine(Path.GetTempPath(), "PaqetFire-routing-data-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRoutingDataSource source = new() { Tag = Tag };
    private readonly FakeRoutingDataValidator validator = new();
    private readonly ManualClock clock = new();
    private RoutingDataRouteState route = new(IsConnected: false, IsTransitioning: false);

    [Fact]
    public async Task UpdateInstallsAVerifiedReleaseForTheNextXrayStart()
    {
        using var service = CreateService();
        Assert.Equal(new RoutingDataStatus(RoutingDataOrigin.Bundled, "Xray 26.3.27", null, null, true, false, false), service.Status);

        Assert.Equal(RoutingDataUpdateOutcome.Updated, await service.StartUpdate());

        var status = service.Status;
        Assert.Equal(RoutingDataOrigin.Updated, status.Origin);
        Assert.Equal(Tag, status.Version);
        Assert.True(status.SupportsMalwareCategories);
        Assert.False(status.IsUpdating);
        Assert.Null(status.LastError);
        Assert.Equal(clock.GetUtcNow(), status.InstalledAt);
        Assert.Equal(clock.GetUtcNow(), status.LastCheckedAt);

        var set = await service.ResolveForLaunchAsync(CancellationToken.None);
        Assert.True(set.IsUpdated);
        Assert.True(set.SupportsMalwareCategories);
        Assert.Equal(source.Files["geoip.dat"], File.ReadAllBytes(Path.Combine(set.AssetDirectory!, "geoip.dat")));
        Assert.Equal(source.Files["geosite.dat"], File.ReadAllBytes(Path.Combine(set.AssetDirectory!, "geosite.dat")));
        // Xray checked the files where they were staged, before they were moved into place.
        var staged = Assert.Single(validator.Directories);
        Assert.False(Directory.Exists(staged));
        Assert.Equal(new[] { set.AssetDirectory }, Directory.GetDirectories(root));

        using var restarted = CreateService();
        Assert.Equal(status with { IsUpdating = false }, restarted.Status);
        Assert.Equal(set.Fingerprint, (await restarted.ResolveForLaunchAsync(CancellationToken.None)).Fingerprint);
    }

    [Fact]
    public async Task AChecksumMismatchKeepsTheCurrentData()
    {
        using var service = CreateService();
        source.ChecksumOverrides["geosite.dat"] = new string('0', 64);

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
        Assert.Contains("geosite.dat", service.Status.LastError, StringComparison.Ordinal);
        Assert.Null(service.Status.LastCheckedAt);
        Assert.Empty(validator.Directories);
        Assert.Empty(Directory.GetDirectories(root));
        Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACurrentReleaseIsNotDownloadedAgain()
    {
        using var service = CreateService();
        await service.StartUpdate();
        source.Calls.Clear();
        clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(RoutingDataUpdateOutcome.UpToDate, await service.StartUpdate());

        Assert.Equal(["tag:Direct"], source.Calls);
        Assert.Equal(clock.GetUtcNow(), service.Status.LastCheckedAt);
        Assert.Equal(Tag, service.Status.Version);
    }

    [Fact]
    public async Task AConnectedRouteNeverFallsBackToADirectDownload()
    {
        route = new(IsConnected: true, IsTransitioning: false);
        source.FailingPath = RoutingDataNetworkPath.Tunnel;
        using var service = CreateService();

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Equal(["tag:Tunnel"], source.Calls);
        Assert.Equal("GitHub could not be reached.", service.Status.LastError);
        Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
    }

    [Fact]
    public async Task NothingIsDownloadedWhileTheKillSwitchHoldsTraffic()
    {
        route = new(IsConnected: false, IsTransitioning: false, IsGuarded: true);
        using var service = CreateService();

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Empty(source.Calls);
        Assert.Contains("kill switch", service.Status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectedRouteDownloadsThroughTheTunnel()
    {
        route = new(IsConnected: true, IsTransitioning: false);
        using var service = CreateService();

        Assert.Equal(RoutingDataUpdateOutcome.Updated, await service.StartUpdate());

        Assert.All(source.Calls, call => Assert.EndsWith(":Tunnel", call, StringComparison.Ordinal));
        Assert.Equal(5, source.Calls.Count);
    }

    [Fact]
    public async Task ADirectFailureIsReportedWithoutAnotherAttempt()
    {
        source.FailingPath = RoutingDataNetworkPath.Direct;
        using var service = CreateService();

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Equal(["tag:Direct"], source.Calls);
        Assert.Equal("GitHub could not be reached.", service.Status.LastError);
    }

    [Fact]
    public async Task DataXrayRejectsIsNotInstalled()
    {
        validator.Reject = true;
        using var service = CreateService();

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
        Assert.StartsWith("Xray rejected the downloaded routing data", service.Status.LastError, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public async Task AFailedUpdateKeepsThePreviousRelease()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var installed = await service.ResolveForLaunchAsync(CancellationToken.None);
        source.Tag = "202610230935";
        validator.Reject = true;

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Equal(Tag, service.Status.Version);
        Assert.NotNull(service.Status.LastError);
        Assert.True(installed.IsSameData(await service.ResolveForLaunchAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task ANewReleaseKeepsOnlyThePreviousOneBesideIt()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var first = (await service.ResolveForLaunchAsync(CancellationToken.None)).AssetDirectory;
        source.Tag = "202610230935";
        await service.StartUpdate();
        var second = (await service.ResolveForLaunchAsync(CancellationToken.None)).AssetDirectory;
        clock.Advance(TimeSpan.FromDays(2));
        source.Tag = "202610240935";
        await service.StartUpdate();
        var third = (await service.ResolveForLaunchAsync(CancellationToken.None)).AssetDirectory;

        Assert.Equal("202610240935", service.Status.Version);
        Assert.False(Directory.Exists(first));
        Assert.Equal(new[] { second, third }.Order(), Directory.GetDirectories(root).Order());
    }

    [Fact]
    public async Task ChangedFilesFallBackToTheBundledData()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var set = await service.ResolveForLaunchAsync(CancellationToken.None);
        var geosite = Path.Combine(set.AssetDirectory!, "geosite.dat");
        var tampered = File.ReadAllBytes(geosite);
        tampered[0] ^= 0xFF;
        File.WriteAllBytes(geosite, tampered);
        File.SetLastWriteTimeUtc(geosite, DateTime.UtcNow.AddMinutes(1));

        Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));

        var status = service.Status;
        Assert.Equal(RoutingDataOrigin.Bundled, status.Origin);
        Assert.False(status.SupportsMalwareCategories);
        Assert.Contains("integrity", status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseBundledRemovesTheDownloadAndStopsAutomaticUpdates()
    {
        using var service = CreateService();
        await service.StartUpdate();

        var status = await service.ApplyAsync(RoutingDataAction.UseBundled, CancellationToken.None);

        Assert.Equal(RoutingDataOrigin.Bundled, status.Origin);
        Assert.False(status.AutomaticUpdates);
        Assert.Empty(Directory.GetDirectories(root));
        Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));
        using var restarted = CreateService();
        Assert.Equal(status, restarted.Status);
    }

    [Fact]
    public async Task UseBundledCancelsARunningDownload()
    {
        var gate = new TaskCompletionSource();
        source.TagGate = gate.Task;
        using var service = CreateService();
        var update = service.StartUpdate();

        var status = await service.ApplyAsync(RoutingDataAction.UseBundled, CancellationToken.None);

        Assert.Equal(RoutingDataUpdateOutcome.Cancelled, await update);
        Assert.False(status.IsUpdating);
        Assert.Equal(RoutingDataOrigin.Bundled, status.Origin);
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public async Task OnlyOneUpdateRunsAtATime()
    {
        var gate = new TaskCompletionSource();
        source.TagGate = gate.Task;
        using var service = CreateService();

        var first = service.StartUpdate();
        var second = service.StartUpdate();
        var reported = await service.ApplyAsync(RoutingDataAction.UpdateNow, CancellationToken.None);

        Assert.Same(first, second);
        Assert.True(reported.IsUpdating);
        gate.SetResult();
        Assert.Equal(RoutingDataUpdateOutcome.Updated, await first);
        Assert.False(service.Status.IsUpdating);
        Assert.Equal(1, source.Calls.Count(call => call.StartsWith("tag:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpdatesDoNotRunDuringAConnectionChange()
    {
        route = new(IsConnected: true, IsTransitioning: true);
        using var service = CreateService(new RoutingDataServiceOptions
        {
            BundledXrayVersion = "26.3.27",
            TransitionWait = TimeSpan.Zero,
        });

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Empty(source.Calls);
        Assert.Contains("connection change", service.Status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticUpdatesCanBeTurnedOffAndOn()
    {
        using var service = CreateService();

        Assert.False((await service.ApplyAsync(RoutingDataAction.DisableAutomaticUpdates, CancellationToken.None)).AutomaticUpdates);
        using (var restarted = CreateService())
        {
            Assert.False(restarted.Status.AutomaticUpdates);
        }


        Assert.True((await service.ApplyAsync(RoutingDataAction.EnableAutomaticUpdates, CancellationToken.None)).AutomaticUpdates);
    }

    [Fact]
    public async Task AWithdrawnReleaseIsReplacedByTheOlderLatestOne()
    {
        using var service = CreateService();
        await service.StartUpdate();
        source.Tag = "202610210935";

        Assert.Equal(RoutingDataUpdateOutcome.Updated, await service.StartUpdate());

        Assert.Equal("202610210935", service.Status.Version);
    }

    [Fact]
    public async Task AReleaseTaggedFarInTheFutureIsRefused()
    {
        source.Tag = "203001010000";
        using var service = CreateService();

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        Assert.Equal(["tag:Direct"], source.Calls);
        Assert.Contains("does not accept", service.Status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatesAUserAsksForAreRateLimited()
    {
        using var service = CreateService();
        await service.ApplyAsync(RoutingDataAction.UpdateNow, CancellationToken.None);
        await service.StartUpdate();

        var refused = await Assert.ThrowsAsync<RoutingDataBusyException>(() =>
            service.ApplyAsync(RoutingDataAction.UpdateNow, CancellationToken.None));
        Assert.Contains("5 minutes", refused.Message, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await service.ApplyAsync(RoutingDataAction.UpdateNow, CancellationToken.None)).IsUpdating);
    }

    [Fact]
    public async Task AReleaseThatFailedIsNotFetchedAgainBeforeTheRetryInterval()
    {
        validator.Reject = true;
        using var service = CreateService();
        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());
        var error = service.Status.LastError;
        source.Calls.Clear();
        validator.Reject = false;

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate());

        // Only the tag and the two small checksum files were requested.
        Assert.Equal(["tag:Direct", "geoip.dat.sha256sum:Direct", "geosite.dat.sha256sum:Direct"], source.Calls);
        Assert.Equal(error, service.Status.LastError);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(RoutingDataUpdateOutcome.Updated, await service.StartUpdate());
    }

    [Fact]
    public async Task AnUpdateHasAnOverallDeadline()
    {
        source.TagGate = new TaskCompletionSource().Task;
        using var service = CreateService(new RoutingDataServiceOptions
        {
            BundledXrayVersion = "26.3.27",
            AttemptDeadline = TimeSpan.FromMilliseconds(200),
        });

        Assert.Equal(RoutingDataUpdateOutcome.Failed, await service.StartUpdate().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("did not finish", service.Status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileAnotherProgramHoldsFallsBackForThatLaunchOnly()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var set = await service.ResolveForLaunchAsync(CancellationToken.None);

        await using (new FileStream(Path.Combine(set.AssetDirectory!, "geoip.dat"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));
            Assert.Equal(RoutingDataOrigin.Updated, service.Status.Origin);
        }

        Assert.True(set.IsSameData(await service.ResolveForLaunchAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task AReleaseDirectoryReplacedByAJunctionIsForgotten()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var set = await service.ResolveForLaunchAsync(CancellationToken.None);
        var elsewhere = Path.Combine(root + "-elsewhere", "copy");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        Directory.Move(set.AssetDirectory!, elsewhere);
        FileSystemLinks.CreateJunction(set.AssetDirectory!, elsewhere);

        Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));

        Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
        Assert.Contains("integrity", service.Status.LastError, StringComparison.Ordinal);
        // Clean-up removes the link and never walks into its target.
        await service.StartUpdate();
        Assert.True(File.Exists(Path.Combine(elsewhere, "geoip.dat")));
        Directory.Delete(root + "-elsewhere", recursive: true);
    }

    [Fact]
    public async Task AHardLinkedDataFileIsRejected()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var set = await service.ResolveForLaunchAsync(CancellationToken.None);
        var outside = root + "-geoip.dat";
        FileSystemLinks.CreateHardLink(outside, Path.Combine(set.AssetDirectory!, "geoip.dat"));
        try
        {
            Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));
            Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task UseBundledKeepsAReleaseAConnectHasChosen()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var chosen = await service.ResolveForLaunchAsync(CancellationToken.None);

        await service.ApplyAsync(RoutingDataAction.UseBundled, CancellationToken.None);

        Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
        Assert.True(File.Exists(Path.Combine(chosen.AssetDirectory!, "geosite.dat")));
        Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoUpdateStartsWhileTheDownloadIsBeingRemoved()
    {
        var gate = new TaskCompletionSource();
        source.TagGate = gate.Task;
        source.IgnoreCancellation = true;
        using var service = CreateService();
        var update = service.StartUpdate();
        await WaitUntilAsync(() => source.Calls.Count > 0);

        var removal = service.ApplyAsync(RoutingDataAction.UseBundled, CancellationToken.None);
        await Assert.ThrowsAsync<RoutingDataBusyException>(() =>
            service.ApplyAsync(RoutingDataAction.UpdateNow, CancellationToken.None));
        Assert.Equal(RoutingDataUpdateOutcome.Cancelled, await service.StartUpdate());

        gate.SetResult();
        await removal;
        Assert.Equal(RoutingDataUpdateOutcome.Cancelled, await update);
        Assert.Empty(Directory.GetDirectories(root));
        Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
    }

    [Fact]
    public async Task AReleaseXrayWasToldToLoadIsNeverDeleted()
    {
        string? selected = null;
        using var service = CreateService(inUse: () => [selected]);
        await service.StartUpdate();
        selected = Path.Combine(root, service.State.InstalledDirectory!);

        await service.ApplyAsync(RoutingDataAction.UseBundled, CancellationToken.None);

        Assert.True(Directory.Exists(selected));
    }

    [Fact]
    public async Task LaunchFailuresAreRecordedAndCorruptDataIsForgotten()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var set = await service.ResolveForLaunchAsync(CancellationToken.None);

        await service.ReportLaunchFailureAsync(set, new RoutingDataUnavailableException("slow", new TimeoutException()));
        Assert.Equal(RoutingDataOrigin.Updated, service.Status.Origin);
        Assert.Contains("did not start in time", service.Status.LastError, StringComparison.Ordinal);

        await service.ReportLaunchFailureAsync(set, new RoutingDataUnavailableException("bad", new InvalidDataException()));
        Assert.Equal(RoutingDataOrigin.Bundled, service.Status.Origin);
    }

    [Fact]
    public async Task XrayThatCannotStartWithUpdatedDataStartsWithTheBundledData()
    {
        using var service = CreateService();
        await service.StartUpdate();
        await using var fixture = await RuntimeFixture.StartAsync(routingData: service);
        fixture.Controller.ConnectFailures.Enqueue(new ConnectionTransitionException(
            "connect", EngineKind.Xray, new RoutingDataUnavailableException("slow", new TimeoutException())));

        var snapshot = await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.Equal(["connect", "connect"], fixture.Controller.Calls);
        Assert.Equal(ConnectionState.Connected, snapshot.ConnectionState);
        Assert.False(fixture.Xray.Last!.BlockMalware);
        Assert.Same(RoutingDataSet.Bundled, fixture.XrayAdapter.RoutingData);
        Assert.NotNull(service.Status.LastError);
    }

    [Fact]
    public async Task ATimedOutReleaseIsSkippedUntilTheNextCheck()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var set = await service.ResolveForLaunchAsync(CancellationToken.None);

        await service.ReportLaunchFailureAsync(set, new RoutingDataUnavailableException("slow", new TimeoutException()));

        // Later connects do not wait for the same slow start again.
        Assert.Same(RoutingDataSet.Bundled, await service.ResolveForLaunchAsync(CancellationToken.None));
        Assert.False(service.Status.SupportsMalwareCategories);
        Assert.Contains("until the next routing data check", service.Status.LastError, StringComparison.Ordinal);

        Assert.Equal(RoutingDataUpdateOutcome.UpToDate, await service.StartUpdate());
        Assert.Equal(set.Version, (await service.ResolveForLaunchAsync(CancellationToken.None)).Version);
        Assert.True(service.Status.SupportsMalwareCategories);
    }

    [Fact]
    public async Task TheBundledRetryChoosesItsPortsAgain()
    {
        using var service = CreateService();
        await service.StartUpdate();
        var paqetPortChecks = 0;
        // Free when the connect plans its ports; held, like by a Paqet still exiting, for the retry.
        await using var fixture = await RuntimeFixture.StartAsync(
            routingData: service,
            portIsFree: (_, port) => port != Core.Configuration.XrayJsonConfigurationWriter.PaqetPort || paqetPortChecks++ == 0);
        fixture.Controller.ConnectFailures.Enqueue(new ConnectionTransitionException(
            "connect", EngineKind.Xray, new RoutingDataUnavailableException("slow", new TimeoutException())));

        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.Equal(["connect", "connect"], fixture.Controller.Calls);
        Assert.NotEqual(Core.Configuration.XrayJsonConfigurationWriter.PaqetPort, fixture.Xray.Last!.Ports!.Paqet);
        Assert.Contains("retried with the bundled data", await fixture.Runtime.GetDiagnosticsAsync(CancellationToken.None),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherXrayFailuresAreNotRetried()
    {
        using var service = CreateService();
        await service.StartUpdate();
        await using var fixture = await RuntimeFixture.StartAsync(routingData: service);
        fixture.Controller.ConnectFailures.Enqueue(new ConnectionTransitionException(
            "connect", EngineKind.Xray, new InvalidOperationException("port in use")));

        await Assert.ThrowsAsync<ConnectionTransitionException>(() => fixture.Runtime.ConnectAsync(CancellationToken.None).AsTask());

        Assert.Equal(["connect"], fixture.Controller.Calls);
    }

    [Fact]
    public void XrayGetsNoInheritedXraySettings()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["xray.location.asset"] = @"C:\Elsewhere",
            ["XRAY_LOCATION_CONFDIR"] = @"C:\Confs",
            ["Xray_Location_Asset"] = @"C:\Other",
            ["PATH"] = @"C:\Windows",
        };

        XrayProcessAdapter.ApplyEnvironment(environment, @"C:\Data");

        Assert.Equal(@"C:\Data", environment["XRAY_LOCATION_ASSET"]);
        Assert.Equal(["PATH", "XRAY_LOCATION_ASSET"], environment.Keys.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdatedDataGetsTheLargerStartupBudget()
    {
        var updated = new RoutingDataSet(@"C:\Data", Tag, []);
        Assert.Equal(TimeSpan.FromSeconds(45), XrayProcessAdapter.TimeoutFor(updated, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)));
        Assert.Equal(TimeSpan.FromSeconds(15), XrayProcessAdapter.TimeoutFor(RoutingDataSet.Bundled, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)));
        Assert.Equal(TimeSpan.FromSeconds(45), BundledEngineOptions.CreateXray(RuntimeFixturePaths()).UpdatedDataTimeout);
    }

    [Fact]
    public void AutomaticChecksAreDailyAndRetryAnHourAfterAFailure()
    {
        var checkedAt = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        var now = checkedAt.AddHours(2);
        Assert.Equal(DateTimeOffset.MinValue, RoutingDataService.NextAutomaticCheck(new RoutingDataState(), null, now));
        Assert.Equal(checkedAt.AddDays(1),
            RoutingDataService.NextAutomaticCheck(new RoutingDataState { LastCheckedAt = checkedAt }, null, now));
        Assert.Equal(checkedAt.AddDays(1).AddHours(1),
            RoutingDataService.NextAutomaticCheck(new RoutingDataState { LastCheckedAt = checkedAt }, checkedAt.AddDays(1).AddHours(1), now));
        Assert.Null(RoutingDataService.NextAutomaticCheck(new RoutingDataState { AutomaticUpdates = false }, null, now));
        // A last check in the future (the clock moved back) is due now, not a day after it.
        Assert.Equal(DateTimeOffset.MinValue,
            RoutingDataService.NextAutomaticCheck(new RoutingDataState { LastCheckedAt = now.AddDays(30) }, null, now));
    }

    [Fact]
    public void StoredStateNamingAnotherDirectoryIsIgnored()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "state.json"), $$"""
            {"installedTag":"{{Tag}}","installedDirectory":"..\\..\\Windows","files":[],"automaticUpdates":false}
            """);

        var state = new RoutingDataStore(root, ProtectedDirectoryPolicy.CurrentUser()).Load();

        Assert.False(state.IsInstalled);
        Assert.False(state.AutomaticUpdates);
    }

    [Fact]
    public void ProbeConfigurationNamesEveryRequiredCategory()
    {
        using var probe = JsonDocument.Parse(XrayRoutingDataValidator.CreateProbeConfiguration());
        var rules = probe.RootElement.GetProperty("routing").GetProperty("rules");

        Assert.Equal(RoutingDataRelease.RequiredSiteCategories,
            rules[0].GetProperty("domain").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(RoutingDataRelease.RequiredIpCategories,
            rules[1].GetProperty("ip").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void ErrorsAreDescribedForUsers()
    {
        Assert.Contains("hourly limit", RoutingDataService.Describe(
            new HttpRequestException("rate", null, HttpStatusCode.Forbidden)), StringComparison.Ordinal);
        Assert.Equal("GitHub answered with HTTP 502.", RoutingDataService.Describe(
            new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway)));
        Assert.True(RoutingDataService.Describe(new InvalidDataException(new string('x', 1000))).Length <= 301);
    }

    [Fact]
    public void DiagnosticsNameTheRoutingDataAndItsLastError()
    {
        var text = DiagnosticBundle.Build(new DiagnosticBundleInput(
                null,
                0,
                new ConnectionStatus(ConnectionState.Disconnected,
                    new EngineStatus(EngineKind.Paqet, EngineState.Stopped),
                    new EngineStatus(EngineKind.Xray, EngineState.Stopped),
                    new EngineStatus(EngineKind.ProxiFyre, EngineState.Stopped)),
                TunnelHealthReport.Unknown,
                "Ready",
                [],
                null,
                null,
                [],
                [],
                RoutingData: new RoutingDataStatus(RoutingDataOrigin.Updated, Tag,
                    new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero), null, true, false, true,
                    "GitHub could not be reached.")),
            DateTimeOffset.UtcNow);

        Assert.Contains($"Routing data: Updated · {Tag} · installed 2026-10-22 10:00Z · last checked never · automatic updates on", text, StringComparison.Ordinal);
        Assert.Contains("Routing data error: GitHub could not be reached.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BrokerCommand.ManageRoutingData, null, false)]
    [InlineData(BrokerCommand.ManageRoutingData, (RoutingDataAction)42, false)]
    [InlineData(BrokerCommand.ManageRoutingData, RoutingDataAction.UpdateNow, true)]
    [InlineData(BrokerCommand.GetSnapshot, RoutingDataAction.UseBundled, false)]
    [InlineData(BrokerCommand.GetSnapshot, null, true)]
    public void PipeRequestsCarryARoutingDataActionOnlyForThatCommand(BrokerCommand command, RoutingDataAction? action, bool valid)
    {
        var error = NamedPipeBrokerServer.Validate(new BrokerRequest(
            Guid.NewGuid(), IpcProtocol.Version, command, RoutingDataAction: action));

        Assert.Equal(valid, error is null);
        if (!valid)
        {
            Assert.Equal(BrokerErrorCode.InvalidRequest, error!.Error!.Code);
        }
    }

    [Fact]
    public async Task MalwareBlockingFollowsTheDataXrayWillLoad()
    {
        using var service = CreateService();
        await using var fixture = await RuntimeFixture.StartAsync(routingData: service);

        var snapshot = await fixture.Runtime.ConnectAsync(CancellationToken.None);
        Assert.False(fixture.Xray.Last!.BlockMalware);
        Assert.Same(RoutingDataSet.Bundled, fixture.XrayAdapter.RoutingData);
        Assert.Equal(RoutingDataOrigin.Bundled, snapshot.RoutingData!.Origin);
        Assert.True(snapshot.Settings!.BlockMalware);

        await service.StartUpdate();
        await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        snapshot = await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.True(fixture.Xray.Last!.BlockMalware);
        Assert.True(fixture.XrayAdapter.RoutingData.IsUpdated);
        Assert.Equal(RoutingDataOrigin.Updated, snapshot.RoutingData!.Origin);
    }

    [Fact]
    public async Task MalwareBlockingStaysOffWhenTheProfileTurnsItOff()
    {
        using var service = CreateService();
        await service.StartUpdate();
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with { BlockMalware = false }, routingData: service);

        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.False(fixture.Xray.Last!.BlockMalware);
        Assert.True(fixture.XrayAdapter.RoutingData.IsUpdated);
    }

    [Fact]
    public async Task NewDataWaitsForAnXrayRestartThatHappensAnyway()
    {
        using var service = CreateService();
        await using var fixture = await RuntimeFixture.StartAsync(routingData: service);
        await fixture.Runtime.ConnectAsync(CancellationToken.None);
        await service.StartUpdate();

        // Only Paqet's inputs changed: the running Xray and its data stay.
        fixture.GatewayMac = "02:AA:BB:CC:DD:EE";
        await fixture.Runtime.HandleNetworkChangeAsync(resumed: false, CancellationToken.None);
        Assert.Equal("restart", fixture.Controller.Calls[^1]);
        Assert.False(fixture.Xray.Last!.BlockMalware);
        Assert.False(fixture.XrayAdapter.RoutingData.IsUpdated);

        // Xray restarts for its own inputs and picks up the new data with it.
        fixture.Adapter = fixture.Adapter with { DnsServers = [IPAddress.Parse("192.168.1.53")] };
        await fixture.Runtime.HandleNetworkChangeAsync(resumed: false, CancellationToken.None);
        Assert.Equal("restart+policy", fixture.Controller.Calls[^1]);
        Assert.True(fixture.Xray.Last!.BlockMalware);
        Assert.True(fixture.XrayAdapter.RoutingData.IsUpdated);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
        catch (IOException)
        {
            // Best effort for a temporary directory.
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    private static Runtime.RuntimePaths RuntimeFixturePaths() => new(
        "unused", "unused", "unused", @"C:\xray.exe", "unused", "unused", "unused", "unused", "unused", "unused");

    private RoutingDataService CreateService(RoutingDataServiceOptions? options = null, Func<IEnumerable<string?>>? inUse = null) => new(
        options ?? new RoutingDataServiceOptions { BundledXrayVersion = "26.3.27" },
        new RoutingDataStore(root, ProtectedDirectoryPolicy.CurrentUser()),
        source,
        validator,
        new RoutingDataFileVerifier(ProtectedDirectoryPolicy.CurrentUser()),
        NullLogger<RoutingDataService>.Instance,
        _ => ValueTask.FromResult(route),
        timeProvider: clock,
        inUse: inUse);

    private sealed class FakeRoutingDataSource : IRoutingDataSource
    {
        public string Tag { get; set; } = string.Empty;

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal)
        {
            ["geoip.dat"] = Encoding.ASCII.GetBytes("geoip with malware and phishing"),
            ["geosite.dat"] = Encoding.ASCII.GetBytes("geosite with category-ir and cryptominers"),
        };

        public Dictionary<string, string> ChecksumOverrides { get; } = new(StringComparer.Ordinal);

        public RoutingDataNetworkPath? FailingPath { get; set; }

        public Task? TagGate { get; set; }

        public bool IgnoreCancellation { get; set; }

        public List<string> Calls { get; } = [];

        public async Task<string> GetLatestTagAsync(RoutingDataNetworkPath path, CancellationToken cancellationToken)
        {
            Calls.Add($"tag:{path}");
            if (TagGate is not null)
            {
                await (IgnoreCancellation ? TagGate : TagGate.WaitAsync(cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }

            return path == FailingPath ? throw new HttpRequestException("unreachable") : Tag;
        }

        public async Task DownloadAsync(
            RoutingDataNetworkPath path,
            Uri uri,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            var name = uri.Segments[^1];
            Calls.Add($"{name}:{path}");
            Assert.Contains($"/download/{Tag}/", uri.AbsolutePath, StringComparison.Ordinal);
            if (path == FailingPath)
            {
                throw new HttpRequestException("unreachable");
            }

            byte[] body;
            if (name.EndsWith(".sha256sum", StringComparison.Ordinal))
            {
                var file = name[..^".sha256sum".Length];
                var digest = ChecksumOverrides.TryGetValue(file, out var forced)
                    ? forced
                    : Convert.ToHexStringLower(SHA256.HashData(Files[file]));
                body = Encoding.ASCII.GetBytes($"{digest}  release/{file}\n");
            }
            else
            {
                body = Files[name];
            }

            Assert.True(body.Length <= maximumBytes);
            await destination.WriteAsync(body, cancellationToken);
        }
    }

    private sealed class FakeRoutingDataValidator : IRoutingDataValidator
    {
        public bool Reject { get; set; }

        public List<string> Directories { get; } = [];

        public Task ValidateAsync(string assetDirectory, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(Path.Combine(assetDirectory, RoutingDataRelease.GeoIpFileName)));
            Assert.True(File.Exists(Path.Combine(assetDirectory, RoutingDataRelease.GeoSiteFileName)));
            if (Reject)
            {
                throw new InvalidDataException("Xray rejected the downloaded routing data: geosite:malware not found");
            }

            Directories.Add(assetDirectory);
            return Task.CompletedTask;
        }
    }
}
