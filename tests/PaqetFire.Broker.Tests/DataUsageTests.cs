using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Ipc;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Usage;
using PaqetFire.Desktop.Presentation;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class DataUsageTests
{
    private static readonly DateOnly Day = new(2026, 10, 26);
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Work = Guid.NewGuid();

    [Fact]
    public void LedgerMergesDaysAndKeepsOnlyTheRetentionWindow()
    {
        var ledger = new DataUsageLedger();
        ledger.Add(Home, Day, 100, 10);
        ledger.Add(Home, Day, 50, 5);
        ledger.Add(Home, Day.AddDays(-DataUsageLedger.RetentionDays), 7, 7);
        ledger.Add(Work, Day.AddDays(-(DataUsageLedger.RetentionDays - 1)), 1, 1);
        ledger.Add(Work, Day, 0, 0);

        Assert.True(ledger.Prune(Day));

        var profiles = ledger.ToProfiles().ToDictionary(profile => profile.ProfileId);
        Assert.Equal([new DailyDataUsage(Day, 150, 15)], profiles[Home].Days);
        Assert.Equal([new DailyDataUsage(Day.AddDays(-(DataUsageLedger.RetentionDays - 1)), 1, 1)], profiles[Work].Days);
        Assert.False(ledger.Prune(Day));
    }

    [Fact]
    public void LedgerResetsOneProfileOrAll()
    {
        var ledger = new DataUsageLedger([
            new ProfileDataUsage(Home, [new DailyDataUsage(Day, 1, 1)]),
            new ProfileDataUsage(Work, [new DailyDataUsage(Day, 2, 2)]),
        ]);

        Assert.True(ledger.Reset(Home));
        Assert.Equal([Work], ledger.ToProfiles().Select(profile => profile.ProfileId));
        Assert.False(ledger.Reset(Home));
        Assert.True(ledger.Reset(null));
        Assert.True(ledger.IsEmpty);
    }

    [Fact]
    public void LedgerSaturatesInsteadOfOverflowing()
    {
        var ledger = new DataUsageLedger();
        ledger.Add(Home, Day, long.MaxValue - 1, 0);
        ledger.Add(Home, Day, 10, 0);

        Assert.Equal(long.MaxValue, ledger.ToProfiles().Single().Days.Single().DownloadBytes);
    }

    [Fact]
    public async Task RecorderAddsOnlyWhatGrewSinceTheLastSample()
    {
        var fixture = new RecorderFixture();
        fixture.Start(Home);

        fixture.Counters = new XrayTrafficCounters(UplinkBytes: 100, DownlinkBytes: 1000);
        await fixture.Recorder.CheckpointAsync(CancellationToken.None);
        fixture.Counters = new XrayTrafficCounters(UplinkBytes: 150, DownlinkBytes: 4000);
        var report = await fixture.Recorder.GetReportAsync(CancellationToken.None);

        Assert.Equal(Day, report.Today);
        Assert.Equal([new DailyDataUsage(Day, 4000, 150)], report.Profiles.Single(profile => profile.ProfileId == Home).Days);
    }

    [Fact]
    public async Task ANewXrayRunOrACounterDropStartsCountingFromZero()
    {
        var fixture = new RecorderFixture();
        fixture.Start(Home);
        fixture.Counters = new XrayTrafficCounters(0, 5000);
        await fixture.Recorder.CheckpointAsync(CancellationToken.None);

        // Xray restarted; its counters begin again, already above the previous total.
        fixture.Start(Home);
        fixture.Counters = new XrayTrafficCounters(0, 6000);
        await fixture.Recorder.CheckpointAsync(CancellationToken.None);

        // Same run, but the counters went backwards.
        fixture.Counters = new XrayTrafficCounters(0, 10);
        var report = await fixture.Recorder.GetReportAsync(CancellationToken.None);

        Assert.Equal(11_010, report.Profiles.Single().Days.Single().DownloadBytes);
    }

    [Fact]
    public async Task ARunStaysChargedToTheProfileItStartedWith()
    {
        var fixture = new RecorderFixture();
        fixture.Start(Home);
        fixture.Counters = new XrayTrafficCounters(0, 100);
        await fixture.Recorder.CheckpointAsync(CancellationToken.None);

        // A profile switch saves the new profile before the old route stops.
        fixture.Session = fixture.Session! with { ProfileId = Work };
        fixture.Counters = new XrayTrafficCounters(0, 300);
        await fixture.Recorder.CheckpointAsync(CancellationToken.None);

        fixture.Start(Work);
        fixture.Counters = new XrayTrafficCounters(0, 40);
        var report = await fixture.Recorder.GetReportAsync(CancellationToken.None);

        var byProfile = report.Profiles.ToDictionary(profile => profile.ProfileId, profile => profile.Days.Single().DownloadBytes);
        Assert.Equal(300, byProfile[Home]);
        Assert.Equal(40, byProfile[Work]);
    }

    [Fact]
    public async Task NothingIsRecordedWhileXrayIsNotRunning()
    {
        var fixture = new RecorderFixture();
        fixture.Counters = new XrayTrafficCounters(10, 10);

        var report = await fixture.Recorder.GetReportAsync(CancellationToken.None);

        Assert.Empty(report.Profiles);
        Assert.Equal(0, fixture.Stats.Queries);
    }

    [Fact]
    public async Task TrafficIsChargedToTheLocalDayItWasCountedOn()
    {
        var fixture = new RecorderFixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 10, 26, 23, 59, 0, TimeSpan.Zero);
        fixture.Start(Home);
        fixture.Counters = new XrayTrafficCounters(0, 100);
        await fixture.Recorder.CheckpointAsync(CancellationToken.None);

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        fixture.Counters = new XrayTrafficCounters(0, 250);
        var report = await fixture.Recorder.GetReportAsync(CancellationToken.None);

        Assert.Equal(Day.AddDays(1), report.Today);
        Assert.Equal(
            [new DailyDataUsage(Day, 100, 0), new DailyDataUsage(Day.AddDays(1), 150, 0)],
            report.Profiles.Single().Days);
    }

    [Fact]
    public async Task ResetClearsWhatTheCurrentRunMovedWithoutCountingItAgain()
    {
        var fixture = new RecorderFixture();
        fixture.Start(Home);
        fixture.Counters = new XrayTrafficCounters(0, 100);
        await fixture.Recorder.CheckpointAsync(CancellationToken.None);
        fixture.Counters = new XrayTrafficCounters(0, 300);

        var cleared = await fixture.Recorder.ResetAsync(new DataUsageReset(null), CancellationToken.None);
        fixture.Counters = new XrayTrafficCounters(0, 350);
        var report = await fixture.Recorder.GetReportAsync(CancellationToken.None);

        Assert.Empty(cleared.Profiles);
        Assert.Empty(fixture.Store.Load());
        Assert.Equal(50, report.Profiles.Single().Days.Single().DownloadBytes);
    }

    [Fact]
    public async Task HistoryIsLoadedOnceAndSavedAtCheckpoints()
    {
        var fixture = new RecorderFixture();
        await fixture.Store.SaveAsync([new ProfileDataUsage(Home, [new DailyDataUsage(Day.AddDays(-1), 9, 9)])], CancellationToken.None);
        fixture.Start(Home);
        fixture.Counters = new XrayTrafficCounters(1, 1);

        await fixture.Recorder.CheckpointAsync(CancellationToken.None);

        Assert.Equal(
            [new DailyDataUsage(Day.AddDays(-1), 9, 9), new DailyDataUsage(Day, 1, 1)],
            fixture.Store.Load().Single().Days);
    }

    [Fact]
    public async Task UnavailableCountersAreSkipped()
    {
        var fixture = new RecorderFixture();
        fixture.Start(Home);
        fixture.Stats.Failure = new HttpRequestException("Xray's API is not ready.");

        var report = await fixture.Recorder.GetReportAsync(CancellationToken.None);

        Assert.Empty(report.Profiles);
    }

    [Fact]
    public void StoreTreatsMissingOrCorruptHistoryAsEmpty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-usage-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data-usage.json");
        try
        {
            Assert.Empty(new DataUsageStore(path).Load());
            File.WriteAllText(path, "{ not json");
            Assert.Empty(new DataUsageStore(path).Load());
            File.WriteAllText(path, """{"version":99,"profiles":[]}""");
            Assert.Empty(new DataUsageStore(path).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [ElevatedFact]
    public async Task StoreRoundTripsHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-usage-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data-usage.json");
        try
        {
            var usage = new[] { new ProfileDataUsage(Home, [new DailyDataUsage(Day, 300_000_000_000, 12)]) };
            await new DataUsageStore(path).SaveAsync(usage, CancellationToken.None);

            var loaded = new DataUsageStore(path).Load().Single();
            Assert.Equal(Home, loaded.ProfileId);
            Assert.Equal(usage[0].Days, loaded.Days);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(BrokerCommand.ResetDataUsage, false, false)]
    [InlineData(BrokerCommand.ResetDataUsage, true, true)]
    [InlineData(BrokerCommand.GetDataUsage, false, true)]
    [InlineData(BrokerCommand.GetDataUsage, true, false)]
    public void PipeRequestsCarryAResetScopeOnlyForAReset(BrokerCommand command, bool withScope, bool valid)
    {
        var error = NamedPipeBrokerServer.Validate(new BrokerRequest(
            Guid.NewGuid(), IpcProtocol.Version, command, DataUsageReset: withScope ? new DataUsageReset(Home) : null));

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void ReportsSurviveTheIpcSerializer()
    {
        var response = new BrokerResponse(
            Guid.NewGuid(),
            IpcProtocol.Version,
            true,
            DataUsage: new DataUsageReport(Day, [new ProfileDataUsage(Home, [new DailyDataUsage(Day, 5, 6)])]));

        var json = JsonSerializer.Serialize(response, IpcJson.SerializerOptions);
        var parsed = JsonSerializer.Deserialize<BrokerResponse>(json, IpcJson.SerializerOptions)!;

        Assert.DoesNotContain("totalBytes", json, StringComparison.Ordinal);
        Assert.Equal(Day, parsed.DataUsage!.Today);
        Assert.Equal(response.DataUsage!.Profiles[0].Days, parsed.DataUsage.Profiles[0].Days);
    }

    [Fact]
    public void SummaryTotalsTodayTheMonthAndTheLastThirtyDays()
    {
        var report = new DataUsageReport(Day, [
            new ProfileDataUsage(Home, [
                new DailyDataUsage(Day, 2048, 1024),
                new DailyDataUsage(Day.AddDays(-1), 1024, 0),
                new DailyDataUsage(new DateOnly(2026, 9, 30), 4096, 0),
                new DailyDataUsage(Day.AddDays(-40), 999_999, 0),
            ]),
            new ProfileDataUsage(Work, [new DailyDataUsage(Day, 1024, 0)]),
        ]);

        var all = DataUsageText.Summarize(report, profileId: null);
        var home = DataUsageText.Summarize(report, Home);

        Assert.Equal(new DataUsageTotals(3072, 1024), all.Today);
        Assert.Equal(new DataUsageTotals(4096, 1024), all.ThisMonth);
        Assert.Equal(new DataUsageTotals(8192, 1024), all.RecentDays);
        Assert.Equal(new DataUsageTotals(2048, 1024), home.Today);
        Assert.Equal("3.0 KB", home.Today.TotalText);
        Assert.Equal("↓ 2.0 KB   ↑ 1.0 KB", home.Today.DetailText);
        Assert.Equal(["Today", "Yesterday", DataUsageText.FormatDay(new DateOnly(2026, 9, 30), Day)], home.Days.Select(row => row.DateText));
        Assert.Equal([75, 25, 100], home.Days.Select(row => row.Share));
    }

    [Fact]
    public void SummaryOfNoUsageIsEmpty()
    {
        var summary = DataUsageText.Summarize(new DataUsageReport(Day, []), Home);

        Assert.Empty(summary.Days);
        Assert.Equal("0 B", summary.RecentDays.TotalText);
    }

    private sealed class RecorderFixture
    {
        public RecorderFixture()
        {
            Stats = new CountersStub(this);
            Recorder = new DataUsageRecorder(
                Stats,
                _ => new(Session),
                Store,
                NullLogger<DataUsageRecorder>.Instance,
                Clock);
        }

        public UtcClock Clock { get; } = new() { Now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };

        public InMemoryDataUsageStore Store { get; } = new();

        public CountersStub Stats { get; }

        public DataUsageRecorder Recorder { get; }

        public TrafficSession? Session { get; set; }

        public XrayTrafficCounters? Counters { get; set; }

        public void Start(Guid profileId)
        {
            Clock.Now = Clock.Now.AddSeconds(1);
            Session = new TrafficSession(Clock.Now, profileId);
            Counters = new XrayTrafficCounters(0, 0);
        }
    }

    private sealed class CountersStub(RecorderFixture fixture) : IXrayTrafficStats
    {
        public int Queries { get; private set; }

        public Exception? Failure { get; set; }

        public ValueTask<XrayTrafficCounters?> QueryAsync(CancellationToken cancellationToken)
        {
            Queries++;
            return Failure is { } failure ? throw failure : new(fixture.Counters);
        }
    }

    private sealed class UtcClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
