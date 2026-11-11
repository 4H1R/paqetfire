using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class EngineDiagnosticsTests
{
    [Fact]
    public void AFailureKeepsTheReasonFromDeepInItsCauses()
    {
        var error = new ConnectionTransitionException(
            "connect",
            EngineKind.Xray,
            new RoutingDataUnavailableException(
                "Xray did not open its local policy endpoint in time with the downloaded routing data.",
                new TimeoutException("Xray was still running but had not opened the port.")));

        var text = FailureText.Describe(error);

        Assert.StartsWith("Xray failed during connect.", text, StringComparison.Ordinal);
        Assert.Contains("downloaded routing data", text, StringComparison.Ordinal);
        Assert.EndsWith("had not opened the port.", text, StringComparison.Ordinal);
        Assert.Equal(text["Xray failed during connect. ".Length..], FailureText.DescribeCause(error));
    }

    [Fact]
    public void PortOwnersNameTheProcessThatListens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = Assert.Single(PortOwners.Find(port), entry => entry.Protocol == ProtocolType.Tcp);

            Assert.Equal(Environment.ProcessId, owner.ProcessId);
            Assert.Equal(Process.GetCurrentProcess().ProcessName, owner.ProcessName);
            Assert.Contains($"PID {Environment.ProcessId}", PortOwners.Describe(port), StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ATimeoutWithTheEngineListeningSaysTheConnectionsFailed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;

            // The test process stands in for the engine that owns the listener.
            var text = ReadinessReport.Describe(
                "Xray", Process.GetCurrentProcess(), endpoint, TimeSpan.FromSeconds(45), "the connection was reset",
                ["core: Xray 26.10.31 started"], "unused");

            Assert.Contains("within 45 seconds", text, StringComparison.Ordinal);
            Assert.Contains("Xray listens on the port, but connections to it failed: the connection was reset.", text, StringComparison.Ordinal);
            Assert.EndsWith("Xray's last output: core: Xray 26.10.31 started", text, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ATimeoutBeforeTheEngineOpensItsPortSaysSoAndSuggestsLogging()
    {
        int port;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        var text = ReadinessReport.Describe(
            "Paqet", Process.GetCurrentProcess(), new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromSeconds(30), null,
            [], "Its packet capture setup did not finish.", "Set the log level to info.");

        Assert.Contains("Paqet was still running but had not opened the port. Its packet capture setup did not finish.", text,
            StringComparison.Ordinal);
        Assert.EndsWith("Paqet printed nothing. Set the log level to info.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimeoutWithAnotherProgramOnThePortNamesIt()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        other.WaitForExit();
        try
        {
            var text = ReadinessReport.Describe(
                "Paqet", other, (IPEndPoint)listener.LocalEndpoint, TimeSpan.FromSeconds(30), null, [], "unused");

            Assert.Contains("Its process had exited with code 0.", text, StringComparison.Ordinal);
            Assert.Contains($"The port is held by {Process.GetCurrentProcess().ProcessName} (PID {Environment.ProcessId}), not Paqet.",
                text, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }
}
