using System.Buffers.Binary;
using System.Net;
using System.Text;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Ipc;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class LiveStatsTests
{
    [Fact]
    public void QueryRequestEncodesPatternAsField1()
    {
        var encoded = XrayStatsClient.EncodeQueryRequest("abc");

        Assert.Equal(new byte[] { 0x0A, 0x03, (byte)'a', (byte)'b', (byte)'c' }, encoded);
    }

    [Fact]
    public void CountersAreParsedFromGrpcFrame()
    {
        var message = Concat(
            Field(1, Stat("outbound>>>paqet>>>traffic>>>uplink", 1234)),
            Field(1, Stat("outbound>>>paqet>>>traffic>>>downlink", 300_000_000_000)));
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        message.CopyTo(frame, 5);

        var counters = XrayStatsClient.ParseCounters(frame);

        Assert.Equal(1234, counters.UplinkBytes);
        Assert.Equal(300_000_000_000, counters.DownlinkBytes);
    }

    [Fact]
    public void EmptyGrpcResponseMeansNoTrafficYet()
    {
        var counters = XrayStatsClient.ParseCounters([0, 0, 0, 0, 0]);

        Assert.Equal(new XrayTrafficCounters(0, 0), counters);
    }

    [Fact]
    public void GeoIpLookupMatchesCountryCidrAndSkipsNonCountrySets()
    {
        var list = Concat(
            Field(1, Concat(Field(1, Encoding.ASCII.GetBytes("private")), Field(2, Cidr([10, 0, 0, 0], 8)))),
            Field(1, Concat(Field(1, Encoding.ASCII.GetBytes("ir")), Field(2, Cidr([5, 160, 0, 0], 14)))),
            Field(1, Concat(Field(1, Encoding.ASCII.GetBytes("us")), Field(2, Cidr([8, 8, 8, 0], 24)))));

        Assert.Equal("IR", GeoIpCountryLookup.FindCountryCode(list, IPAddress.Parse("5.163.255.1").GetAddressBytes()));
        Assert.Equal("US", GeoIpCountryLookup.FindCountryCode(list, IPAddress.Parse("8.8.8.8").GetAddressBytes()));
        Assert.Null(GeoIpCountryLookup.FindCountryCode(list, IPAddress.Parse("5.164.0.1").GetAddressBytes()));
        Assert.Null(GeoIpCountryLookup.FindCountryCode(list, IPAddress.Parse("10.1.2.3").GetAddressBytes()));
    }

    [Fact]
    public void XrayConfigurationEnablesLoopbackStatsForPaqetOutbound()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.IranDirect,
            XrayDomainStrategy.IPIfNonMatch,
            true,
            true,
            false,
            true,
            null,
            null,
            []));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal($"127.0.0.1:{XrayJsonConfigurationWriter.StatsApiPort}", root.GetProperty("api").GetProperty("listen").GetString());
        Assert.Equal(["StatsService"], root.GetProperty("api").GetProperty("services").EnumerateArray().Select(item => item.GetString()));
        Assert.True(root.GetProperty("policy").GetProperty("system").GetProperty("statsOutboundDownlink").GetBoolean());
        Assert.True(root.GetProperty("policy").GetProperty("system").GetProperty("statsOutboundUplink").GetBoolean());
    }

    [Fact]
    public void ViewModelDerivesRatesTotalsAndElapsedTime()
    {
        var since = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var live = new LiveStatsViewModel();

        live.Apply(new LiveConnectionStats(since.AddSeconds(15), true, since, 1000, 10_000));
        live.Apply(new LiveConnectionStats(since.AddSeconds(17), true, since, 3048, 2_107_152, 147, since.AddSeconds(16), "104.28.214.164", "IR"));

        Assert.Equal("00:00:17", live.ElapsedText);
        Assert.Equal("1.0 KB/s", live.UploadRateText);
        Assert.Equal("1.0 MB/s", live.DownloadRateText);
        Assert.Equal("Total 2.0 MB", live.DownloadTotalText);
        Assert.Equal("147 ms", live.LatencyText);
        Assert.Equal(LatencyQuality.Good, live.Quality);
        Assert.Equal("IR", live.CountryCode);
        Assert.Equal("104.28.214.164", live.PublicAddressText);
        Assert.Equal(LiveStatsViewModel.PingHistoryLength, live.PingBars.Count);
        Assert.Equal(1, live.PingBars[^1].Opacity);
    }

    [Fact]
    public void ViewModelTreatsCounterDropAsXrayRestartAndResetsOnNewSession()
    {
        var since = DateTimeOffset.UnixEpoch;
        var live = new LiveStatsViewModel();
        live.Apply(new LiveConnectionStats(since.AddSeconds(1), true, since, 5000, 5000));

        live.Apply(new LiveConnectionStats(since.AddSeconds(2), true, since, 10, 10));
        Assert.Equal("0 B/s", live.DownloadRateText);

        live.Apply(new LiveConnectionStats(since.AddSeconds(100), true, since.AddSeconds(99), 0, 0));
        Assert.Equal("00:00:01", live.ElapsedText);
        Assert.Equal("—", live.LatencyText);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(19_251, "18.8 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void BytesAreFormattedWithBinaryUnits(long bytes, string expected) =>
        Assert.Equal(expected, LiveStatsText.FormatBytes(bytes));

    [Fact]
    public void ElapsedTimeIncludesDaysForLongSessions() =>
        Assert.Equal("1d 02:03:04", LiveStatsText.FormatElapsed(new TimeSpan(1, 2, 3, 4)));

    private static byte[] Stat(string name, long value) =>
        Concat(Field(1, Encoding.UTF8.GetBytes(name)), [0x10, .. Varint((ulong)value)]);

    private static byte[] Cidr(byte[] address, int prefix) =>
        Concat(Field(1, address), [0x10, .. Varint((ulong)prefix)]);

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
