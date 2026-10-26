using System.Net;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Engines;

/// <param name="Size">The exact size of the pinned file, in bytes.</param>
/// <param name="FileName">The name it is installed under in the exit engines directory.</param>
public sealed record DownloadableEngine(
    ExitProxyKind Kind,
    string Version,
    Uri Uri,
    string Sha256,
    long Size,
    string FileName);

/// <summary>
/// The optional exit engines. Neither is in the installer: Psiphon is downloaded on request
/// and pinned like a bundled engine; OpenVPN is the user's own installation.
/// </summary>
internal static class ExitEngineOptions
{
    // The console client published in Psiphon-Labs/psiphon-tunnel-core-binaries at
    // commit d95d7f964c043a5a0ebd25cca801bc2753e14f74; only a 32-bit Windows build exists.
    // A URL pinned to a full commit always serves these bytes.
    public static DownloadableEngine Psiphon { get; } = new(
        ExitProxyKind.Psiphon,
        "d95d7f9",
        new Uri("https://github.com/Psiphon-Labs/psiphon-tunnel-core-binaries/raw/d95d7f964c043a5a0ebd25cca801bc2753e14f74/windows/psiphon-tunnel-core-i686.exe"),
        "34139a07deca9e9b94e5b55ebdf5e2583ca87f3d838873f105488681d22f4a0e",
        27956224,
        "psiphon-tunnel-core.exe");

    public static PsiphonProcessOptions CreatePsiphon(RuntimePaths paths) => new()
    {
        ExecutablePath = paths.PsiphonExecutablePath,
        ConfigurationPath = paths.PsiphonConfigurationPath,
        DataDirectory = paths.PsiphonDataDirectory,
        Version = Psiphon.Version,
        ExpectedExecutableSha256 = Psiphon.Sha256,
        SocksEndpoint = new IPEndPoint(IPAddress.Loopback, PsiphonJsonConfigurationWriter.LocalSocksPort),
        ReadinessTimeout = TimeSpan.FromSeconds(60),
    };

    public static OpenVpnProcessOptions CreateOpenVpn(RuntimePaths paths) => new()
    {
        ConfigurationPath = paths.OpenVpnConfigurationPath,
        AdapterName = OpenVpnConfigurationWriter.AdapterName,
        ReadinessTimeout = TimeSpan.FromSeconds(45),
    };
}
