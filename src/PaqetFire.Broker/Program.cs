using PaqetFire.Broker;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Profiles;

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
builder.Services.AddSingleton<IRouteProbe, SocketRouteProbe>();
builder.Services.AddSingleton<IConnectionVerifier, ConnectionVerifier>();
builder.Services.AddSingleton<IPaqetConfigurationWriter, PaqetYamlConfigurationWriter>();
builder.Services.AddSingleton<IXrayConfigurationWriter, XrayJsonConfigurationWriter>();
builder.Services.AddSingleton<IProxiFyreConfigurationWriter, ProxiFyreJsonConfigurationWriter>();
builder.Services.AddSingleton<IMachineSettingsStore>(_ =>
    new MachineSettingsStore(paths.MachineSettingsPath));
builder.Services.AddSingleton<IProfileSecretProtector, DpapiProfileSecretProtector>();
builder.Services.AddSingleton<IMachineProfileCatalogStore>(services =>
    new MachineProfileCatalogStore(
        Path.Combine(Path.GetDirectoryName(paths.MachineSettingsPath)!, "profiles.json"),
        services.GetRequiredService<IProfileSecretProtector>()));
builder.Services.AddSingleton<IAtomicConfigurationStore>(_ =>
    new AtomicConfigurationStore(
        paths.PayloadRoot,
        [paths.PaqetConfigurationPath, paths.XrayConfigurationPath, paths.ProxiFyreConfigurationPath]));
builder.Services.AddSingleton(_ => new PaqetProcessAdapter(BundledEngineOptions.CreatePaqet(paths)));
builder.Services.AddSingleton(_ => new XrayProcessAdapter(BundledEngineOptions.CreateXray(paths)));
builder.Services.AddSingleton(_ => new ProxiFyreProcessAdapter(BundledEngineOptions.CreateProxiFyre(paths)));
builder.Services.AddSingleton<IConnectionController>(services => new ConnectionController(
    services.GetRequiredService<PaqetProcessAdapter>(),
    services.GetRequiredService<XrayProcessAdapter>(),
    services.GetRequiredService<ProxiFyreProcessAdapter>()));
builder.Services.AddSingleton<IPaqetFireRuntime, PaqetFireRuntime>();
builder.Services.AddSingleton<IBrokerRequestHandler, ConnectionBrokerRequestHandler>();
builder.Services.AddSingleton<NamedPipeBrokerServer>();
builder.Services.AddHostedService<BrokerWorker>();

await builder.Build().RunAsync();
