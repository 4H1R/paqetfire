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
        Version = "v1.0.0-alpha.21-a1",
        ExpectedExecutableSha256 = "5b87fe8f9e28de33233fc1d0bac933669630f7315c1582030ff5fc5abafaada8",
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
