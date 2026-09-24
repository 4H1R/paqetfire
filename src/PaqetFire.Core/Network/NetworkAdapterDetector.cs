using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PaqetFire.Core.Network;

public sealed record NetworkAdapterDetails(
    Guid InterfaceGuid,
    string InterfaceName,
    IPAddress LocalAddress,
    IPAddress Gateway,
    NetworkInterfaceType Type);

public sealed record DetectedNetworkAdapter(NetworkAdapterDetails Adapter, string? RouterMac);

public sealed class NetworkAdapterDetector
{
    private readonly Func<IReadOnlyList<NetworkAdapterDetails>> readAdapters;
    private readonly Func<IPAddress, IPAddress, string?> resolveMac;

    public NetworkAdapterDetector() : this(ReadAdapters, ResolveMacAddress) { }

    public NetworkAdapterDetector(
        Func<IReadOnlyList<NetworkAdapterDetails>> readAdapters,
        Func<IPAddress, IPAddress, string?> resolveMac)
    {
        this.readAdapters = readAdapters;
        this.resolveMac = resolveMac;
    }

    public IReadOnlyList<NetworkAdapterDetails> GetAdapters() => readAdapters()
        .OrderBy(adapter => adapter.Type switch
        {
            NetworkInterfaceType.Ethernet => 0,
            NetworkInterfaceType.Wireless80211 => 1,
            _ => 2,
        })
        .ThenBy(adapter => adapter.InterfaceName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public DetectedNetworkAdapter Detect(Guid? selectedInterfaceGuid = null) =>
        Detect(GetAdapters(), selectedInterfaceGuid);

    public DetectedNetworkAdapter Detect(
        IReadOnlyList<NetworkAdapterDetails> adapters,
        Guid? selectedInterfaceGuid)
    {
        var adapter = selectedInterfaceGuid is { } selected
            ? adapters.FirstOrDefault(candidate => candidate.InterfaceGuid == selected)
            : adapters.FirstOrDefault();
        if (adapter is null)
        {
            throw new InvalidOperationException(selectedInterfaceGuid is null
                ? "No active IPv4 network adapter with a default gateway was found. Connect Ethernet or Wi-Fi, then detect again."
                : "The selected network interface is unavailable or has no IPv4 default gateway. Reconnect it, select another interface, or choose Automatic.");
        }

        return new DetectedNetworkAdapter(adapter, resolveMac(adapter.Gateway, adapter.LocalAddress));
    }

    private static IReadOnlyList<NetworkAdapterDetails> ReadAdapters()
    {
        var adapters = new List<NetworkAdapterDetails>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                !Guid.TryParse(networkInterface.Id, out var guid)) continue;

            try
            {
                var properties = networkInterface.GetIPProperties();
                var local = properties.UnicastAddresses.Select(value => value.Address)
                    .FirstOrDefault(IsUsableLocalIpv4);
                var gateway = properties.GatewayAddresses.Select(value => value.Address)
                    .FirstOrDefault(IsUsableIpv4);
                if (local is not null && gateway is not null)
                    adapters.Add(new(guid, networkInterface.Name, local, gateway, networkInterface.NetworkInterfaceType));
            }
            catch (NetworkInformationException)
            {
                // An interface may disappear while Windows enumerates it.
            }
        }
        return adapters;
    }

    private static bool IsUsableIpv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any);

    private static bool IsUsableLocalIpv4(IPAddress address) =>
        IsUsableIpv4(address) && address.GetAddressBytes() is not [169, 254, _, _];

    public static string? ResolveMacAddress(IPAddress gateway, IPAddress localAddress)
    {
        if (!IsUsableIpv4(gateway) || !IsUsableIpv4(localAddress)) return null;
        var buffer = new byte[8];
        var length = buffer.Length;
        var result = SendARP(
            BitConverter.ToUInt32(gateway.GetAddressBytes(), 0),
            BitConverter.ToUInt32(localAddress.GetAddressBytes(), 0),
            buffer, ref length);
        return result == 0 && length == 6
            ? string.Join(':', buffer.Take(length).Select(value => value.ToString("X2")))
            : null;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destinationAddress, uint sourceAddress,
        [Out] byte[] macAddress, ref int physicalAddressLength);
}
