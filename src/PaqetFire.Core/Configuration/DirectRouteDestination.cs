using System.Globalization;
using System.Net;

namespace PaqetFire.Core.Configuration;

internal enum DirectRouteDestinationKind
{
    Domain,
    Ip,
}

internal readonly record struct DirectRouteDestination(
    DirectRouteDestinationKind Kind,
    string Value)
{
    public static bool TryParse(string? value, out DirectRouteDestination destination)
    {
        destination = default;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return false;
        }

        var candidate = value.Trim();
        if (TryNormalizeIpOrCidr(candidate, out var ip))
        {
            destination = new DirectRouteDestination(DirectRouteDestinationKind.Ip, ip);
            return true;
        }

        if (candidate.StartsWith("*.", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }

        if (candidate.Contains('*', StringComparison.Ordinal) ||
            candidate.StartsWith(".", StringComparison.Ordinal) ||
            candidate.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            candidate = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (Uri.CheckHostName(candidate) != UriHostNameType.Dns)
        {
            return false;
        }

        destination = new DirectRouteDestination(
            DirectRouteDestinationKind.Domain,
            $"domain:{candidate}");
        return true;
    }

    private static bool TryNormalizeIpOrCidr(string candidate, out string normalized)
    {
        normalized = string.Empty;
        var slash = candidate.IndexOf('/');
        var addressText = slash < 0 ? candidate : candidate[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        if (slash < 0)
        {
            normalized = address.ToString();
            return true;
        }

        if (candidate.IndexOf('/', slash + 1) >= 0 ||
            !int.TryParse(candidate[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        var maximumPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > maximumPrefix)
        {
            return false;
        }

        normalized = $"{address}/{prefix}";
        return true;
    }
}
