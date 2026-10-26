using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class BundledEngineOptionsTests
{
    [Theory]
    [InlineData(EngineKind.Paqet)]
    [InlineData(EngineKind.Xray)]
    [InlineData(EngineKind.ProxiFyre)]
    public void ProductionStartupPinsMatchReleaseManifest(EngineKind kind)
    {
        var manifest = ProductPayloadManifest.Load(Path.Combine(AppContext.BaseDirectory, "release-payload-manifest.json"));
        var engine = Assert.Single(manifest.Engines, item => item.Engine == kind);
        var entryPoint = Assert.Single(engine.Files, file => file.Path == engine.EntryPoint);
        var paths = new RuntimePaths("payload", "paqet.exe", "paqet.yaml", "xray.exe", "xray.json",
            "geoip.dat", "geosite.dat", "ProxiFyre.exe", "app-config.json", "settings.json");
        (string? version, string? digest) = kind switch
        {
            EngineKind.Paqet => Pin(BundledEngineOptions.CreatePaqet(paths)),
            EngineKind.Xray => Pin(BundledEngineOptions.CreateXray(paths)),
            EngineKind.ProxiFyre => Pin(BundledEngineOptions.CreateProxiFyre(paths)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.Equal(engine.Version, version);
        Assert.Equal(entryPoint.Sha256, digest, ignoreCase: true);
    }

    private static (string?, string?) Pin(PaqetProcessOptions options) => (options.Version, options.ExpectedExecutableSha256);
    private static (string?, string?) Pin(XrayProcessOptions options) => (options.Version, options.ExpectedExecutableSha256);
    private static (string?, string?) Pin(ProxiFyreProcessOptions options) => (options.Version, options.ExpectedExecutableSha256);

    [Fact]
    public void ExitEnginesAreNotPartOfTheReleasePayload()
    {
        var manifest = ProductPayloadManifest.Load(Path.Combine(AppContext.BaseDirectory, "release-payload-manifest.json"));
        Assert.DoesNotContain(manifest.Engines, engine => engine.Engine.IsExit());
    }

    [Fact]
    public void PsiphonStartupPinsTheDownloadedRelease()
    {
        var paths = new RuntimePaths("payload", "paqet.exe", "paqet.yaml", "xray.exe", "xray.json",
            "geoip.dat", "geosite.dat", "ProxiFyre.exe", "app-config.json", Path.Combine(Path.GetTempPath(), "settings.json"));
        var options = ExitEngineOptions.CreatePsiphon(paths);
        Assert.Equal(ExitEngineOptions.Psiphon.Version, options.Version);
        Assert.Equal(ExitEngineOptions.Psiphon.Sha256, options.ExpectedExecutableSha256);
        Assert.Equal(paths.PsiphonExecutablePath, options.ExecutablePath);
        Assert.StartsWith(paths.ExitEnginesDirectory, options.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        // A URL pinned to a full commit always serves the same bytes.
        Assert.Matches("/raw/[0-9a-f]{40}/", ExitEngineOptions.Psiphon.Uri.AbsolutePath);
    }
}
