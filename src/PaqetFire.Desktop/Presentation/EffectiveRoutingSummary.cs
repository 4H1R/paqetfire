using PaqetFire.Core.Configuration;

namespace PaqetFire.Desktop.Presentation;

/// <summary>The one-line Overview summary of what the saved profile routes and where it leaves.</summary>
public static class EffectiveRoutingSummary
{
    public static string Create(PaqetFireSettingsView? settings)
    {
        if (settings is null || (!settings.HasTransportKey && PaqetConfigurationValidator.RequiresTransportKey(settings.Advanced.KcpBlock)))
        {
            return "Set up a profile to preview which traffic will use PaqetFire.";
        }

        var parts = new List<string>
        {
            RoutingScopeText.Create(settings),
            settings.RegionalPreset == RegionalRoutingPreset.IranDirect ? "Iran direct" : "All destinations routed",
        };
        if (ExitText(settings) is { } exit)
        {
            parts.Add(exit);
        }

        parts.Add(settings.BypassLan ? "LAN bypass on" : "LAN routed");
        parts.Add(settings.DirectBitTorrent ? "BitTorrent direct" : "BitTorrent routed");
        parts.Add(settings.DirectRouteDestinations.Count switch
        {
            0 => "no custom direct destinations",
            1 => "1 custom direct destination",
            var count => $"{count} custom direct destinations",
        });
        parts.Add(settings.KillSwitchEnabled ? "Kill switch on" : "Kill switch off");
        if (settings.ShareWithLan)
        {
            parts.Add("LAN share on");
        }

        if (settings.ShareViaHotspot)
        {
            parts.Add("Hotspot share on");
        }

        return string.Join(" · ", parts);
    }

    private static string? ExitText(PaqetFireSettingsView settings) => settings.ExitProxy switch
    {
        ExitProxyKind.Psiphon => settings.Psiphon.EgressRegion is { Length: > 0 } region
            ? $"Exit: Psiphon ({region})"
            : "Exit: Psiphon",
        ExitProxyKind.OpenVpn => "Exit: OpenVPN",
        _ => null,
    };
}
