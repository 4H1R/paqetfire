using Microsoft.Extensions.Hosting.WindowsServices;
using PaqetFire.Broker;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PaqetFire Broker";
});
if (WindowsServiceHelpers.IsWindowsService())
{
    // Replaces the default service lifetime so the broker hears about resume itself.
    builder.Services.AddSingleton<IHostLifetime, PowerAwareServiceLifetime>();
}

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
var dataDirectory = Path.GetDirectoryName(paths.MachineSettingsPath)!;

builder.Services.AddSingleton(paths);
// %ProgramData% lets standard users create folders, so %ProgramData%\PaqetFire may have
// been planted. Every store resolves this first: it is secured, and anything nobody
// trusted put in it is removed, before the broker reads a byte from it.
builder.Services.AddSingleton(services =>
{
    var directory = new ProtectedDirectory(dataDirectory, ProtectedDirectoryPolicy.MachineData);
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PaqetFire.Broker.DataDirectory");
    if (directory.Ensure())
    {
        logger.LogInformation("Created the protected data directory {Directory}.", directory.Path);
    }

    foreach (var removed in directory.RemoveUntrustedEntries())
    {
        logger.LogWarning("Removed {Entry} from the data directory: nobody trusted created it.", removed);
    }

    return directory;
});
builder.Services.AddSingleton(services => new EngineCredentialStore(
    Path.Combine(dataDirectory, "engine-credentials.json"),
    services.GetRequiredService<ProtectedDirectory>()).LoadOrCreate());
builder.Services.AddSingleton<IBrokerStateStore>(services => new BrokerStateStore(
    Path.Combine(dataDirectory, "broker-state.json"),
    services.GetRequiredService<ProtectedDirectory>()));
builder.Services.AddSingleton<PayloadIntegrityInspector>();
builder.Services.AddSingleton<PrerequisiteInspector>();
builder.Services.AddSingleton<IFirewallRuleStore, WindowsFirewallRuleStore>();
builder.Services.AddSingleton<EngineFirewallRules>();
builder.Services.AddSingleton<NetworkEnvironmentDetector>();
builder.Services.AddSingleton<HotspotNetworkDetector>();
builder.Services.AddSingleton<IRouteProbe, SocketRouteProbe>();
builder.Services.AddSingleton<IConnectionVerifier, ConnectionVerifier>();
builder.Services.AddSingleton<IXrayTrafficStats, XrayStatsClient>();
builder.Services.AddSingleton<ITunnelProbe, PaqetTunnelProbe>();
builder.Services.AddSingleton<IIpv6CapabilityProbe, Ipv6CapabilityProbe>();
builder.Services.AddSingleton<TunnelActivity>();
builder.Services.AddSingleton<IGeoIpCountryLookup>(_ => new GeoIpCountryLookup(paths.XrayGeoIpPath));
builder.Services.AddSingleton<ILiveStatsService, LiveStatsService>();
builder.Services.AddSingleton<IPaqetConfigurationWriter, PaqetYamlConfigurationWriter>();
builder.Services.AddSingleton<IXrayConfigurationWriter, XrayJsonConfigurationWriter>();
builder.Services.AddSingleton<IProxiFyreConfigurationWriter, ProxiFyreJsonConfigurationWriter>();
builder.Services.AddSingleton<IMachineSettingsStore>(services =>
    new MachineSettingsStore(paths.MachineSettingsPath, services.GetRequiredService<ProtectedDirectory>()));
builder.Services.AddSingleton<IProfileSecretProtector, DpapiProfileSecretProtector>();
builder.Services.AddSingleton<IMachineProfileCatalogStore>(services =>
    new MachineProfileCatalogStore(
        Path.Combine(dataDirectory, "profiles.json"),
        services.GetRequiredService<IProfileSecretProtector>(),
        services.GetRequiredService<ProtectedDirectory>()));
builder.Services.AddSingleton<IAtomicConfigurationStore>(_ =>
    new AtomicConfigurationStore(
        paths.PayloadRoot,
        [paths.PaqetConfigurationPath, paths.XrayConfigurationPath, paths.ProxiFyreConfigurationPath, paths.PsiphonConfigurationPath]));
