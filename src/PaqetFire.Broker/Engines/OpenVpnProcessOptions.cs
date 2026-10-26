namespace PaqetFire.Broker.Engines;

public sealed record OpenVpnProcessOptions
{
    public required string ConfigurationPath { get; init; }

    /// <summary>The TAP adapter OpenVPN uses; created with tapctl when it is missing.</summary>
    public required string AdapterName { get; init; }

    /// <summary>Finds the user's OpenVPN installation; re-checked before every start.</summary>
    public Func<OpenVpnLookup> Locate { get; init; } = OpenVpnInstallation.Find;

    /// <summary>
    /// How long a start waits for the tunnel. OpenVPN reaches its server through Paqet, so a
    /// TLS handshake takes several tunnel round trips.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a completed connection waits for the server's address to appear on the adapter.</summary>
    public TimeSpan AddressWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a stop waits for OpenVPN to close the tunnel before it is killed.</summary>
    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
