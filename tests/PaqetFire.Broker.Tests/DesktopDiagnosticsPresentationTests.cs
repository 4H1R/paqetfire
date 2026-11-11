using PaqetFire.Core.Diagnostics;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class DesktopDiagnosticsPresentationTests
{
    [Fact]
    public void VerificationRowsListProblemsFirstAndKeepReportOrderWithinAGroup()
    {
        var report = Report(
            Check(VerificationCheckKind.EngineChain, VerificationCheckStatus.Passed, "Engine chain"),
            Check(VerificationCheckKind.ApplicationCapture, VerificationCheckStatus.Info, "Capture process"),
            Check(VerificationCheckKind.Ipv6, VerificationCheckStatus.Skipped, "IPv6 route"),
            Check(VerificationCheckKind.UdpDns, VerificationCheckStatus.Warning, "Routed UDP/DNS"),
            Check(VerificationCheckKind.TcpIpv4, VerificationCheckStatus.Failed, "TCP over IPv4"),
            Check(VerificationCheckKind.LocalRoute, VerificationCheckStatus.Passed, "Local Xray route"),
            Check(VerificationCheckKind.DnsLeak, VerificationCheckStatus.Failed, "DNS leak"));

        var rows = VerificationDisplayItem.FromReport(report);

        Assert.Equal(
            ["TCP over IPv4", "DNS leak", "Routed UDP/DNS", "Engine chain", "Capture process", "Local Xray route", "IPv6 route"],
            rows.Select(row => row.Name));
        Assert.Equal(
            [StatusTone.Critical, StatusTone.Critical, StatusTone.Caution, StatusTone.Success, StatusTone.Neutral, StatusTone.Success, StatusTone.Neutral],
            rows.Select(row => row.Tone));
    }

    [Fact]
    public void EveryFailedOrInconclusiveCheckSaysWhatToDo()
    {
        foreach (var kind in Enum.GetValues<VerificationCheckKind>())
        {
            foreach (var status in new[] { VerificationCheckStatus.Failed, VerificationCheckStatus.Warning })
            {
                var remedy = VerificationRemedy.For(kind, status);
                Assert.NotNull(remedy);
                Assert.False(string.IsNullOrWhiteSpace(remedy.Text));
                Assert.Equal(remedy.ActionText is null, remedy.Destination is null);
            }
        }
    }

    [Theory]
    [InlineData(VerificationCheckStatus.Passed)]
    [InlineData(VerificationCheckStatus.Info)]
    [InlineData(VerificationCheckStatus.Skipped)]
    public void ChecksThatNeedNothingHaveNoRemedy(VerificationCheckStatus status) =>
        Assert.Null(VerificationRemedy.For(VerificationCheckKind.DnsLeak, status));

    [Theory]
    [InlineData(VerificationCheckKind.TcpIpv4, VerificationRemedy.ConnectionPage)]
    [InlineData(VerificationCheckKind.PublicAddress, VerificationRemedy.ConnectionPage)]
    [InlineData(VerificationCheckKind.DnsLeak, VerificationRemedy.RoutingPage)]
    [InlineData(VerificationCheckKind.Ipv6, VerificationRemedy.RoutingPage)]
    [InlineData(VerificationCheckKind.LocalRoute, null)]
    public void FailuresLinkToThePageThatFixesThem(VerificationCheckKind kind, string? destination)
    {
        var row = VerificationDisplayItem.FromCheck(Check(kind, VerificationCheckStatus.Failed, "Check"));

        Assert.Equal(destination ?? string.Empty, row.ActionDestination);
        Assert.Equal(destination is not null, row.HasAction);
        Assert.True(row.HasRemedy);
    }

    [Fact]
    public void PassedRowsCarryNoRemedyAndShowTheObservedValue()
    {
        var row = VerificationDisplayItem.FromCheck(new ConnectionVerificationCheck(
            VerificationCheckKind.PublicAddress, VerificationCheckStatus.Passed, "Proxied public address", "Returned.", 42, "198.51.100.10"));

        Assert.False(row.HasRemedy);
        Assert.False(row.HasAction);
        Assert.Equal("Returned. Observed: 198.51.100.10", row.Detail);
        Assert.Equal("42 ms", row.Duration);
    }

    [Theory]
    [InlineData("ERROR", StatusTone.Critical)]
    [InlineData("WARN", StatusTone.Caution)]
    [InlineData("INFO", StatusTone.Accent)]
    [InlineData("ENGINE", StatusTone.Accent)]
    public void ActivityLevelsHaveASeverityTone(string level, StatusTone expected) =>
        Assert.Equal(expected, StatusToneText.ForActivityLevel(level));

    private static ConnectionVerificationCheck Check(VerificationCheckKind kind, VerificationCheckStatus status, string name) =>
        new(kind, status, name, "Detail.");

    private static ConnectionVerificationReport Report(params ConnectionVerificationCheck[] checks) =>
        new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, checks);
}
