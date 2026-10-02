using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace PaqetFire.Broker.Diagnostics;

public interface IGeoIpCountryLookup
{
    ValueTask<string?> FindCountryCodeAsync(IPAddress address, CancellationToken cancellationToken);
}

/// <summary>
/// Offline country lookup against the bundled Xray geoip.dat, so the exit address
/// is never sent to a third-party geolocation service.
/// </summary>
public sealed class GeoIpCountryLookup(string geoIpPath) : IGeoIpCountryLookup
{
    private readonly ConcurrentDictionary<IPAddress, string?> cache = new();

    public async ValueTask<string?> FindCountryCodeAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (cache.TryGetValue(address, out var cached))
        {
            return cached;
        }

        // The file is ~20 MB; it is read only once per new exit address and not retained.
        var data = await File.ReadAllBytesAsync(geoIpPath, cancellationToken).ConfigureAwait(false);
        var country = FindCountryCode(data, address.GetAddressBytes());
        cache[address] = country;
        return country;
    }

    // GeoIPList { repeated GeoIP entry = 1; }
    // GeoIP { string country_code = 1; repeated CIDR cidr = 2; }
    // CIDR { bytes ip = 1; uint32 prefix = 2; }
    internal static string? FindCountryCode(ReadOnlySpan<byte> geoIpList, ReadOnlySpan<byte> address)
    {
        var list = new ProtoReader(geoIpList);
        while (list.TryReadField(out var field, out var wireType))
        {
            if (field != 1 || wireType != 2)
            {
                list.Skip(wireType);
                continue;
            }

            var entry = new ProtoReader(list.ReadBytes());
            string? code = null;
            var matched = false;
            while (entry.TryReadField(out var entryField, out var entryWireType))
            {
                if (entryField == 1 && entryWireType == 2)
                {
                    code = Encoding.ASCII.GetString(entry.ReadBytes());
                    if (code.Length != 2)
                    {
                        // Skip non-country sets such as "private" or provider lists.
                        break;
                    }
                }
                else if (entryField == 2 && entryWireType == 2)
                {
                    if (CidrContains(entry.ReadBytes(), address))
                    {
                        matched = true;
                        if (code is not null)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    entry.Skip(entryWireType);
                }
            }

            if (matched && code is { Length: 2 })
            {
                return code.ToUpperInvariant();
            }
        }

        return null;
    }

    private static bool CidrContains(ReadOnlySpan<byte> cidr, ReadOnlySpan<byte> address)
    {
        var reader = new ProtoReader(cidr);
        ReadOnlySpan<byte> network = default;
        var prefix = 0;
        while (reader.TryReadField(out var field, out var wireType))
        {
            if (field == 1 && wireType == 2)
            {
                network = reader.ReadBytes();
            }
            else if (field == 2 && wireType == 0)
            {
                prefix = (int)reader.ReadVarint();
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        if (network.Length != address.Length || prefix > network.Length * 8)
        {
            return false;
        }

        var fullBytes = prefix / 8;
        if (!network[..fullBytes].SequenceEqual(address[..fullBytes]))
        {
            return false;
        }

        var remainingBits = prefix % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (network[fullBytes] & mask) == (address[fullBytes] & mask);
    }
}
