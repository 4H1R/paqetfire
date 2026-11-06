using System.Net;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Engines;

internal static class BundledEngineOptions
{
    public const string XrayVersion = "26.10.31";

    public static PaqetProcessOptions CreatePaqet(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.PaqetExecutablePath,
        ConfigurationPath = paths.PaqetConfigurationPath,
        TrustedExecutableRoot = paths.PayloadRoot,
        TrustedConfigurationRoot = paths.PayloadRoot,
        SocksEndpoint = new IPEndPoint(IPAddress.Loopback, 1080),
        Version = "v1.0.0-alpha.21-a2",
        ExpectedExecutableSha256 = "8939e1d8ac79e67075d6e6a4ded0ff8bb69141a9c1c3ef5aa955cec37cc3dfb5",
        ReadinessTimeout = TimeSpan.FromSeconds(30),
    };

    public static XrayProcessOptions CreateXray(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.XrayExecutablePath,
        ConfigurationPath = paths.XrayConfigurationPath,
        GeoIpPath = paths.XrayGeoIpPath,
        GeoSitePath = paths.XrayGeoSitePath,
        Version = XrayVersion,
        ExpectedExecutableSha256 = "d5b558191cccb1e92a5772b68b8cab67717b26f634b0f15153215f65ae982a7e",
        InboundEndpoint = new IPEndPoint(IPAddress.Loopback, XrayJsonConfigurationWriter.InboundPort),
        ReadinessTimeout = TimeSpan.FromSeconds(15),
    };

    public static ProxiFyreProcessOptions CreateProxiFyre(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.ProxiFyreExecutablePath,
        ConfigurationPath = paths.ProxiFyreConfigurationPath,
        Version = "2.6.4",
        ExpectedExecutableSha256 = "beb7ff5b5283f872c610a6c70abfa9a5949f09c50fc766298776e5798a48ccfc",
        ReadinessTimeout = TimeSpan.FromSeconds(8),
    };
}
