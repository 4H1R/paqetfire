using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Profiles;

/// <summary>
/// The only JSON seam for profile catalogs. Persistence always delegates secrets to
/// a protector; portable exports use a schema in which secret fields do not exist.
/// </summary>
public static class ProfileCatalogJson
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDocumentBytes = 8 * 1024 * 1024;

    private const string CatalogKind = "paqetfire-profile-catalog";
    private const string ExportKind = "paqetfire-profile-export";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string WriteProtected(ProfileCatalog catalog, IProfileSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(protector);
        var profiles = catalog.Profiles.Select(profile => new StoredProfile(
            profile.Id,
            ProfileSettingsData.FromSettings(profile.Settings),
            new ProtectedSecrets(
                ProtectOptional(protector, TransportKeyPurpose(profile.Id), profile.Settings.TransportKey),
                ProtectOptional(protector, LanPasswordPurpose(profile.Id), profile.Settings.LanSocksPassword))))
            .ToArray();
        return Serialize(
            new ProtectedCatalogDocument(
                CurrentSchemaVersion,
                CatalogKind,
                catalog.ActiveProfileId,
                catalog.DefaultProfileId,
                profiles),
            JsonOptions);
    }

    public static ProfileCatalog ReadProtected(string json, IProfileSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var document = Deserialize<ProtectedCatalogDocument>(json);
        ValidateHeader(document.SchemaVersion, document.Kind, CatalogKind);
        try
        {
            var profiles = RequiredProfiles(document.Profiles).Select(profile =>
            {
                ArgumentNullException.ThrowIfNull(profile.Settings);
                ArgumentNullException.ThrowIfNull(profile.Secrets);
                var settings = profile.Settings.ToSettings() with
                {
                    TransportKey = UnprotectOptional(
                        protector,
                        TransportKeyPurpose(profile.Id),
                        profile.Secrets.ProtectedTransportKey),
                    LanSocksPassword = UnprotectOptional(
                        protector,
                        LanPasswordPurpose(profile.Id),
                        profile.Secrets.ProtectedLanSocksPassword),
                };
                return new ConnectionProfile(profile.Id, settings);
            });
            return ProfileCatalog.Rehydrate(profiles, document.ActiveProfileId, document.DefaultProfileId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The protected profile catalog is invalid.", exception);
        }
    }

    public static string WriteRedactedExport(ProfileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var profiles = catalog.Profiles.Select(profile => new ExportedProfile(
            profile.Id,
            ProfileSettingsData.FromSettings(profile.Settings)))
            .ToArray();
        return Serialize(
            new RedactedExportDocument(
                CurrentSchemaVersion,
                ExportKind,
                catalog.ActiveProfileId,
                catalog.DefaultProfileId,
                profiles),
            JsonOptions);
    }

    /// <summary>
    /// Reads a portable export. Imported profiles deliberately have empty secret values
    /// and therefore must receive credentials before connection validation can succeed.
    /// </summary>
    public static ProfileCatalog ReadRedactedExport(string json)
    {
        var document = Deserialize<RedactedExportDocument>(json);
        ValidateHeader(document.SchemaVersion, document.Kind, ExportKind);
        try
        {
            var profiles = RequiredProfiles(document.Profiles).Select(profile =>
            {
                ArgumentNullException.ThrowIfNull(profile.Settings);
                return new ConnectionProfile(profile.Id, profile.Settings.ToSettings());
            });
            return ProfileCatalog.Rehydrate(profiles, document.ActiveProfileId, document.DefaultProfileId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The profile export is invalid.", exception);
        }
    }

    private static T Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
        {
            throw new InvalidDataException($"A profile document cannot exceed {MaximumDocumentBytes} bytes.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException("The profile document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The profile document is not valid JSON.", exception);
        }
    }

    private static string Serialize<T>(T document, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(document, options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
        {
            throw new InvalidDataException($"A profile document cannot exceed {MaximumDocumentBytes} bytes.");
        }

        return json;
    }

    private static IReadOnlyList<T> RequiredProfiles<T>(IReadOnlyList<T>? profiles) =>
        profiles ?? throw new InvalidDataException("The profile document has no profile list.");

    private static void ValidateHeader(int version, string? kind, string expectedKind)
    {
        if (version != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Profile schema version {version} is not supported; expected {CurrentSchemaVersion}.");
        }

        if (!string.Equals(kind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The profile document kind is invalid.");
        }
    }

    private static string? ProtectOptional(
        IProfileSecretProtector protector,
        string purpose,
        string? clearText)
    {
        if (string.IsNullOrEmpty(clearText))
        {
            return null;
        }

        var protectedText = protector.Protect(purpose, clearText);
        if (string.IsNullOrEmpty(protectedText) ||
            string.Equals(protectedText, clearText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The profile secret protector returned an unprotected value.");
        }

        return protectedText;
    }

    private static string UnprotectOptional(
        IProfileSecretProtector protector,
        string purpose,
        string? protectedText) =>
        string.IsNullOrEmpty(protectedText) ? string.Empty : protector.Unprotect(purpose, protectedText);

    private static string TransportKeyPurpose(Guid id) => $"PaqetFire.Profile.{id:N}.TransportKey.v1";

    private static string LanPasswordPurpose(Guid id) => $"PaqetFire.Profile.{id:N}.LanSocksPassword.v1";

    private sealed record ProtectedCatalogDocument(
        int SchemaVersion,
        string Kind,
        Guid ActiveProfileId,
        Guid DefaultProfileId,
        IReadOnlyList<StoredProfile>? Profiles);

    private sealed record RedactedExportDocument(
        int SchemaVersion,
        string Kind,
        Guid ActiveProfileId,
        Guid DefaultProfileId,
        IReadOnlyList<ExportedProfile>? Profiles);

    private sealed record StoredProfile(
        Guid Id,
        ProfileSettingsData? Settings,
        ProtectedSecrets? Secrets);

    private sealed record ExportedProfile(Guid Id, ProfileSettingsData? Settings);

    private sealed record ProtectedSecrets(
        string? ProtectedTransportKey,
        string? ProtectedLanSocksPassword);

    private sealed record ProfileSettingsData
    {
        public string ProfileName { get; init; } = "Default";

        public string ServerEndpoint { get; init; } = string.Empty;

        public RoutingMode RoutingMode { get; init; } = RoutingMode.AllApplications;

        public IReadOnlyList<string> SelectedApplications { get; init; } = [];

        public IReadOnlyList<string> UserExclusions { get; init; } = [];

        public IReadOnlyList<string> DirectRouteDestinations { get; init; } = [];

        public bool BypassLan { get; init; }

        public bool RouteTcp { get; init; } = true;

        public bool RouteUdp { get; init; } = true;

        public bool RouteIpv4 { get; init; } = true;

        public bool RouteIpv6 { get; init; } = true;

        public RegionalRoutingPreset RegionalPreset { get; init; } = RegionalRoutingPreset.IranDirect;

        public XrayDomainStrategy DomainStrategy { get; init; } = XrayDomainStrategy.IPIfNonMatch;

        public bool BlockAds { get; init; } = true;

        public bool BlockQuic { get; init; }

        public bool DirectBitTorrent { get; init; } = true;

        public bool KillSwitchEnabled { get; init; }

        public bool ShareWithLan { get; init; }

        public int LanSocksPort { get; init; } = 1082;

        public string LanSocksUsername { get; init; } = "paqetfire";

        public bool ShareViaHotspot { get; init; }

        public int HotspotSocksPort { get; init; } = 10808;

        public string KcpMode { get; init; } = "fast";

        public IReadOnlyList<string> LocalTcpFlags { get; init; } = ["PA"];

        public IReadOnlyList<string> RemoteTcpFlags { get; init; } = ["PA"];

        public static ProfileSettingsData FromSettings(PaqetFireSettings settings) => new()
        {
            ProfileName = settings.ProfileName,
            ServerEndpoint = settings.ServerEndpoint,
            RoutingMode = settings.RoutingMode,
            SelectedApplications = settings.SelectedApplications,
            UserExclusions = settings.UserExclusions,
            DirectRouteDestinations = settings.DirectRouteDestinations,
            BypassLan = settings.BypassLan,
            RouteTcp = settings.RouteTcp,
            RouteUdp = settings.RouteUdp,
            RouteIpv4 = settings.RouteIpv4,
            RouteIpv6 = settings.RouteIpv6,
            RegionalPreset = settings.RegionalPreset,
            DomainStrategy = settings.DomainStrategy,
            BlockAds = settings.BlockAds,
            BlockQuic = settings.BlockQuic,
            DirectBitTorrent = settings.DirectBitTorrent,
            KillSwitchEnabled = settings.KillSwitchEnabled,
            ShareWithLan = settings.ShareWithLan,
            LanSocksPort = settings.LanSocksPort,
            LanSocksUsername = settings.LanSocksUsername,
            ShareViaHotspot = settings.ShareViaHotspot,
            HotspotSocksPort = settings.HotspotSocksPort,
            KcpMode = settings.KcpMode,
            LocalTcpFlags = settings.LocalTcpFlags,
            RemoteTcpFlags = settings.RemoteTcpFlags,
        };

        public PaqetFireSettings ToSettings() => new()
        {
            ProfileName = ProfileName,
            ServerEndpoint = ServerEndpoint,
            TransportKey = string.Empty,
            RoutingMode = RoutingMode,
            SelectedApplications = SelectedApplications,
            UserExclusions = UserExclusions,
            DirectRouteDestinations = DirectRouteDestinations,
            BypassLan = BypassLan,
            RouteTcp = RouteTcp,
            RouteUdp = RouteUdp,
            RouteIpv4 = RouteIpv4,
            RouteIpv6 = RouteIpv6,
            RegionalPreset = RegionalPreset,
            DomainStrategy = DomainStrategy,
            BlockAds = BlockAds,
            BlockQuic = BlockQuic,
            DirectBitTorrent = DirectBitTorrent,
            KillSwitchEnabled = KillSwitchEnabled,
            ShareWithLan = ShareWithLan,
            LanSocksPort = LanSocksPort,
            LanSocksUsername = LanSocksUsername,
            LanSocksPassword = string.Empty,
            ShareViaHotspot = ShareViaHotspot,
            HotspotSocksPort = HotspotSocksPort,
            KcpMode = KcpMode,
            LocalTcpFlags = LocalTcpFlags,
            RemoteTcpFlags = RemoteTcpFlags,
        };
    }
}
