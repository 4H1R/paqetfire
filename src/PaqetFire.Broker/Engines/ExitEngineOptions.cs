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
    // commit b710e31e2f3a69ff5fdecfa44f0b3be146b2d0c9; only a 32-bit Windows build exists.
    // A URL pinned to a full commit always serves these bytes.
    public static DownloadableEngine Psiphon { get; } = new(
        ExitProxyKind.Psiphon,
        "b710e31",
        new Uri("https://github.com/Psiphon-Labs/psiphon-tunnel-core-binaries/raw/b710e31e2f3a69ff5fdecfa44f0b3be146b2d0c9/windows/psiphon-tunnel-core-i686.exe"),
        "d1427683d11d2d68bbaeeaff8d5071fe38cb5f6b823a487df13562c1491a27c7",
        28003840,
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