builder.Services.AddSingleton<BrokerSignal>();
builder.Services.AddSingleton(services =>
{
    var signal = services.GetRequiredService<BrokerSignal>();
    var progress = new ConnectionProgressTracker();
    progress.Changed += signal.Notify;
    return progress;
});
builder.Services.AddSingleton(services =>
{
    var adapter = new PaqetProcessAdapter(BundledEngineOptions.CreatePaqet(paths));
    adapter.UnexpectedExit += (_, _) => services.GetRequiredService<BrokerSignal>().Notify();
    return adapter;
});
builder.Services.AddSingleton<RoutingDataFileVerifier>();
builder.Services.AddSingleton(services =>
{
    var adapter = new XrayProcessAdapter(
        BundledEngineOptions.CreateXray(paths),
        services.GetRequiredService<RoutingDataFileVerifier>());
    adapter.UnexpectedExit += (_, _) => services.GetRequiredService<BrokerSignal>().Notify();
    return adapter;
});
builder.Services.AddSingleton(services =>
{
    var firewall = services.GetRequiredService<EngineFirewallRules>();
    // ProxiFyre's redirected traffic needs the firewall rules, so their background
    // repair must have finished before capture starts.
    var adapter = new ProxiFyreProcessAdapter(
        BundledEngineOptions.CreateProxiFyre(paths),
        cancellationToken => firewall.EnsureRepairedAsync().WaitAsync(cancellationToken));
    adapter.UnexpectedExit += (_, _) => services.GetRequiredService<BrokerSignal>().Notify();
    return adapter;
});
builder.Services.AddSingleton(services =>
{
    var adapter = new PsiphonProcessAdapter(BundledEngineOptions.CreatePsiphon(paths));
    adapter.UnexpectedExit += (_, _) => services.GetRequiredService<BrokerSignal>().Notify();
    return adapter;
});
builder.Services.AddSingleton<IConnectionController>(services => new ConnectionController(
    services.GetRequiredService<PaqetProcessAdapter>(),
    services.GetRequiredService<XrayProcessAdapter>(),
    services.GetRequiredService<ProxiFyreProcessAdapter>(),
    services.GetRequiredService<PsiphonProcessAdapter>()));
builder.Services.AddSingleton<IRoutingDataSource, GitHubRoutingDataSource>();
builder.Services.AddSingleton<IRoutingDataValidator, XrayRoutingDataValidator>();
builder.Services.AddSingleton(services =>
{
    _ = services.GetRequiredService<ProtectedDirectory>();
    var controller = services.GetRequiredService<IConnectionController>();
    var progress = services.GetRequiredService<ConnectionProgressTracker>();
    var xray = services.GetRequiredService<XrayProcessAdapter>();
    return new RoutingDataService(
        new RoutingDataServiceOptions { BundledXrayVersion = BundledEngineOptions.XrayVersion },
        new RoutingDataStore(Path.Combine(dataDirectory, "routing-data")),
        services.GetRequiredService<IRoutingDataSource>(),
        services.GetRequiredService<IRoutingDataValidator>(),
        services.GetRequiredService<RoutingDataFileVerifier>(),
        services.GetRequiredService<ILogger<RoutingDataService>>(),
        async cancellationToken =>
        {
            var status = await controller.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            // The tunnel probe after a connect is not a transition; the route is already up.
            var transitioning = progress.Current is { Stage: not ConnectionStage.ProbingTunnel };
            return new RoutingDataRouteState(
                status.State == ConnectionState.Connected,
                transitioning,
                IsGuarded: status.State == ConnectionState.Guarded);
        },
        services.GetRequiredService<BrokerSignal>(),
        inUse: () => [xray.RoutingData.AssetDirectory]);
});
builder.Services.AddSingleton<TunnelHealthTracker>();
builder.Services.AddSingleton<IPaqetFireRuntime, PaqetFireRuntime>();
builder.Services.AddSingleton<TunnelWatchdog>();
builder.Services.AddSingleton<NetworkChangeMonitor>();
builder.Services.AddSingleton<IBrokerRequestHandler, ConnectionBrokerRequestHandler>();
builder.Services.AddSingleton<NamedPipeBrokerServer>();
builder.Services.AddHostedService<BrokerWorker>();

await builder.Build().RunAsync();
