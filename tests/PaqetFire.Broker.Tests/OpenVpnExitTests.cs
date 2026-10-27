using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class OpenVpnExitTests
{
    private const string Configuration = """
        client
        proto tcp
        remote vpn.example.net 443
        auth-user-pass
        up evil.bat
        <ca>
        -----BEGIN CERTIFICATE-----
        AAAA
        -----END CERTIFICATE-----
        </ca>
        """;

    [Fact]
    public async Task WithoutOpenVpnTheExitExplainsWhereToGetIt()
    {
        await using var adapter = new OpenVpnProcessAdapter(Options(new OpenVpnLookup(null, "Install OpenVPN Community.")));

        var status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal(EngineState.NotInstalled, status.State);
        Assert.Equal("Install OpenVPN Community.", status.Detail);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await adapter.StartAsync(CancellationToken.None));
        Assert.Equal("Install OpenVPN Community.", error.Message);
    }

    [Fact]
    public async Task LogLinesReportTheTunnelAndItsProblems()
    {
        await using var adapter = new OpenVpnProcessAdapter(Options(new OpenVpnLookup(null, "missing")));

        // Without a PaqetFire Exit adapter carrying the server's address, a completed
        // connection is refused rather than reported.
        adapter.HandleOutput("2026-10-26 12:00:00 Initialization Sequence Completed");
        Assert.False(adapter.TunnelActive);
        Assert.Contains(adapter.GetRecentLogs(), entry => entry.Message.Contains("no IPv4 address", StringComparison.Ordinal));
        adapter.HandleOutput("2026-10-26 12:01:00 SIGUSR1[soft,connection-reset] received, process restarting");
        adapter.HandleOutput("2026-10-26 12:01:05 AUTH: Received control message: AUTH_FAILED");

        Assert.Equal(4, adapter.GetRecentLogs().Count);
    }

    [Fact]
    public void OnlyAnAdministratorOnlyInstallationIsTrusted()
    {
        // System32 belongs to TrustedInstaller; a file in the user's temp folder does not.
        Assert.True(OpenVpnInstallation.IsAdministratorOnly(new FileInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))));
        Assert.True(OpenVpnInstallation.IsAdministratorOnly(new DirectoryInfo(Environment.SystemDirectory)));
        Assert.True(OpenVpnInstallation.AncestorsAreFixed(new DirectoryInfo(Environment.SystemDirectory)));
        Assert.False(OpenVpnInstallation.AncestorsAreFixed(Directory.CreateTempSubdirectory("PaqetFire-ancestors-")));
        var temporary = Path.GetTempFileName();
        try
        {
            Assert.False(OpenVpnInstallation.IsAdministratorOnly(new FileInfo(temporary)));
            Assert.False(OpenVpnInstallation.IsAdministratorOnly(new DirectoryInfo(Path.GetTempPath())));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [Fact]
    public void AnInstallationUsersCanChangeIsRefused()
    {
        var directory = Directory.CreateTempSubdirectory("PaqetFire-openvpn-");
        try
        {
            var executable = Path.Combine(directory.FullName, "openvpn.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(directory.FullName, "tapctl.exe"));

            var lookup = OpenVpnInstallation.Inspect(executable);

            Assert.Null(lookup.Installation);
            Assert.NotNull(lookup.Problem);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("2.5.11", "too old")]
    [InlineData("2.6.0", null)]
    [InlineData("2.6.23", null)]
    [InlineData("2.7.0", "SOCKS5 login")]
    [InlineData("2.7.8", "SOCKS5 login")]
    public void OnlyOpenVpn26CanBeTheExit(string version, string? problem)
    {
        var result = OpenVpnInstallation.CheckVersion(Version.Parse(version));

        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("10.96.0.2", 16, null)]
    [InlineData("172.20.1.5", 24, null)]
    [InlineData("10.0.0.2", 1, "wide enough")]
    [InlineData("10.0.0.2", 8, "wide enough")]
    [InlineData("8.8.8.2", 24, "public address")]
    [InlineData("192.168.1.77", 24, "overlaps")]
    [InlineData("192.168.0.2", 16, "overlaps")]
    public void TheServerCannotGiveTheExitAdapterANetworkThatCapturesOtherTraffic(string address, int prefix, string? problem)
    {
        var result = OpenVpnProcessAdapter.CheckAdapterAddress(
            System.Net.IPAddress.Parse(address), prefix, [(System.Net.IPAddress.Parse("192.168.1.10"), 24)]);

        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DnsServersOnTheExitAdapterAreRefused()
    {
        // Windows lists the site-local fec0:0:0:ffff::1 placeholders on adapters without DNS.
        Assert.Null(OpenVpnProcessAdapter.CheckAdapterDns([System.Net.IPAddress.Parse("fec0:0:0:ffff::1")]));
        Assert.Null(OpenVpnProcessAdapter.CheckAdapterDns([]));
        Assert.NotNull(OpenVpnProcessAdapter.CheckAdapterDns([System.Net.IPAddress.Parse("10.98.0.1")]));
    }

    [Fact]
    public async Task OnlyAWholeLogLineReportsTheTunnel()
    {
        await using var adapter = new OpenVpnProcessAdapter(Options(new OpenVpnLookup(null, "missing")));

        adapter.HandleOutput("PUSH: Received control message: 'PUSH_REPLY,echo Initialization Sequence Completed'");

        Assert.False(adapter.TunnelActive);
    }

    [Fact]
    public async Task ANewConfigurationDoesNotInheritTheStoredPassword()
    {
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with
        {
            ExitProxy = ExitProxyKind.OpenVpn,
            OpenVpn = new OpenVpnExitOptions(Configuration, "vpn-user", "vpn-password"),
        });
        var stored = fixture.Catalog.Catalog!.ActiveProfile.Settings;
        var elsewhere = Configuration.Replace("vpn.example.net", "attacker.example", StringComparison.Ordinal);

        await Assert.ThrowsAsync<ConfigurationValidationException>(async () => await fixture.Runtime.SaveSettingsAsync(
            stored with { TransportKey = string.Empty, OpenVpn = new OpenVpnExitOptions(elsewhere, "vpn-user") },
            connectAfterSave: false,
            CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationValidationException>(async () => await fixture.Runtime.SaveSettingsAsync(
            stored with { TransportKey = string.Empty, OpenVpn = new OpenVpnExitOptions(Username: "other-user") },
            connectAfterSave: false,
            CancellationToken.None));
        Assert.Equal("vpn-password", fixture.Catalog.Catalog!.ActiveProfile.Settings.OpenVpn.Password);
    }

    [Fact]
    public async Task AnOpenVpnProfileWritesItsFilesAndBindsXrayToTheExitAdapter()
    {
        await using var adapter = new OpenVpnProcessAdapter(Options(new OpenVpnLookup(null, "missing")));
        await using var fixture = await RuntimeFixture.StartAsync(
            settings => settings with
            {
                ExitProxy = ExitProxyKind.OpenVpn,
                OpenVpn = new OpenVpnExitOptions(Configuration, "vpn-user", "vpn-password"),
            },
            openVpn: adapter);

        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.True(adapter.IsEnabled);
        Assert.Equal(new InterfaceExitOutbound(OpenVpnConfigurationWriter.AdapterName), fixture.Xray.Last!.Exit);
        var configuration = fixture.Configurations.Files[fixture.Paths.OpenVpnConfigurationPath];
        Assert.Contains("socks-proxy 127.0.0.1 1080", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.bat", configuration, StringComparison.Ordinal);
        Assert.Equal("vpn-user\nvpn-password\n", fixture.Configurations.Files[fixture.Paths.OpenVpnUserCredentialsPath]);
        Assert.EndsWith("\n", fixture.Configurations.Files[fixture.Paths.OpenVpnSocksCredentialsPath], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProfileWithoutTheOpenVpnExitLeavesNoAccountOnDisk()
    {
        await using var adapter = new OpenVpnProcessAdapter(Options(new OpenVpnLookup(null, "missing")));
        adapter.UseExit(true);
        await using var fixture = await RuntimeFixture.StartAsync(openVpn: adapter);
        fixture.Configurations.Files[fixture.Paths.OpenVpnUserCredentialsPath] = "stale\naccount\n";

        await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.False(adapter.IsEnabled);
        Assert.Null(fixture.Xray.Last!.Exit);
        Assert.False(fixture.Configurations.Files.ContainsKey(fixture.Paths.OpenVpnUserCredentialsPath));
        Assert.False(fixture.Configurations.Files.ContainsKey(fixture.Paths.OpenVpnSocksCredentialsPath));
    }

    [Fact]
    public async Task ASaveWithoutTheSecretsKeepsTheStoredOnes()
    {
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with
        {
            ExitProxy = ExitProxyKind.OpenVpn,
            OpenVpn = new OpenVpnExitOptions(Configuration, "vpn-user", "vpn-password"),
        });
        var stored = fixture.Catalog.Catalog!.ActiveProfile.Settings;

        await fixture.Runtime.SaveSettingsAsync(
            stored with { TransportKey = string.Empty, OpenVpn = new OpenVpnExitOptions(Username: "vpn-user") },
            connectAfterSave: false,
            CancellationToken.None);

        Assert.Equal(
            new OpenVpnExitOptions(Configuration, "vpn-user", "vpn-password"),
            fixture.Catalog.Catalog!.ActiveProfile.Settings.OpenVpn);
    }

    [Fact]
    public async Task ADownloadedPsiphonIsInstalledOnlyWithThePinnedDigest()
    {
        using var directory = new TemporaryDirectory();
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var source = new BytesSource(bytes);
        var installer = Installer(directory.Path, Pin(bytes), source);

        await installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Download), OpenAsUser, CancellationToken.None);
        await installer.PendingDownload!;

        var psiphon = Assert.Single(installer.GetAvailability(), engine => engine.Kind == ExitProxyKind.Psiphon);
        Assert.True(psiphon.IsInstalled);
        Assert.Null(psiphon.Detail);
        Assert.Equal(RoutingDataNetworkPath.Direct, source.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(installer.PsiphonPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchThePinIsDiscarded()
    {
        using var directory = new TemporaryDirectory();
        var pinned = RandomNumberGenerator.GetBytes(4096);
        var installer = Installer(directory.Path, Pin(pinned), new BytesSource(RandomNumberGenerator.GetBytes(4096)));

        await installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Download), OpenAsUser, CancellationToken.None);
        await installer.PendingDownload!;

        var psiphon = Assert.Single(installer.GetAvailability(), engine => engine.Kind == ExitProxyKind.Psiphon);
        Assert.False(psiphon.IsInstalled);
        Assert.Contains("SHA-256", psiphon.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task TheKillSwitchKeepsADownloadFromLeavingOutsideTheRoute()
    {
        using var directory = new TemporaryDirectory();
        var bytes = RandomNumberGenerator.GetBytes(64);
        var source = new BytesSource(bytes);
        var installer = Installer(directory.Path, Pin(bytes), source, new RoutingDataRouteState(false, false, IsGuarded: true));

        await installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Download), OpenAsUser, CancellationToken.None);
        await installer.PendingDownload!;

        Assert.Null(source.Path);
        Assert.Contains("kill switch", Assert.Single(installer.GetAvailability(), engine => engine.Kind == ExitProxyKind.Psiphon).Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APsiphonFileTheUserDownloadedIsCheckedAndCopied()
    {
        using var directory = new TemporaryDirectory();
        var bytes = RandomNumberGenerator.GetBytes(2048);
        var installer = Installer(directory.Path, Pin(bytes), new BytesSource([]));
        var chosen = Path.Combine(Path.GetTempPath(), $"psiphon-{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllBytesAsync(chosen, RandomNumberGenerator.GetBytes(2048));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Import, chosen), OpenAsUser, CancellationToken.None));

            await File.WriteAllBytesAsync(chosen, bytes);
            await installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Import, chosen), OpenAsUser, CancellationToken.None);

            Assert.True(Assert.Single(installer.GetAvailability(), engine => engine.Kind == ExitProxyKind.Psiphon).IsInstalled);
        }
        finally
        {
            File.Delete(chosen);
        }
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\psiphon.exe")]
    [InlineData(@"\\?\C:\psiphon.exe")]
    [InlineData("psiphon.exe")]
    [InlineData("")]
    public async Task OnlyALocalFileCanBeImported(string path)
    {
        using var directory = new TemporaryDirectory();
        var installer = Installer(directory.Path, Pin([1, 2, 3]), new BytesSource([]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Import, path), OpenAsUser, CancellationToken.None));
    }

    [Fact]
    public async Task PsiphonCannotBeRemovedWhileTheRouteUsesIt()
    {
        using var directory = new TemporaryDirectory();
        var bytes = RandomNumberGenerator.GetBytes(64);
        var inUse = true;
        var installer = Installer(directory.Path, Pin(bytes), new BytesSource(bytes), inUse: () => inUse);
        await installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Download), OpenAsUser, CancellationToken.None);
        await installer.PendingDownload!;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Remove), OpenAsUser, CancellationToken.None));
        inUse = false;
        await installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Remove), OpenAsUser, CancellationToken.None);

        Assert.False(File.Exists(installer.PsiphonPath));
    }

    [Fact]
    public async Task OpenVpnIsNeverDownloadedByTheBroker()
    {
        using var directory = new TemporaryDirectory();
        var installer = Installer(directory.Path, Pin([1]), new BytesSource([1]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.ApplyAsync(new ExitEngineAction(ExitProxyKind.OpenVpn, ExitEngineOperation.Download), OpenAsUser, CancellationToken.None));
        var openVpn = Assert.Single(installer.GetAvailability(), engine => engine.Kind == ExitProxyKind.OpenVpn);
        Assert.False(openVpn.CanDownload);
        Assert.Equal("Install OpenVPN.", openVpn.Detail);
        Assert.Equal(OpenVpnInstallation.Download.ToString(), openVpn.DownloadUrl);
    }

    private static FileStream OpenAsUser(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public async Task AnImportIsOpenedThroughTheRequestingUser()
    {
        using var directory = new TemporaryDirectory();
        var bytes = RandomNumberGenerator.GetBytes(256);
        var installer = Installer(directory.Path, Pin(bytes), new BytesSource([]));
        var chosen = Path.Combine(Path.GetTempPath(), $"psiphon-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(chosen, bytes);
        try
        {
            var opened = new List<string>();
            await installer.ApplyAsync(
                new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Import, chosen),
                path =>
                {
                    opened.Add(path);
                    return OpenAsUser(path);
                },
                CancellationToken.None);

            Assert.Equal([chosen], opened);
            await Assert.ThrowsAsync<InvalidOperationException>(() => installer.ApplyAsync(
                new ExitEngineAction(ExitProxyKind.Psiphon, ExitEngineOperation.Import, chosen),
                BrokerRequester.Unknown.OpenFileAsUser,
                CancellationToken.None));
        }
        finally
        {
            File.Delete(chosen);
        }
    }

    private static OpenVpnProcessOptions Options(OpenVpnLookup lookup) => new()
    {
        ConfigurationPath = Path.Combine(Path.GetTempPath(), "missing-openvpn.ovpn"),
        AdapterName = OpenVpnConfigurationWriter.AdapterName,
        Locate = () => lookup,
        AddressWait = TimeSpan.Zero,
    };

    private static DownloadableEngine Pin(byte[] bytes) => new(
        ExitProxyKind.Psiphon,
        "test",
        new Uri("https://github.com/example/binaries/raw/0000000000000000000000000000000000000000/psiphon.exe"),
        Convert.ToHexStringLower(SHA256.HashData(bytes)),
        bytes.Length,
        "psiphon-tunnel-core.exe");

    private static ExitEngineInstaller Installer(
        string directory,
        DownloadableEngine engine,
        IRoutingDataSource source,
        RoutingDataRouteState? route = null,
        Func<bool>? inUse = null) => new(
        engine,
        new ProtectedDirectory(directory, ProtectedDirectoryPolicy.CurrentUser()),
        source,
        _ => ValueTask.FromResult(route ?? new RoutingDataRouteState(false, false)),
        () => new OpenVpnLookup(null, "Install OpenVPN."),
        inUse ?? (() => false),
        NullLogger.Instance);

    private sealed class BytesSource(byte[] bytes) : IRoutingDataSource
    {
        public RoutingDataNetworkPath? Path { get; private set; }

        public Task<string> GetLatestTagAsync(RoutingDataNetworkPath path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task DownloadAsync(
            RoutingDataNetworkPath path,
            Uri uri,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            Path = path;
            await destination.WriteAsync(bytes, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PaqetFire-exit-engines", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
