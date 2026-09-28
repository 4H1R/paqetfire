using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ProfileManagementTests
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(ProfileActionKind.Activate)]
    [InlineData(ProfileActionKind.Delete)]
    public async Task ImportedProfilesCanBeSelectedOrDeletedBeforeEnteringKeys(ProfileActionKind kind)
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.ImportRedacted,
            Payload: Export()), CancellationToken.None);

        var snapshot = await fixture.Runtime.ManageProfilesAsync(new(kind,
            kind == ProfileActionKind.Delete ? FirstId : SecondId), CancellationToken.None);

        Assert.Equal(SecondId, Assert.Single(snapshot.ProfileCatalog!.Profiles, profile => profile.IsActive).Id);
        Assert.False(snapshot.Settings!.HasTransportKey);
        Assert.False(snapshot.IsConfigured);
        Assert.Equal(kind == ProfileActionKind.Delete ? 1 : 2, snapshot.ProfileCatalog!.Profiles.Count);
        Assert.Equal(0, fixture.Controller.ConnectionAttempts);
    }

    [Fact]
    public async Task ImportAndSwitchPreserveApplicationSocksCredentials()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.ImportRedacted,
            Payload: Export()), CancellationToken.None);

        AssertCredentials(fixture.Store.Catalog!);
        await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.Activate, SecondId), CancellationToken.None);
        AssertCredentials(fixture.Store.Catalog!);
        Assert.All(fixture.Store.Catalog!.Profiles, profile => Assert.Empty(profile.Settings.TransportKey));
    }

    [Fact]
    public async Task DeletingTheOnlyInvalidProfileReplacesItWithAnEmptyEditableProfile()
    {
        await using var fixture = new Fixture();
        var snapshot = await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.Delete, FirstId), CancellationToken.None);

        Assert.DoesNotContain(snapshot.ProfileCatalog!.Profiles, profile => profile.Id == FirstId);
        Assert.Single(snapshot.ProfileCatalog.Profiles);
        Assert.False(snapshot.Settings!.HasTransportKey);
        Assert.Empty(snapshot.Settings.ServerEndpoint);
        AssertCredentials(fixture.Store.Catalog!);
    }

    private static void AssertCredentials(ProfileCatalog catalog) => Assert.All(catalog.Profiles, profile =>
    {
        Assert.Equal("local-user", profile.Settings.LanSocksUsername);
        Assert.Equal("local-password", profile.Settings.LanSocksPassword);
    });

    [Fact]
    public async Task KeysCanBeSavedIndependentlyWithoutRunningTheEngines()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.Import, Payload: Export()), CancellationToken.None);
        await fixture.Runtime.SaveSettingsAsync(fixture.Store.Catalog!.ActiveProfile.Settings with
        {
            TransportKey = "first-test-key", LanSocksPassword = string.Empty,
            LocalTcpFlags = ["S", "S", "AN"],
            Advanced = new() { KcpMtu = 1200 },
        }, false, CancellationToken.None);
        await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.Activate, SecondId), CancellationToken.None);
        await fixture.Runtime.SaveSettingsAsync(fixture.Store.Catalog!.ActiveProfile.Settings with
        {
            TransportKey = "second-test-key", LanSocksPassword = "rotated-test-password",
        }, false, CancellationToken.None);
        var exported = ProfileCatalogJson.ReadImport(await fixture.Runtime.ExportProfilesAsync(CancellationToken.None));
        Assert.Equal("first-test-key", exported.Profiles[0].Settings.TransportKey);
        Assert.Equal("second-test-key", exported.Profiles[1].Settings.TransportKey);
        Assert.Equal(["S", "S", "AN"], exported.Profiles[0].Settings.LocalTcpFlags);
        Assert.Equal(1200, exported.Profiles[0].Settings.Advanced.KcpMtu);
        await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.Activate, FirstId), CancellationToken.None);
        Assert.Equal("rotated-test-password", fixture.Store.Catalog!.ActiveProfile.Settings.LanSocksPassword);
        Assert.Equal(0, fixture.Controller.ConnectionAttempts);
    }

    [Fact]
    public async Task FactoryResetStopsRouteClearsSecretsAndRemovesGeneratedConfigurations()
    {
        await using var fixture = new Fixture();
        fixture.Store.Catalog = ProfileCatalog.Create(FirstId, new()
        {
            ProfileName = "Changed", ServerEndpoint = "server.example:8443", TransportKey = "test-key",
            LanSocksUsername = "changed-user", LanSocksPassword = "changed-password", KillSwitchEnabled = true,
            Advanced = new() { KcpMtu = 1200, ConnectionCount = 4 },
        }).Apply(new ProfileCatalogChange.Duplicate(FirstId, SecondId));
        fixture.Controller.State = ConnectionState.Connected;
        var snapshot = await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.FactoryReset), CancellationToken.None);
        Assert.Equal(ConnectionState.Disconnected, snapshot.ConnectionState);
        Assert.False(snapshot.IsConfigured);
        Assert.Equal(1, fixture.Controller.Disconnects);
        Assert.Equal(0, fixture.Controller.ConnectionAttempts);
        Assert.False(snapshot.Settings!.HasTransportKey);
        Assert.False(snapshot.Settings.HasLanSocksPassword);
        Assert.False(snapshot.Settings.KillSwitchEnabled);
        Assert.Equal(new PaqetAdvancedOptions(), snapshot.Settings.Advanced);
        Assert.Equal(3, fixture.Configurations.DeletedPaths.Count);
        Assert.Single(fixture.Store.Catalog!.Profiles);
        Assert.Equal("paqetfire", fixture.Store.Catalog.ActiveProfile.Settings.LanSocksUsername);
    }

    [Fact]
    public async Task ResetFailureToStopTheRouteLeavesProfilesAndFilesUntouched()
    {
        await using var fixture = new Fixture();
        fixture.Controller.DisconnectError = new IOException("Test stop failure");
        await Assert.ThrowsAsync<IOException>(() => fixture.Runtime.ManageProfilesAsync(
            new(ProfileActionKind.FactoryReset), CancellationToken.None).AsTask());
        Assert.Equal(FirstId, fixture.Store.Catalog!.ActiveProfileId);
        AssertCredentials(fixture.Store.Catalog);
        Assert.Empty(fixture.Configurations.DeletedPaths);
    }

    [Fact]
    public async Task FreshInstallationCanImportPortableProfilesWithKeys()
    {
        await using var fixture = new Fixture();
        fixture.Store.Catalog = null;
        var portable = ProfileCatalogJson.WritePortableExport(ProfileCatalog.Create(FirstId, new()
        {
            ProfileName = "Imported", ServerEndpoint = "server.example:8443", TransportKey = "test-key",
        }));
        var snapshot = await fixture.Runtime.ManageProfilesAsync(new(ProfileActionKind.Import, Payload: portable), CancellationToken.None);
        Assert.True(snapshot.IsConfigured);
        Assert.True(snapshot.Settings!.HasTransportKey);
        Assert.Equal("test-key", fixture.Store.Catalog!.ActiveProfile.Settings.TransportKey);
    }

    [Fact]
    public async Task IncompleteProfileCannotReplaceARunningRoute()
    {
        await using var fixture = new Fixture();
        fixture.Controller.State = ConnectionState.Connected;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.ManageProfilesAsync(
            new(ProfileActionKind.Delete, FirstId), CancellationToken.None).AsTask());
        Assert.Equal(FirstId, fixture.Store.Catalog!.ActiveProfileId);
        Assert.Equal(0, fixture.Controller.Disconnects);
    }

    private static string Export() => ProfileCatalogJson.WriteRedactedExport(
        ProfileCatalog.Create(FirstId, new() { ProfileName = "First", ServerEndpoint = "first.example:8443",
            ShareWithLan = true, LanSocksUsername = "imported-user" })
        .Apply(new ProfileCatalogChange.Add(SecondId, new() { ProfileName = "Second", ServerEndpoint = "second.example:8443",
            ShareWithLan = true, LanSocksUsername = "another-imported-user" })));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PaqetProcessAdapter paqet;
        private readonly XrayProcessAdapter xray;
        public CatalogStore Store { get; } = new();
        public RecordingController Controller { get; } = new();
        public RecordingConfigurations Configurations { get; } = new();
        public PaqetFireRuntime Runtime { get; }

        public Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "PaqetFire-profile-tests", Guid.NewGuid().ToString("N"));
            var paths = new RuntimePaths(root, Path.Combine(root, "paqet.exe"), Path.Combine(root, "paqet.yaml"),
                Path.Combine(root, "xray.exe"), Path.Combine(root, "xray.json"), Path.Combine(root, "geoip.dat"),
                Path.Combine(root, "geosite.dat"), Path.Combine(root, "proxifyre.exe"), Path.Combine(root, "proxifyre.json"),
                Path.Combine(root, "settings.json"));
            paqet = new(BundledEngineOptions.CreatePaqet(paths));
            xray = new(BundledEngineOptions.CreateXray(paths));
            // Profile editing must not need engines, payloads, or a usable network adapter.
            Runtime = new(Controller, paqet, xray, new SettingsStore(), Store,
                Configurations, null!, null!, null!, null!, null!, null!, new PrerequisiteInspector(), null!, paths,
                NullLogger<PaqetFireRuntime>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await paqet.DisposeAsync();
            await xray.DisposeAsync();
        }
    }

    private sealed class CatalogStore : IMachineProfileCatalogStore
    {
        public ProfileCatalog? Catalog { get; set; } = ProfileCatalog.Create(FirstId, new()
        {
            ProfileName = "Local", LanSocksUsername = "local-user", LanSocksPassword = "local-password",
        });
        public ValueTask<ProfileCatalog?> LoadAsync(CancellationToken token) => ValueTask.FromResult(Catalog);
        public ValueTask SaveAsync(ProfileCatalog catalog, CancellationToken token)
        {
            Catalog = catalog;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SettingsStore : IMachineSettingsStore
    {
        public ValueTask<PaqetFireSettings?> LoadAsync(CancellationToken token) => ValueTask.FromResult<PaqetFireSettings?>(null);
        public ValueTask SaveAsync(PaqetFireSettings settings, CancellationToken token) => ValueTask.CompletedTask;
    }

    private sealed class RecordingController : IConnectionController
    {
        public ConnectionState State { get; set; } = ConnectionState.Disconnected;
        public int Disconnects { get; private set; }
        public Exception? DisconnectError { get; set; }
        public int ConnectionAttempts { get; private set; }
        public ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken token) => ValueTask.FromResult(new ConnectionStatus(
            State, new(EngineKind.Paqet, EngineState.Stopped),
            new(EngineKind.Xray, EngineState.Stopped), new(EngineKind.ProxiFyre, EngineState.Stopped)));
        public ValueTask DisconnectAsync(CancellationToken token)
        {
            if (DisconnectError is { } error) return ValueTask.FromException(error);
            Disconnects++;
            State = ConnectionState.Disconnected;
            return ValueTask.CompletedTask;
        }
        public ValueTask GuardAsync(CancellationToken token) => throw new InvalidOperationException("Unexpected guard activation.");
        public ValueTask ConnectAsync(CancellationToken token)
        {
            ConnectionAttempts++;
            throw new InvalidOperationException("Unexpected connection attempt.");
        }
    }

    private sealed class RecordingConfigurations : IAtomicConfigurationStore
    {
        public List<string> DeletedPaths { get; } = [];
        public Task DeleteAsync(string destinationPath, CancellationToken token = default)
        {
            DeletedPaths.Add(destinationPath);
            return Task.CompletedTask;
        }
        public Task WriteAsync(string path, string text, CancellationToken token = default) => throw new InvalidOperationException("Unexpected engine configuration write.");
        public Task RollbackAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }
}
