using System.Net;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Engines;

internal static class BundledEngineOptions
{
    public static PaqetProcessOptions CreatePaqet(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.PaqetExecutablePath,
        ConfigurationPath = paths.PaqetConfigurationPath,
        TrustedExecutableRoot = paths.PayloadRoot,
        TrustedConfigurationRoot = paths.PayloadRoot,
        SocksEndpoint = new IPEndPoint(IPAddress.Loopback, 1080),
        Version = "v1.0.0-alpha.21",
        ExpectedExecutableSha256 = "7d73f5130757b538c26c3ed76439a150978c3e06289c724de37d916789aaf5dc",
        ReadinessTimeout = TimeSpan.FromSeconds(30),
    };

    public static XrayProcessOptions CreateXray(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.XrayExecutablePath,
        ConfigurationPath = paths.XrayConfigurationPath,
        GeoIpPath = paths.XrayGeoIpPath,
        GeoSitePath = paths.XrayGeoSitePath,
        Version = "26.3.27",
        ExpectedExecutableSha256 = "15c2d007954ac53ba69b80ec91242786b3c0b71d52649165b4ca1d5cc96ef8f1",
        InboundEndpoint = new IPEndPoint(IPAddress.Loopback, XrayJsonConfigurationWriter.InboundPort),
        ReadinessTimeout = TimeSpan.FromSeconds(15),
    };

    public static ProxiFyreProcessOptions CreateProxiFyre(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.ProxiFyreExecutablePath,
        ConfigurationPath = paths.ProxiFyreConfigurationPath,
        Version = "2.6.1",
        ExpectedExecutableSha256 = "b317381d7f61af1c5a697c0de32c7743869843fbf52dd1805e9ebd1a5612741e",
        ReadinessTimeout = TimeSpan.FromSeconds(8),
    };
}
