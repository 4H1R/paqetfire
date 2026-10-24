using System.Buffers.Binary;
using System.Text;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class PsiphonExitTests
{
    [Fact]
    public void NoticesReportTheTunnelAndTheCountriesOnOffer()
    {
        var adapter = CreateAdapter();

        adapter.HandleOutput("""{"data":{"regions":["US","DE","NL"]},"noticeType":"AvailableEgressRegions","timestamp":"2026-10-24T10:00:00Z"}""");
        adapter.HandleOutput("""{"data":{"serverRegion":"DE"},"noticeType":"ConnectedServerRegion","timestamp":"2026-10-24T10:00:01Z"}""");
        adapter.HandleOutput("""{"data":{"count":1},"noticeType":"Tunnels","timestamp":"2026-10-24T10:00:02Z"}""");

        var session = adapter.Session;
        Assert.True(session.TunnelActive);
        Assert.Equal("DE", session.ConnectedRegion);
        Assert.Equal(["DE", "NL", "US"], session.AvailableRegions);

        adapter.HandleOutput("""{"data":{"count":0},"noticeType":"Tunnels","timestamp":"2026-10-24T10:00:03Z"}""");
        Assert.False(adapter.Session.TunnelActive);
    }

    [Fact]
    public void LogsNeverCarryTheUpstreamCredentials()
    {
        var adapter = CreateAdapter();

        adapter.HandleOutput("""{"data":{"message":"dial socks5://engine-user:secret-password@127.0.0.1:1080 failed"},"noticeType":"UpstreamProxyError","timestamp":"t"}""");
        adapter.HandleOutput("plain line with socks5://user:other-secret@127.0.0.1:1080");

        var logs = string.Join("\n", adapter.GetRecentLogs().Select(entry => entry.Message));
        Assert.DoesNotContain("secret-password", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret", logs, StringComparison.Ordinal);
        Assert.Contains("socks5://***@127.0.0.1:1080", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientRegionIsNotLogged()
    {
        var adapter = CreateAdapter();

        adapter.HandleOutput("""{"data":{"region":"IR"},"noticeType":"ClientRegion","timestamp":"t"}""");

        Assert.Empty(adapter.GetRecentLogs());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"noticeType\":5}")]
    [InlineData("{broken")]
    public void MalformedOutputIsKeptAsPlainText(string line)
    {
        Assert.False(PsiphonProcessAdapter.TryParseNotice(line, out _, out _));
        var adapter = CreateAdapter();
        adapter.HandleOutput(line);
        Assert.Equal(line, Assert.Single(adapter.GetRecentLogs()).Message);
    }

    [Fact]
    public async Task TheExitIsDisabledUntilAConfigurationSelectsIt()
    {
        var adapter = CreateAdapter();
        Assert.Equal(EngineKind.Psiphon, adapter.Kind);
        Assert.False(adapter.IsEnabled);

        adapter.UseExit(true);
        Assert.True(adapter.IsEnabled);
        Assert.Equal(EngineState.NotInstalled, (await adapter.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public void TrafficThroughTheExitCountsAndDirectTrafficDoesNot()
    {
        var message = Concat(
            Field(1, Stat("outbound>>>paqet>>>traffic>>>uplink", 10)),
            Field(1, Stat("outbound>>>exit>>>traffic>>>uplink", 5)),
            Field(1, Stat("outbound>>>exit>>>traffic>>>downlink", 700)),
            Field(1, Stat("outbound>>>direct>>>traffic>>>downlink", 9_000)),
            Field(1, Stat("outbound>>>dns-out>>>traffic>>>uplink", 3)));
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        message.CopyTo(frame, 5);

        var counters = XrayStatsClient.ParseCounters(frame);

        Assert.Equal(15, counters.UplinkBytes);
        Assert.Equal(700, counters.DownlinkBytes);
    }

    [Fact]
    public void FormStateRoundTripsTheExitProxy()
    {
        var original = new PaqetFireSettings
        {
            ServerEndpoint = "vpn.example.com:9999",
            ExitProxy = ExitProxyKind.Psiphon,
            Psiphon = new PsiphonExitOptions("JP"),
        };

        var form = ConnectionFormState.FromSettings(PaqetFireSettingsView.FromSettings(original));
        var settings = form.ToSettings(string.Empty, string.Empty);

        Assert.Equal(ExitProxyKind.Psiphon, form.ExitProxy);
        Assert.Equal("JP", form.PsiphonRegion);
        Assert.Equal(original.ExitProxy, settings.ExitProxy);
        Assert.Equal(original.Psiphon, settings.Psiphon);
        Assert.False(form.IsEquivalentTo(form with { PsiphonRegion = "US" }));
        Assert.True(form.IsEquivalentTo(form with { PsiphonRegion = " jp " }));
    }

    [Fact]
    public void ProgressShowsTheExitStepOnlyForProfilesThatUseIt()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new ConnectionProgress(ConnectionStage.StartingPolicy, now, now);

        Assert.DoesNotContain(ConnectionProgressText.CreateSteps(policy), step => step.Label == "Connecting the exit proxy");
        Assert.Contains(ConnectionProgressText.CreateSteps(policy, withExit: true), step => step.Label == "Connecting the exit proxy");
        Assert.StartsWith("Step 5 of 7", ConnectionProgressText.CreateSummary(
            new ConnectionProgress(ConnectionStage.StartingExit, now, now), now), StringComparison.Ordinal);
    }

    private static PsiphonProcessAdapter CreateAdapter() => new(new PsiphonProcessOptions
    {
        ExecutablePath = Path.Combine(Path.GetTempPath(), "missing-psiphon", "psiphon.exe"),
        ConfigurationPath = Path.Combine(Path.GetTempPath(), "missing-psiphon", "config.json"),
        DataDirectory = Path.Combine(Path.GetTempPath(), "missing-psiphon", "data"),
        ExpectedExecutableSha256 = new string('0', 64),
        Version = "test",
        DataDirectoryPolicy = null,
    });

    private static byte[] Stat(string name, long value) =>
        Concat(Field(1, Encoding.UTF8.GetBytes(name)), [0x10, .. Varint((ulong)value)]);

    private static byte[] Field(int number, byte[] value) =>
        [(byte)((number << 3) | 2), .. Varint((ulong)value.Length), .. value];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
