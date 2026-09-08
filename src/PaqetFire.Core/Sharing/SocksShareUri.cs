namespace PaqetFire.Core.Sharing;

public static class SocksShareUri
{
    public static string Create(SocksShareEndpoint endpoint, SharedProxyCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credentials);

        return $"socks5://{Uri.EscapeDataString(credentials.Username)}:" +
               $"{Uri.EscapeDataString(credentials.Password)}@{endpoint.Address}:{endpoint.Port}";
    }

    // QR encoders should receive this exact text. Bitmap rendering is deliberately
    // left to the UI so Core does not acquire a graphics or QR package dependency.
    public static string CreateQrPayload(
        SocksShareEndpoint endpoint,
        SharedProxyCredentials credentials) =>
        Create(endpoint, credentials);
}
