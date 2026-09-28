using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class PaqetAdvancedOptionsTests
{
    private static PaqetProfile Profile(string mode = "fast") => new("server.example:8443", "127.0.0.1:1080",
        "Ethernet", "7b35c9bb-cf61-4c14-9f3d-53b7468c7cc8", "192.168.1.20", "aa:bb:cc:dd:ee:ff", ["S", "S", "AN"], ["PA"], mode);

    [Fact]
    public void FullTuningFlowsFromApplicationSettingsIntoPaqetYaml()
    {
        var options = new PaqetAdvancedOptions
        {
            LogLevel = "warn", LocalIpv4Port = 43210, LocalIpv6Endpoint = "[2001:db8::1]:43210",
            Ipv6RouterMac = "aa:bb:cc:dd:ee:ff", PcapSocketBufferBytes = 1024, ConnectionCount = 1,
            KcpNoDelay = 1, KcpIntervalMilliseconds = 20, KcpResend = 2, KcpNoCongestion = 1,
            KcpWriteDelay = false, KcpAckNoDelay = true, KcpMtu = 1200, KcpReceiveWindow = 32768,
            KcpSendWindow = 256, KcpBlock = "aes-128-gcm", SmuxBufferBytes = 4194304, StreamBufferBytes = 2097152,
            SmuxKeepAliveSeconds = 4, SmuxKeepAliveTimeoutSeconds = 10, FecDataShards = 10, FecParityShards = 3,
        };
        var settings = new PaqetFireSettings { ServerEndpoint = "server.example:8443", TransportKey = "test-key", KcpMode = "manual", Advanced = options };
        Assert.Empty(PaqetFireSettingsValidator.Validate(settings));
        Assert.Equal(options, PaqetFireSettingsView.FromSettings(settings).Advanced);
        var yaml = new PaqetYamlConfigurationWriter().Write(options.ApplyTo(Profile("manual")), settings.TransportKey);
        foreach (var field in new[] { "level: \"warn\"", "192.168.1.20:43210", "[2001:db8::1]:43210", "sockbuf: 1024",
                     "conn: 1", "nodelay: 1", "interval: 20", "resend: 2", "nocongestion: 1", "wdelay: false", "acknodelay: true",
                     "mtu: 1200", "rcvwnd: 32768", "sndwnd: 256", "block: \"aes-128-gcm\"", "smuxbuf: 4194304",
                     "streambuf: 2097152", "smuxkalive: 4", "smuxktimeout: 10", "dshard: 10", "pshard: 3" })
            Assert.Contains(field, yaml);
        Assert.Contains("local_flag: [\"S\", \"S\", \"AN\"]", yaml);
        Assert.DoesNotContain("tcpbuf:", yaml);
        Assert.DoesNotContain("udpbuf:", yaml);
    }

    [Fact]
    public void InvalidPacketTuningAndEffectiveSmuxDefaultsAreRejected()
    {
        foreach (var options in new PaqetAdvancedOptions[]
        {
            new() { KcpReceiveWindow = 32769 }, new() { PcapSocketBufferBytes = 104857601 },
            new() { LocalIpv4Port = 1234, ConnectionCount = 2 },
            new() { LocalIpv6Endpoint = "[2001:db8::1]:1234", Ipv6RouterMac = "aa:bb:cc:dd:ee:ff" },
            new() { StreamBufferBytes = 4194305 }, new() { SmuxBufferBytes = 1024 },
            new() { SmuxKeepAliveSeconds = 9 }, new() { SmuxKeepAliveTimeoutSeconds = 1 },
            new() { FecDataShards = 256, FecParityShards = 1 },
        }) Assert.NotEmpty(PaqetConfigurationValidator.ValidateAdvanced(options, "fast"));
        Assert.NotEmpty(PaqetConfigurationValidator.ValidateAdvanced(new(), "manual"));
    }

    [Fact]
    public void DisabledFecAndEqualKeepaliveDurationsAreSupported()
    {
        Assert.Empty(PaqetConfigurationValidator.ValidateAdvanced(new()
        {
            FecDataShards = 0, FecParityShards = 0, SmuxKeepAliveSeconds = 8, SmuxKeepAliveTimeoutSeconds = 8,
        }, "fast"));
        Assert.Empty(PaqetConfigurationValidator.ValidateAdvanced(new() { FecDataShards = 128, FecParityShards = 128 }, "fast"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("null")]
    public void UnencryptedModesDoNotRequireAnUnusedTransportKey(string block)
    {
        var settings = new PaqetFireSettings { ServerEndpoint = "server.example:8443", Advanced = new() { KcpBlock = block } };
        Assert.Empty(PaqetFireSettingsValidator.Validate(settings));
        Assert.Empty(PaqetConfigurationValidator.Validate(settings.Advanced.ApplyTo(Profile()), string.Empty));
    }
}
