using System.Text.Json;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Desktop.ViewModels;

public sealed class DesktopPreferences
{
    public string ProfileName { get; set; } = "My Paqet route";

    public string ServerEndpoint { get; set; } = string.Empty;

    public string LocalSocksEndpoint { get; set; } = "127.0.0.1:1080";

    public string InterfaceName { get; set; } = string.Empty;

    public string InterfaceGuid { get; set; } = string.Empty;

    public string LocalIpv4Address { get; set; } = string.Empty;

    public string RouterMac { get; set; } = string.Empty;

    public string LocalTcpFlags { get; set; } = "S";

    public string RemoteTcpFlags { get; set; } = "SA";

    public string KcpMode { get; set; } = "fast";

    public bool RouteAllApplications { get; set; } = true;

    public string SelectedApplications { get; set; } = string.Empty;

    public string UserExclusions { get; set; } = string.Empty;

    public string DirectRouteDestinations { get; set; } = string.Empty;

    public bool BypassLan { get; set; } = true;

    public RegionalRoutingPreset RegionalPreset { get; set; } = RegionalRoutingPreset.IranDirect;

    public XrayDomainStrategy DomainStrategy { get; set; } = XrayDomainStrategy.IPIfNonMatch;

    public bool BlockAds { get; set; } = true;

    public bool BlockQuic { get; set; }

    public bool DirectBitTorrent { get; set; } = true;

    public bool KillSwitch { get; set; }

    public bool ShareWithLan { get; set; }

    public int LanSocksPort { get; set; } = 1082;

    public string LanSocksUsername { get; set; } = "paqetfire";

    public bool ShareViaHotspot { get; set; }

    public int HotspotSocksPort { get; set; } = 10808;

    public bool RouteTcp { get; set; } = true;

    public bool RouteUdp { get; set; } = true;

    public bool RouteIpv4 { get; set; } = true;

    public bool RouteIpv6 { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool ConnectOnLaunch { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;
}

public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string filePath;

    public DesktopPreferencesStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PaqetFire");
        filePath = Path.Combine(directory, "desktop-settings.json");
    }

    public async Task<DesktopPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new DesktopPreferences();
            }

            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<DesktopPreferences>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? new DesktopPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopPreferences();
        }
    }

    public async Task SaveAsync(
        DesktopPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The settings directory is unavailable.");
        Directory.CreateDirectory(directory);

        var temporaryPath = filePath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    preferences,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, filePath, overwrite: true);
    }
}

public sealed class ActivityLogEntry
{
    public ActivityLogEntry()
    {
    }

    public ActivityLogEntry(string time, string message, string level = "INFO")
    {
        Time = time;
        Message = message;
        Level = level;
    }

    public string Time { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Level { get; set; } = "INFO";
}
