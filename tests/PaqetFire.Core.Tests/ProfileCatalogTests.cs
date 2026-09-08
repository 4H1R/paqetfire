using System.Text;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.Routing;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class ProfileCatalogTests
{
    private static readonly Guid HomeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CopyId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void EditsPreserveActiveAndDefaultSemantics()
    {
        var catalog = ProfileCatalog.Create(HomeId, Settings("Home"));

        catalog = catalog.Apply(new ProfileCatalogChange.Add(WorkId, Settings("Work"), MakeActive: true));
        catalog = catalog.Apply(new ProfileCatalogChange.MakeDefault(WorkId));
        catalog = catalog.Apply(new ProfileCatalogChange.Duplicate(WorkId, CopyId));
        catalog = catalog.Apply(new ProfileCatalogChange.Rename(CopyId, "  Travel  "));

        Assert.Equal(CopyId, catalog.ActiveProfileId);
        Assert.Equal(WorkId, catalog.DefaultProfileId);
        Assert.Equal("Travel", catalog.ActiveProfile.Settings.ProfileName);

        catalog = catalog.Apply(new ProfileCatalogChange.Delete(CopyId));
        Assert.Equal(WorkId, catalog.ActiveProfileId);
        Assert.Equal(WorkId, catalog.DefaultProfileId);

        catalog = catalog.Apply(new ProfileCatalogChange.Activate(HomeId));
        catalog = catalog.Apply(new ProfileCatalogChange.Delete(WorkId));
        Assert.Equal(HomeId, catalog.ActiveProfileId);
        Assert.Equal(HomeId, catalog.DefaultProfileId);
    }

    [Fact]
    public void DuplicateChoosesStableUniqueNamesAndCopiesAllSettings()
    {
        var original = Settings("Office") with
        {
            SelectedApplications = ["browser.exe"],
            TransportKey = "transport-secret",
            LanSocksPassword = "sharing-secret",
        };
        var catalog = ProfileCatalog.Create(HomeId, original)
            .Apply(new ProfileCatalogChange.Duplicate(HomeId, WorkId, MakeActive: false))
            .Apply(new ProfileCatalogChange.Duplicate(HomeId, CopyId, MakeActive: false));

        Assert.Equal("Office copy", catalog.Profiles[1].Settings.ProfileName);
        Assert.Equal("Office copy (2)", catalog.Profiles[2].Settings.ProfileName);
        Assert.Equal(original.TransportKey, catalog.Profiles[1].Settings.TransportKey);
        Assert.Equal(original.LanSocksPassword, catalog.Profiles[1].Settings.LanSocksPassword);
        Assert.Equal(original.SelectedApplications, catalog.Profiles[1].Settings.SelectedApplications);
        Assert.Equal(HomeId, catalog.ActiveProfileId);
    }

    [Fact]
    public void InvalidEditsAreRejectedWithoutMutatingTheCatalog()
    {
        var catalog = ProfileCatalog.Create(HomeId, Settings("Home"))
            .Apply(new ProfileCatalogChange.Add(WorkId, Settings("Work")));

        Assert.Throws<ProfileCatalogException>(() =>
            catalog.Apply(new ProfileCatalogChange.Rename(WorkId, "home")));
        Assert.Throws<ProfileCatalogException>(() =>
            catalog.Apply(new ProfileCatalogChange.Add(HomeId, Settings("Other"))));
        Assert.Equal(["Home", "Work"], catalog.Profiles.Select(profile => profile.Settings.ProfileName));

        var single = ProfileCatalog.Create(HomeId, Settings("Only"));
        Assert.Throws<ProfileCatalogException>(() =>
            single.Apply(new ProfileCatalogChange.Delete(HomeId)));
    }

    [Fact]
    public void ProtectedPersistenceNeverWritesPlaintextSecretsAndRoundTripsThem()
    {
        const string transportSecret = "transport-secret-that-must-not-leak";
        const string sharingSecret = "sharing-secret-that-must-not-leak";
        var catalog = ProfileCatalog.Create(HomeId, Settings("Home") with
        {
            TransportKey = transportSecret,
            LanSocksPassword = sharingSecret,
        });
        var protector = new TestProtector();

        var json = ProfileCatalogJson.WriteProtected(catalog, protector);

        Assert.DoesNotContain(transportSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(sharingSecret, json, StringComparison.Ordinal);
        Assert.Contains("protectedTransportKey", json, StringComparison.Ordinal);
        var loaded = ProfileCatalogJson.ReadProtected(json, protector);
        Assert.Equal(transportSecret, loaded.ActiveProfile.Settings.TransportKey);
        Assert.Equal(sharingSecret, loaded.ActiveProfile.Settings.LanSocksPassword);
    }

    [Fact]
    public void ProtectedPersistenceRejectsAnIdentityProtector()
    {
        var catalog = ProfileCatalog.Create(HomeId, Settings("Home"));

        Assert.Throws<InvalidOperationException>(() =>
            ProfileCatalogJson.WriteProtected(catalog, new IdentityProtector()));
    }

    [Fact]
    public void RedactedExportHasNoSecretFieldsOrValues()
    {
        const string transportSecret = "private-transport-key";
        const string sharingSecret = "private-sharing-password";
        var catalog = ProfileCatalog.Create(HomeId, Settings("Home") with
        {
            TransportKey = transportSecret,
            LanSocksPassword = sharingSecret,
        });

        var json = ProfileCatalogJson.WriteRedactedExport(catalog);

        Assert.DoesNotContain("transportKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lanSocksPassword", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(transportSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(sharingSecret, json, StringComparison.Ordinal);
        var imported = ProfileCatalogJson.ReadRedactedExport(json);
        Assert.Equal(string.Empty, imported.ActiveProfile.Settings.TransportKey);
        Assert.Equal(string.Empty, imported.ActiveProfile.Settings.LanSocksPassword);
        Assert.Equal(catalog.ActiveProfile.Settings.ServerEndpoint, imported.ActiveProfile.Settings.ServerEndpoint);
    }

    [Fact]
    public void RedactedImportRejectsInjectedSecretFields()
    {
        var json = ProfileCatalogJson.WriteRedactedExport(ProfileCatalog.Create(HomeId, Settings("Home")));
        var injected = json.Replace(
            "\"serverEndpoint\": \"server.example:8443\"",
            "\"serverEndpoint\": \"server.example:8443\",\r\n        \"transportKey\": \"injected\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ProfileCatalogJson.ReadRedactedExport(injected));
    }

    [Fact]
    public void SerializationAndLegacyMigrationAreDeterministic()
    {
        var first = ProfileCatalog.FromLegacySettings(HomeId, Settings("Legacy"));
        var second = ProfileCatalog.FromLegacySettings(HomeId, Settings("Legacy"));

        var firstJson = ProfileCatalogJson.WriteRedactedExport(first);
        var secondJson = ProfileCatalogJson.WriteRedactedExport(second);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(firstJson, ProfileCatalogJson.WriteRedactedExport(
            ProfileCatalogJson.ReadRedactedExport(firstJson)));
    }

    [Fact]
    public void DocumentsRejectWrongKindsAndFutureVersions()
    {
        var json = ProfileCatalogJson.WriteRedactedExport(ProfileCatalog.Create(HomeId, Settings("Home")));

        Assert.Throws<InvalidDataException>(() => ProfileCatalogJson.ReadRedactedExport(
            json.Replace("paqetfire-profile-export", "unknown-document", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ProfileCatalogJson.ReadRedactedExport(
            json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal)));
    }

    [Fact]
    public void SerializationRejectsDocumentsThatTheLoaderCannotAccept()
    {
        var oversizedEntry = new string('x', ProfileCatalogJson.MaximumDocumentBytes);
        var catalog = ProfileCatalog.Create(HomeId, Settings("Home") with
        {
            SelectedApplications = [oversizedEntry],
        });

        Assert.Throws<InvalidDataException>(() => ProfileCatalogJson.WriteRedactedExport(catalog));
        Assert.Throws<InvalidDataException>(() =>
            ProfileCatalogJson.WriteProtected(catalog, new TestProtector()));
    }

    private static PaqetFireSettings Settings(string name) => new()
    {
        ProfileName = name,
        ServerEndpoint = "server.example:8443",
        TransportKey = "secret",
        RoutingMode = RoutingMode.SelectedApplications,
        SelectedApplications = ["app.exe"],
        UserExclusions = ["excluded.exe"],
        DirectRouteDestinations = ["example.com"],
        BypassLan = true,
        RouteUdp = false,
        RouteIpv6 = false,
        RegionalPreset = RegionalRoutingPreset.None,
        DomainStrategy = XrayDomainStrategy.AsIs,
        BlockAds = false,
        BlockQuic = true,
        DirectBitTorrent = false,
        KillSwitchEnabled = true,
        ShareWithLan = true,
        LanSocksPort = 2082,
        LanSocksUsername = "family",
        LanSocksPassword = "password123",
        ShareViaHotspot = true,
        HotspotSocksPort = 2083,
        KcpMode = "fast2",
        LocalTcpFlags = ["S"],
        RemoteTcpFlags = ["SA"],
    };

    private sealed class TestProtector : IProfileSecretProtector
    {
        public string Protect(string purpose, string clearText) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{purpose}\0{clearText}"));

        public string Unprotect(string purpose, string protectedText)
        {
            var clear = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            var prefix = purpose + '\0';
            if (!clear.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Protected value purpose did not match.");
            }

            return clear[prefix.Length..];
        }
    }

    private sealed class IdentityProtector : IProfileSecretProtector
    {
        public string Protect(string purpose, string clearText) => clearText;

        public string Unprotect(string purpose, string protectedText) => protectedText;
    }
}
