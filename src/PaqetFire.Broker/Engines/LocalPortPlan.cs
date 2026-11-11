using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// The loopback ports the engines use now. The runtime sets it with the configurations it
/// writes; the adapters read it when they start, and the probes and statistics client
/// whenever they dial an engine.
/// </summary>
public sealed class LocalPortPlan
{
    private LocalEnginePorts current;

    public LocalPortPlan(LocalEnginePorts? initial = null)
    {
        current = initial is { IsValid: true } ? initial : LocalEnginePorts.Default;
    }

    public LocalEnginePorts Current => Volatile.Read(ref current);

    public void Use(LocalEnginePorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (!ports.IsValid)
        {
            throw new ArgumentException("The local engine ports must be distinct ports from 1024 to 65535.", nameof(ports));
        }

        Volatile.Write(ref current, ports);
    }

    public IPEndPoint PaqetEndpoint => new(IPAddress.Loopback, Current.Paqet);

    public IPEndPoint XrayInboundEndpoint => new(IPAddress.Loopback, Current.XrayInbound);

    public IPEndPoint PsiphonEndpoint => new(IPAddress.Loopback, Current.PsiphonSocks);
}

/// <summary>The engine listeners a plan assigns, in the order they are assigned.</summary>
public enum LocalPortRole
{
    Paqet,
    XrayInbound,
    XrayStats,
    PsiphonSocks,
}

/// <summary>
/// Chooses the loopback ports for the next engine start. A port stays where it is while it
/// is free or held by PaqetFire's own running engine, so a route never moves under a
/// running process; a port another program holds moves to a free one.
/// </summary>
public static class LocalPortAllocator
{
    // Below the Windows dynamic range (49152 and up), so a port chosen here is not also
    // handed out as some connection's source port moments later.
    private const int CandidateFloor = 20000;
    private const int CandidateCeiling = 45000;
    private const int MaximumCandidates = 256;

    /// <param name="current">The ports in use or last used.</param>
    /// <param name="reserved">Ports the engines must not take, such as the LAN and hotspot share ports.</param>
    /// <param name="isUsable">Whether a role may listen on a port: free, or held by that role's own running engine.</param>
    /// <param name="pinned">Roles that keep their port whatever happens, because a running consumer depends on it.</param>
    /// <exception cref="InvalidOperationException">No free port was found for a role.</exception>
    public static LocalEnginePorts Plan(
        LocalEnginePorts current,
        IReadOnlyCollection<int> reserved,
        Func<LocalPortRole, int, bool> isUsable,
        IReadOnlySet<LocalPortRole>? pinned = null,
        Func<int, int, int>? nextCandidate = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(isUsable);
        nextCandidate ??= Random.Shared.Next;
        if (!current.IsValid)
        {
            current = LocalEnginePorts.Default;
        }

        var taken = new HashSet<int>(reserved);
        var defaults = LocalEnginePorts.Default;
        int Assign(LocalPortRole role, int existing, int fallback)
        {
            if (pinned?.Contains(role) == true)
            {
                taken.Add(existing);
                return existing;
            }

            foreach (var port in (int[])[existing, fallback])
            {
                if (!taken.Contains(port) && isUsable(role, port))
                {
                    taken.Add(port);
                    return port;
                }
            }

            for (var attempt = 0; attempt < MaximumCandidates; attempt++)
            {
                // Another role's port is not a candidate, so one move never displaces it.
                var port = nextCandidate(CandidateFloor, CandidateCeiling);
                if (!taken.Contains(port) && !current.Uses(port) && !defaults.Uses(port) && isUsable(role, port))
                {
                    taken.Add(port);
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"No free local port was found for {Describe(role)}. Close programs that hold many local ports, then connect again.");
        }

        // Pinned roles claim their ports first, so the others cannot be assigned onto them.
        foreach (var (role, port) in Roles(current))
        {
            if (pinned?.Contains(role) == true)
            {
                taken.Add(port);
            }
        }

        return new LocalEnginePorts(
            Assign(LocalPortRole.Paqet, current.Paqet, defaults.Paqet),
            Assign(LocalPortRole.XrayInbound, current.XrayInbound, defaults.XrayInbound),
            Assign(LocalPortRole.XrayStats, current.XrayStats, defaults.XrayStats),
            Assign(LocalPortRole.PsiphonSocks, current.PsiphonSocks, defaults.PsiphonSocks));
    }

    public static IEnumerable<(LocalPortRole Role, int Port)> Roles(LocalEnginePorts ports) =>
    [
        (LocalPortRole.Paqet, ports.Paqet),
        (LocalPortRole.XrayInbound, ports.XrayInbound),
        (LocalPortRole.XrayStats, ports.XrayStats),
        (LocalPortRole.PsiphonSocks, ports.PsiphonSocks),
    ];

    public static string Describe(LocalPortRole role) => role switch
    {
        LocalPortRole.Paqet => "Paqet's local SOCKS5 listener",
        LocalPortRole.XrayInbound => "Xray's local SOCKS5 listener",
        LocalPortRole.XrayStats => "Xray's statistics listener",
        LocalPortRole.PsiphonSocks => "Psiphon's local SOCKS5 listener",
        _ => role.ToString(),
    };

    /// <summary>
    /// Whether nothing on this computer listens on the port at the address, for TCP or UDP,
    /// and a bind there succeeds. The bind also catches ports Windows reserves for
    /// Hyper-V, WSL, or Docker, which appear in no listener table.
    /// </summary>
    public static bool IsFree(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            if (properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners())
                .Any(listener => listener.Port == port && Overlaps(listener.Address, address)))
            {
                return false;
            }
        }
        catch (NetworkInformationException)
        {
            // The bind below still decides.
        }

        return CanBind(address, port, SocketType.Stream, ProtocolType.Tcp) &&
               CanBind(address, port, SocketType.Dgram, ProtocolType.Udp);
    }

    private static bool Overlaps(IPAddress listener, IPAddress address) =>
        listener.Equals(address) ||
        listener.Equals(IPAddress.Any) ||
        listener.Equals(IPAddress.IPv6Any) ||
        (listener.IsIPv4MappedToIPv6 && listener.MapToIPv4().Equals(address));

    private static bool CanBind(IPAddress address, int port, SocketType socketType, ProtocolType protocol)
    {
        try
        {
            // A plain bind, like the engines' own: an exclusive one would also fail on the
            // TIME_WAIT connections a just-stopped engine leaves, and move its port needlessly.
            using var socket = new Socket(address.AddressFamily, socketType, protocol);
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is not
                                                     (SocketError.AddressAlreadyInUse or SocketError.AccessDenied))
        {
            // An address this computer does not have (yet) is for the engine to report; only a
            // port that is taken or reserved counts as unavailable.
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
