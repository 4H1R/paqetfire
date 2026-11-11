using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PaqetFire.Broker.Engines;

/// <summary>A process with a TCP listener or UDP socket on a local IPv4 port.</summary>
public sealed record PortOwner(int ProcessId, string ProcessName, IPEndPoint LocalEndpoint, ProtocolType Protocol)
{
    public override string ToString() =>
        $"{ProcessName} (PID {ProcessId}, {(Protocol == ProtocolType.Udp ? "UDP" : "TCP")} {LocalEndpoint})";
}

/// <summary>
/// Reads Windows' IPv4 TCP listener and UDP tables with their owning process IDs, so a
/// report can name the program that holds a port instead of only saying it is taken.
/// </summary>
public static class PortOwners
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;
    private const int NoError = 0;
    private const int InsufficientBuffer = 122;

    /// <summary>The processes holding the port; empty when it is free or the tables cannot be read.</summary>
    public static IReadOnlyList<PortOwner> Find(int port)
    {
        if (port is < 1 or > 65535)
        {
            return [];
        }

        try
        {
            return ReadTcpListeners().Concat(ReadUdpSockets())
                .Where(entry => entry.Endpoint.Port == port)
                .Select(entry => new PortOwner(entry.ProcessId, NameOf(entry.ProcessId), entry.Endpoint, entry.Protocol))
                .DistinctBy(owner => (owner.ProcessId, owner.Protocol, owner.LocalEndpoint))
                .ToArray();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
                                              ExternalException or OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>Who holds the port, for a sentence such as "port 1080 is held by …".</summary>
    public static string? Describe(int port)
    {
        var owners = Find(port);
        return owners.Count == 0
            ? null
            : string.Join(", ", owners.GroupBy(owner => owner.ProcessId).Select(group => group.First()).Select(owner =>
                $"{owner.ProcessName} (PID {owner.ProcessId})"));
    }

    private static string NameOf(int processId)
    {
        if (processId == 0)
        {
            return "System Idle";
        }

        if (processId == 4)
        {
            return "System";
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception)
        {
            // The process exited after the table was read.
            return "an exited process";
        }
    }

    private static IEnumerable<(IPEndPoint Endpoint, int ProcessId, ProtocolType Protocol)> ReadTcpListeners()
    {
        // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, PID.
        const int rowSize = 24;
        foreach (var row in ReadTable((IntPtr buffer, ref int size) =>
                     GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0), rowSize))
        {
            yield return (Endpoint(row, 4, 8), BinaryPrimitives.ReadInt32LittleEndian(row[20..]), ProtocolType.Tcp);
        }
    }

    private static IEnumerable<(IPEndPoint Endpoint, int ProcessId, ProtocolType Protocol)> ReadUdpSockets()
    {
        // MIB_UDPROW_OWNER_PID: local address, local port, PID.
        const int rowSize = 12;
        foreach (var row in ReadTable((IntPtr buffer, ref int size) =>
                     GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableOwnerPid, 0), rowSize))
        {
            yield return (Endpoint(row, 0, 4), BinaryPrimitives.ReadInt32LittleEndian(row[8..]), ProtocolType.Udp);
        }
    }

    private static IPEndPoint Endpoint(byte[] row, int addressOffset, int portOffset)
    {
        var address = new IPAddress(row.AsSpan(addressOffset, 4));
        // The port is in network byte order in the low two bytes.
        var port = (row[portOffset] << 8) | row[portOffset + 1];
        return new IPEndPoint(address, port);
    }

    private delegate int TableReader(IntPtr buffer, ref int size);

    private static List<byte[]> ReadTable(TableReader read, int rowSize)
    {
        var size = 0;
        var result = read(IntPtr.Zero, ref size);
        for (var attempt = 0; attempt < 4 && result == InsufficientBuffer; attempt++)
        {
            // The table can grow between the size query and the read.
            size += rowSize * 16;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = read(buffer, ref size);
                if (result != NoError)
                {
                    continue;
                }

                var count = Marshal.ReadInt32(buffer);
                var rows = new List<byte[]>(count);
                for (var index = 0; index < count; index++)
                {
                    var row = new byte[rowSize];
                    Marshal.Copy(buffer + 4 + index * rowSize, row, 0, rowSize);
                    rows.Add(row);
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int addressFamily, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetExtendedUdpTable(
        IntPtr table, ref int size, bool order, int addressFamily, int tableClass, int reserved);
}
