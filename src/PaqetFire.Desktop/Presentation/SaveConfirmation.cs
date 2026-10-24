using System.Globalization;
using System.Net;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Desktop.Presentation;

/// <summary>
/// Decides whether a successful save response really carries the submitted profile. A save
/// that was cancelled before the broker stored it must not clear the form or its secrets.
/// Only fields the broker stores verbatim (apart from endpoint normalization) are compared.
/// </summary>
public static class SaveConfirmation
{
    public static bool Reflects(PaqetFireSettings submitted, PaqetFireSettingsView? stored)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        if (stored is null)
        {
            return false;
        }

        return string.Equals(submitted.ProfileName.Trim(), stored.ProfileName.Trim(), StringComparison.Ordinal) &&
            string.Equals(NormalizeEndpoint(submitted.ServerEndpoint), NormalizeEndpoint(stored.ServerEndpoint), StringComparison.Ordinal) &&
            submitted.NetworkInterfaceGuid == stored.NetworkInterfaceGuid &&
            submitted.RoutingMode == stored.RoutingMode &&
            submitted.KillSwitchEnabled == stored.KillSwitchEnabled &&
            submitted.ShareWithLan == stored.ShareWithLan &&
            submitted.ShareViaHotspot == stored.ShareViaHotspot &&
            submitted.DnsResolver == stored.DnsResolver &&
            submitted.ExitProxy == stored.ExitProxy &&
            string.Equals(submitted.Psiphon?.EgressRegion ?? string.Empty, stored.Psiphon?.EgressRegion ?? string.Empty, StringComparison.Ordinal) &&
            (submitted.TransportKey.Length == 0 || stored.HasTransportKey) &&
            (submitted.LanSocksPassword.Length == 0 || stored.HasLanSocksPassword);
    }

    // The broker lower-cases host names and canonicalizes IP literals.
    private static string NormalizeEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim() ?? string.Empty;
        var separator = value.LastIndexOf(':');
        if (separator <= 0)
        {
            return value.ToLowerInvariant();
        }

        var host = value[..separator];
        var port = value[(separator + 1)..];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        host = IPAddress.TryParse(host, out var address)
            ? address.ToString()
            : host.ToLowerInvariant();
        return int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? $"{host}|{number}"
            : $"{host}|{port}";
    }
}
