using PaqetFire.Broker;
using System.Net;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PaqetFire Broker";
});

var payloadRoot = Path.Combine(AppContext.BaseDirectory, "payload");
var paqetRoot = Path.Combine(payloadRoot, "engines", "paqet", "x64");
var xrayRoot = Path.Combine(payloadRoot, "engines", "xray", "x64");
var proxiFyreRoot = Path.Combine(payloadRoot, "engines", "proxifyre", "x64");
var paths = new RuntimePaths(
    payloadRoot,
    Path.Combine(paqetRoot, "paqet_windows_amd64.exe"),
    Path.Combine(paqetRoot, "config.yaml"),
    Path.Combine(xrayRoot, "xray.exe"),
    Path.Combine(xrayRoot, "config.json"),
    Path.Combine(xrayRoot, "geoip.dat"),
    Path.Combine(xrayRoot, "geosite.dat"),
    Path.Combine(proxiFyreRoot, "ProxiFyre.exe"),
    Path.Combine(proxiFyreRoot, "app-config.json"),
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PaqetFire",
        "settings.json"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<PayloadIntegrityInspector>();
builder.Services.AddSingleton<PrerequisiteInspector>();
builder.Services.AddSingleton<NetworkEnvironmentDetector>();
builder.Services.AddSingleton<HotspotNetworkDetector>();
builder.Services.AddSingleton<IPaqetConfigurationWriter, PaqetYamlConfigurationWriter>();
builder.Services.AddSingleton<IXrayConfigurationWriter, XrayJsonConfigurationWriter>();
builder.Services.AddSingleton<IProxiFyreConfigurationWriter, ProxiFyreJsonConfigurationWriter>();
builder.Services.AddSingleton<IMachineSettingsStore>(_ =>
    new MachineSettingsStore(paths.MachineSettingsPath));
builder.Services.AddSingleton<IAtomicConfigurationStore>(_ =>
    new AtomicConfigurationStore(
        paths.PayloadRoot,
        [paths.PaqetConfigurationPath, paths.XrayConfigurationPath, paths.ProxiFyreConfigurationPath]));
builder.Services.AddSingleton(_ => new PaqetProcessAdapter(new PaqetProcessOptions
{
    ExecutablePath = paths.PaqetExecutablePath,
    ConfigurationPath = paths.PaqetConfigurationPath,
    TrustedExecutableRoot = paths.PayloadRoot,
    TrustedConfigurationRoot = paths.PayloadRoot,
    SocksEndpoint = new IPEndPoint(IPAddress.Loopback, 1080),
    Version = "v1.0.0-alpha.21",
    ExpectedExecutableSha256 = "7d73f5130757b538c26c3ed76439a150978c3e06289c724de37d916789aaf5dc",
    ReadinessTimeout = TimeSpan.FromSeconds(30),
}));
builder.Services.AddSingleton(_ => new XrayProcessAdapter(new XrayProcessOptions
{
    ExecutablePath = paths.XrayExecutablePath,
    ConfigurationPath = paths.XrayConfigurationPath,
    GeoIpPath = paths.XrayGeoIpPath,
    GeoSitePath = paths.XrayGeoSitePath,
    Version = "26.3.27",
    ExpectedExecutableSha256 = "15c2d007954ac53ba69b80ec91242786b3c0b71d52649165b4ca1d5cc96ef8f1",
    InboundEndpoint = new IPEndPoint(IPAddress.Loopback, XrayJsonConfigurationWriter.InboundPort),
    ReadinessTimeout = TimeSpan.FromSeconds(15),
}));
builder.Services.AddSingleton(_ => new ProxiFyreProcessAdapter(new ProxiFyreProcessOptions
{
    ExecutablePath = paths.ProxiFyreExecutablePath,
    ConfigurationPath = paths.ProxiFyreConfigurationPath,
    Version = "2.6.0",
    ExpectedExecutableSha256 = "eaa48f0efc0dfbbab6f4ea6fc2ff6c7b45164dca544921962025b698bd59869f",
    ReadinessTimeout = TimeSpan.FromSeconds(8),
}));
builder.Services.AddSingleton<IConnectionController>(services => new ConnectionController(
    services.GetRequiredService<PaqetProcessAdapter>(),
    services.GetRequiredService<XrayProcessAdapter>(),
    services.GetRequiredService<ProxiFyreProcessAdapter>()));
builder.Services.AddSingleton<IPaqetFireRuntime, PaqetFireRuntime>();
builder.Services.AddSingleton<IBrokerRequestHandler, ConnectionBrokerRequestHandler>();
builder.Services.AddSingleton<NamedPipeBrokerServer>();
builder.Services.AddHostedService<BrokerWorker>();

await builder.Build().RunAsync();
