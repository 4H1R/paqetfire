namespace PaqetFire.Core.Network;

public class HotspotNetworkDetector
{
    public static bool IsHotspotAddress(System.Net.IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 192 && b[1] == 168 && (b[2] == 137 || b[2] == 173);
    }

    public (string Address, string Name)? TryDetect() =>
        TryDetect(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces());

    public (string Address, string Name)? TryDetect(System.Collections.Generic.IEnumerable<System.Net.NetworkInformation.NetworkInterface> interfaces)
    {
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            // A subnet alone does not identify a hotspot: ordinary LANs can use
            // the same range. Require a Windows Wi-Fi Direct/Hosted adapter.
            var description = nic.Description ?? string.Empty;
            if (!description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) &&
                !description.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !g.Address.Equals(System.Net.IPAddress.Any)))
            {
                continue;
            }
            var ipv4 = props.UnicastAddresses.Select(u => u.Address)
                .FirstOrDefault(IsHotspotAddress);

            if (ipv4 is null || !IsHotspotAddress(ipv4))
            {
                continue;
            }

            return (ipv4.ToString(), nic.Name);
        }

        return null;
    }
}
