using System.Net;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Engines;

internal static class BundledEngineOptions
{
    public const string XrayVersion = "26.3.27";

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
        Version = XrayVersion,
        ExpectedExecutableSha256 = "15c2d007954ac53ba69b80ec91242786b3c0b71d52649165b4ca1d5cc96ef8f1",
        InboundEndpoint = new IPEndPoint(IPAddress.Loopback, XrayJsonConfigurationWriter.InboundPort),
        ReadinessTimeout = TimeSpan.FromSeconds(15),
    };

    // The console client published in Psiphon-Labs/psiphon-tunnel-core-binaries at
    // commit d95d7f964c043a5a0ebd25cca801bc2753e14f74; only a 32-bit Windows build exists.
    public static PsiphonProcessOptions CreatePsiphon(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.PsiphonExecutablePath,
        ConfigurationPath = paths.PsiphonConfigurationPath,
        DataDirectory = paths.PsiphonDataDirectory,
        Version = "d95d7f9",
        ExpectedExecutableSha256 = "34139a07deca9e9b94e5b55ebdf5e2583ca87f3d838873f105488681d22f4a0e",
        SocksEndpoint = new IPEndPoint(IPAddress.Loopback, PsiphonJsonConfigurationWriter.LocalSocksPort),
        ReadinessTimeout = TimeSpan.FromSeconds(60),
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
