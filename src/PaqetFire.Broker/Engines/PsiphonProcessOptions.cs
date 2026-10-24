using System.Net;
using PaqetFire.Broker.Configuration;

namespace PaqetFire.Broker.Engines;

public sealed record PsiphonProcessOptions
{
    public required string ExecutablePath { get; init; }

    public required string ConfigurationPath { get; init; }

    /// <summary>Psiphon's server list and data store.</summary>
    public required string DataDirectory { get; init; }

    public required string ExpectedExecutableSha256 { get; init; }

    public required string Version { get; init; }

    /// <summary>Secures <see cref="DataDirectory"/> before every start; null only creates it (tests).</summary>
    public ProtectedDirectoryPolicy? DataDirectoryPolicy { get; init; } = ProtectedDirectoryPolicy.BrokerPrivate;

    public IPEndPoint SocksEndpoint { get; init; } = new(IPAddress.Loopback, 1084);

    /// <summary>
    /// How long a start waits for the first Psiphon tunnel. A first run downloads the server
    /// list through Paqet before it can try any server.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
