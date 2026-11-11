namespace PaqetFire.Desktop.Presentation;

/// <summary>What the LAN share switch and its warning say about the listener and who can reach it.</summary>
public static class LanShareStatusText
{
    /// <summary>
    /// The switch's "on" text: the listener runs only when the saved profile shares on the same
    /// port and the route is connected, because it is an inbound of the running Xray.
    /// </summary>
    public static string OnContent(bool savedShareOn, int? savedPort, int formPort, bool connected, string address)
    {
        if (!savedShareOn || savedPort != formPort)
        {
            return "On — save to apply";
        }

        if (!connected)
        {
            return "On — starts when you connect";
        }

        return string.IsNullOrWhiteSpace(address)
            ? $"On — listening on port {formPort}"
            : $"On — listening on {address.Trim()}:{formPort}";
    }

    /// <summary>
    /// A warning while sharing on a network the user has not marked trusted; the listener
    /// follows whichever network the adapter joins.
    /// </summary>
    public static string? ExposureWarning(bool shareOn, bool? networkTrusted, string? networkName)
    {
        if (!shareOn || networkTrusted == true)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(networkName) ? "this network" : networkName.Trim();
        return $"Anyone on {name} can reach this proxy with the password.";
    }
}
