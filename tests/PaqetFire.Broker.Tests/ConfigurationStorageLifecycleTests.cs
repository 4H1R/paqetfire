using System.Security.AccessControl;
using System.Security.Principal;
using PaqetFire.Broker.Configuration;
using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Broker.Tests;

// These exercise the same identity boundary as the installed broker. The hosted
// Windows CI runner is elevated; an ordinary developer session runs the separate
// creation/denied-read tests instead of weakening the product DACL for this fixture.
public sealed class BrokerIdentityFactAttribute : FactAttribute
{
    public BrokerIdentityFactAttribute()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) &&
            identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            Skip = "Requires an elevated Administrator or SYSTEM token for protected configuration lifecycle.";
    }
}

public sealed class ConfigurationStorageLifecycleTests
{
    [BrokerIdentityFact]
    public void ReparseAncestorIsRejectedBeforeCreatingOutsideDirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-reparse-" + Guid.NewGuid());
        var root = Path.Combine(directory, "protected");
        var outside = Path.Combine(directory, "outside");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            Assert.Throws<UnauthorizedAccessException>(() => new AtomicConfigurationStore(
                root, [Path.Combine(link, "missing", "config.yaml")]));
            Assert.False(Directory.Exists(Path.Combine(outside, "missing")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(directory, recursive: true);
        }
    }

    [BrokerIdentityFact]
    public async Task ReplaceAndRollbackRetainProtectedSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-storage-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");
        try
        {
            var store = new AtomicConfigurationStore(directory, [path]);
            await store.WriteAsync(path, "first-secret");
            Assert.Equal("first-secret", await File.ReadAllTextAsync(path));
            AssertProtected(path);
            await store.WriteAsync(path, "second-secret");
            Assert.Equal("second-secret", await File.ReadAllTextAsync(path));
            AssertProtected(path);
            AssertProtected(path + ".bak");
            await store.RollbackAsync(path);
            Assert.Equal("first-secret", await File.ReadAllTextAsync(path));
            AssertProtected(path);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.WriteAsync(path, "cancelled", new CancellationToken(true)));
            Assert.Equal("first-secret", await File.ReadAllTextAsync(path));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [BrokerIdentityFact]
    public async Task MachineSettingsRoundTripAndReplacementKeepSecretsProtected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new MachineSettingsStore(path);
            var settings = new PaqetFireSettings { TransportKey = "test-transport-secret", LanSocksPassword = "test-lan-secret" };
            await store.SaveAsync(settings, CancellationToken.None);
            AssertProtected(path);
            Assert.DoesNotContain(settings.TransportKey, await File.ReadAllTextAsync(path));
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(settings.TransportKey, loaded!.TransportKey);
            Assert.Equal(settings.LanSocksPassword, loaded.LanSocksPassword);
            await store.SaveAsync(settings with { TransportKey = "replacement-secret" }, CancellationToken.None);
            Assert.Equal("replacement-secret", (await store.LoadAsync(CancellationToken.None))!.TransportKey);
            AssertProtected(path);
            Assert.Empty(Directory.GetFiles(directory, "*.new"));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void AssertProtected(string path)
    {
        var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.True(sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        });
    }
}
