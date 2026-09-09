using PaqetFire.Broker.Configuration;
using System.Security.Cryptography;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Network;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.Routing;
using System.Text;

namespace PaqetFire.Broker.Runtime;

public sealed class PaqetFireRuntime(
    IConnectionController connectionController,
    PaqetProcessAdapter paqetAdapter,
    XrayProcessAdapter xrayAdapter,
    IMachineSettingsStore settingsStore,
    IMachineProfileCatalogStore profileCatalogStore,
    IAtomicConfigurationStore configurationStore,
    IPaqetConfigurationWriter paqetWriter,
    IXrayConfigurationWriter xrayWriter,
    IProxiFyreConfigurationWriter proxiFyreWriter,
    NetworkEnvironmentDetector networkDetector,
    HotspotNetworkDetector hotspotDetector,
    PayloadIntegrityInspector payloadInspector,
    PrerequisiteInspector prerequisiteInspector,
    IConnectionVerifier connectionVerifier,
    RuntimePaths paths,
    ILogger<PaqetFireRuntime> logger) : IPaqetFireRuntime
{
    private const int RecentLogBudgetBytes = 24 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SnapshotMetadataCache snapshotMetadata = new();
    private ConnectionVerificationReport? latestVerification;
    private ProfileCatalog? profileCatalog;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var settings = await TryLoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        profileCatalog = await profileCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (profileCatalog is null && settings is not null)
        {
            profileCatalog = ProfileCatalog.FromLegacySettings(Guid.NewGuid(), settings);
            await profileCatalogStore.SaveAsync(profileCatalog, cancellationToken).ConfigureAwait(false);
        }
        else if (profileCatalog is not null)
        {
            settings = profileCatalog.ActiveProfile.Settings;
            await SaveLegacySettingsMirrorAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        await snapshotMetadata.UpdateSettingsAsync(settings).ConfigureAwait(false);
        if (settings?.KillSwitchEnabled != true ||
            !File.Exists(paths.ProxiFyreConfigurationPath))
        {
            return;
        }

        try
        {
            await connectionController.GuardAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("The routing kill switch was restored during broker startup.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The routing kill switch could not be restored during broker startup.");
        }
    }

    public async ValueTask<BrokerSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var settings = await snapshotMetadata.GetSettingsAsync(TryLoadSettingsAsync, cancellationToken)
            .ConfigureAwait(false);
        return await CreateSnapshotAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BrokerSnapshot> SaveSettingsAsync(
        PaqetFireSettings settings,
        bool connectAfterSave,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadActiveSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(settings.TransportKey) && existing is not null)
            {
                settings = settings with { TransportKey = existing.TransportKey };
            }

            if (string.IsNullOrEmpty(settings.LanSocksPassword) && existing is not null)
            {
                settings = settings with { LanSocksPassword = existing.LanSocksPassword };
            }

            var errors = PaqetFireSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new ConfigurationValidationException(errors);
            }

            var current = await connectionController.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            var reconnect = connectAfterSave || current.State == ConnectionState.Connected;
            if (reconnect)
            {
                EnsurePrerequisitesAvailable();
            }

            var inspection = await payloadInspector.InspectAsync(cancellationToken)
                .ConfigureAwait(false);
            if (inspection.State != PayloadInspectionState.Ready)
            {
                throw new InvalidOperationException(inspection.Detail);
            }

            var normalized = Normalize(settings);
            latestVerification = null;
            var catalogToSave = profileCatalog is null
                ? ProfileCatalog.Create(Guid.NewGuid(), normalized)
                : profileCatalog.Apply(new ProfileCatalogChange.Update(
                    profileCatalog.ActiveProfileId,
                    normalized));
            string? interfaceName = null;
            var activationError = await ConfigurationActivation.ApplyAsync(
                connectionController,
                async () =>
                {
                    interfaceName = await WriteEngineConfigurationsAsync(normalized, cancellationToken)
                        .ConfigureAwait(false);
                    await profileCatalogStore.SaveAsync(catalogToSave, cancellationToken).ConfigureAwait(false);
                    profileCatalog = catalogToSave;
                    await snapshotMetadata.UpdateSettingsAsync(normalized).ConfigureAwait(false);
                    await SaveLegacySettingsMirrorAsync(normalized, cancellationToken).ConfigureAwait(false);
                },
                current.State != ConnectionState.Disconnected,
                normalized.KillSwitchEnabled,
                reconnect,
                cancellationToken).ConfigureAwait(false);
            var operationWarning = activationError is null ? null
                : $"The profile was saved, but the route could not be activated. {activationError.Message}";
            if (activationError is not null)
                logger.LogWarning(activationError, "The saved profile could not be activated.");

            logger.LogInformation(
                "Applied PaqetFire profile {ProfileName} on adapter {InterfaceName}.",
                normalized.ProfileName,
                interfaceName);
            var snapshot = await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(normalized), cancellationToken).ConfigureAwait(false);
            return snapshot with { OperationWarning = operationWarning };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BrokerSnapshot> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadActiveSettingsAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Save a valid Paqet profile before connecting.");
            EnsurePrerequisitesAvailable();

            var inspection = await payloadInspector.InspectAsync(cancellationToken)
                .ConfigureAwait(false);
            if (inspection.State != PayloadInspectionState.Ready)
            {
                throw new InvalidOperationException(inspection.Detail);
            }

            settings = Normalize(settings);
            latestVerification = null;
            var errors = PaqetFireSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new ConfigurationValidationException(errors);
            }

            await WriteEngineConfigurationsAsync(settings, cancellationToken).ConfigureAwait(false);

            if (settings.KillSwitchEnabled)
            {
                await connectionController.GuardAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await connectionController.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (settings.KillSwitchEnabled)
                {
                    await connectionController.GuardAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
            await snapshotMetadata.UpdateSettingsAsync(settings).ConfigureAwait(false);
            return await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(settings), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BrokerSnapshot> DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            latestVerification = null;
            var settings = await snapshotMetadata.GetSettingsAsync(TryLoadSettingsAsync, cancellationToken)
                .ConfigureAwait(false);
            if (settings?.KillSwitchEnabled == true)
            {
                await connectionController.GuardAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await connectionController.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            return await CreateSnapshotAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StopEnginesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            latestVerification = null;
            await connectionController.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BrokerSnapshot> VerifyConnectionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadActiveSettingsAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Save a valid Paqet profile before verifying the route.");
            var status = await connectionController.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            latestVerification = await connectionVerifier.VerifyAsync(status, Normalize(settings), cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Connection verification completed with {PassedCount} passed and {FailedCount} failed checks.",
                latestVerification.PassedCount,
                latestVerification.FailedCount);
            return await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(settings), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BrokerSnapshot> ManageProfilesAsync(
        ProfileAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await EnsureProfileCatalogAsync(cancellationToken).ConfigureAwait(false);
            var currentStatus = await connectionController.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            ProfileCatalog updated;
            switch (action.Kind)
            {
                case ProfileActionKind.Rename:
                    updated = catalog.Apply(new ProfileCatalogChange.Rename(
                        action.ProfileId ?? throw new InvalidOperationException("Choose a profile to rename."),
                        action.Name ?? throw new InvalidOperationException("Enter a profile name.")));
                    break;
                case ProfileActionKind.Duplicate:
                    updated = catalog.Apply(new ProfileCatalogChange.Duplicate(
                        action.ProfileId ?? catalog.ActiveProfileId,
                        Guid.NewGuid(),
                        action.Name,
                        MakeActive: true));
                    break;
                case ProfileActionKind.Delete:
                    updated = catalog.Apply(new ProfileCatalogChange.Delete(
                        action.ProfileId ?? throw new InvalidOperationException("Choose a profile to delete.")));
                    break;
                case ProfileActionKind.Activate:
                    updated = catalog.Apply(new ProfileCatalogChange.Activate(
                        action.ProfileId ?? throw new InvalidOperationException("Choose a profile to activate.")));
                    break;
                case ProfileActionKind.MakeDefault:
                    updated = catalog.Apply(new ProfileCatalogChange.MakeDefault(
                        action.ProfileId ?? throw new InvalidOperationException("Choose a default profile.")));
                    break;
                case ProfileActionKind.ImportRedacted:
                    if (currentStatus.State != ConnectionState.Disconnected)
                    {
                        throw new InvalidOperationException("Disconnect the route before replacing profiles from an import.");
                    }
                    updated = ProfileCatalogJson.ReadRedactedExport(
                        action.Payload ?? throw new InvalidOperationException("The profile import is empty."));
                    break;
                default:
                    throw new InvalidOperationException("The profile action is not supported.");
            }

            var activeChanged = updated.ActiveProfileId != catalog.ActiveProfileId ||
                                action.Kind == ProfileActionKind.ImportRedacted;
            if (!activeChanged)
            {
                await profileCatalogStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                profileCatalog = updated;
                if (action.Kind == ProfileActionKind.Rename &&
                    action.ProfileId == catalog.ActiveProfileId)
                {
                    var renamed = Normalize(updated.ActiveProfile.Settings);
                    await snapshotMetadata.UpdateSettingsAsync(renamed).ConfigureAwait(false);
                    await SaveLegacySettingsMirrorAsync(renamed, cancellationToken).ConfigureAwait(false);
                }
                return await CreateSnapshotAsync(
                    PaqetFireSettingsView.FromSettings(updated.ActiveProfile.Settings),
                    cancellationToken).ConfigureAwait(false);
            }

            var selected = Normalize(updated.ActiveProfile.Settings);
            if (action.Kind != ProfileActionKind.ImportRedacted)
            {
                var errors = PaqetFireSettingsValidator.Validate(selected);
                if (errors.Count > 0)
                {
                    throw new ConfigurationValidationException(errors);
                }
            }

            latestVerification = null;
            var reconnect = currentStatus.State != ConnectionState.Disconnected;
            string? activationError = null;
            if (reconnect)
            {
                EnsurePrerequisitesAvailable();
                var error = await ConfigurationActivation.ApplyAsync(
                    connectionController,
                    async () =>
                    {
                        await WriteEngineConfigurationsAsync(selected, cancellationToken).ConfigureAwait(false);
                        await profileCatalogStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                        profileCatalog = updated;
                        await snapshotMetadata.UpdateSettingsAsync(selected).ConfigureAwait(false);
                        await SaveLegacySettingsMirrorAsync(selected, cancellationToken).ConfigureAwait(false);
                    },
                    stopCurrentRoute: true,
                    enableGuard: selected.KillSwitchEnabled,
                    reconnect: true,
                    cancellationToken).ConfigureAwait(false);
                activationError = error?.Message;
            }
            else
            {
                await profileCatalogStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                profileCatalog = updated;
                await snapshotMetadata.UpdateSettingsAsync(selected).ConfigureAwait(false);
                await SaveLegacySettingsMirrorAsync(selected, cancellationToken).ConfigureAwait(false);
            }

            var snapshot = await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(selected), cancellationToken)
                .ConfigureAwait(false);
            return activationError is null
                ? snapshot
                : snapshot with { OperationWarning = $"The profile changed, but its route could not be activated. {activationError}" };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<string> ExportProfilesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await EnsureProfileCatalogAsync(cancellationToken).ConfigureAwait(false);
            return ProfileCatalogJson.WriteRedactedExport(catalog);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<string> WriteEngineConfigurationsAsync(
        PaqetFireSettings settings,
        CancellationToken cancellationToken)
    {
        var network = networkDetector.Detect();
        var paqetProfile = new PaqetProfile(
            settings.ServerEndpoint,
            "127.0.0.1:1080",
            network.InterfaceName,
            network.InterfaceGuid,
            network.LocalIpv4Address,
            network.GatewayMacAddress,
            settings.LocalTcpFlags,
            settings.RemoteTcpFlags,
            settings.KcpMode);

        var policy = new RoutingPolicy(
            settings.RoutingMode,
            $"127.0.0.1:{XrayJsonConfigurationWriter.InboundPort}",
            settings.SelectedApplications,
            settings.UserExclusions,
            BypassLan: settings.BypassLan,
            RouteTcp: settings.RouteTcp,
            RouteUdp: settings.RouteUdp,
            RouteIpv4: settings.RouteIpv4,
            RouteIpv6: settings.RouteIpv6);
        var brokerExecutablePath = Path.Combine(AppContext.BaseDirectory, "PaqetFire.Broker.exe");
        var lockedExclusions = RoutingPolicyCompiler.CreateLockedExclusions(
            paths.PaqetExecutablePath,
            paths.ProxiFyreExecutablePath,
            [paths.XrayExecutablePath, brokerExecutablePath]);
        var routePlan = RoutingPolicyCompiler.Compile(
            policy,
            paths.PaqetExecutablePath,
            paths.ProxiFyreExecutablePath,
            [paths.XrayExecutablePath, brokerExecutablePath]);

        LanSocksShare? hotspotShare = null;
        if (settings.ShareViaHotspot)
        {
            var hotspot = hotspotDetector.TryDetect()
                ?? throw new InvalidOperationException(
                    "No active Windows hotspot network was found. Turn on Mobile hotspot in Windows Settings, connect this laptop via Ethernet or Wi-Fi first, then re-detect.");
            hotspotShare = new LanSocksShare(hotspot.Address, settings.HotspotSocksPort, settings.LanSocksUsername, settings.LanSocksPassword);
        }
        var paqetText = paqetWriter.Write(paqetProfile, settings.TransportKey);
        var xrayText = xrayWriter.Write(new XrayRoutingPolicy(
            settings.RegionalPreset,
            settings.DomainStrategy,
            settings.BypassLan,
            settings.BlockAds,
            settings.BlockQuic,
            settings.DirectBitTorrent,
            settings.ShareWithLan
                ? new LanSocksShare(
                    network.LocalIpv4Address,
                    settings.LanSocksPort,
                    settings.LanSocksUsername,
                    settings.LanSocksPassword)
                : null,
            hotspotShare,
            settings.DirectRouteDestinations));
        var proxiFyreText = proxiFyreWriter.Write(routePlan, lockedExclusions);

        await configurationStore.WriteAsync(paths.PaqetConfigurationPath, paqetText, cancellationToken)
            .ConfigureAwait(false);
        await configurationStore.WriteAsync(paths.ProxiFyreConfigurationPath, proxiFyreText, cancellationToken)
            .ConfigureAwait(false);
        await configurationStore.WriteAsync(paths.XrayConfigurationPath, xrayText, cancellationToken)
            .ConfigureAwait(false);
        return network.InterfaceName;
    }

    private async ValueTask<BrokerSnapshot> CreateSnapshotAsync(
        PaqetFireSettingsView? settings,
        CancellationToken cancellationToken)
    {
        var status = await connectionController.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        var prerequisites = snapshotMetadata.GetPrerequisites(prerequisiteInspector.Inspect);
        var missing = prerequisites.Where(item => !item.IsInstalled).Select(item => item.DisplayName).ToArray();
        var message = missing.Length > 0
            ? $"Install the missing prerequisite{(missing.Length == 1 ? string.Empty : "s")}: {string.Join(", ", missing)}."
            : settings is null
                ? "Add your server and transport key, then save the profile."
                : status.State == ConnectionState.Guarded && settings.KillSwitchEnabled
                    ? "The routing kill switch is active. Routed applications remain blocked until Paqet reconnects or the kill switch is disabled."
                    : status.Detail;

        var paqetLogs = paqetAdapter.GetRecentLogs()
            .TakeLast(60)
            .Select(entry => (
                entry.OccurredAt,
                Text: $"{entry.OccurredAt:HH:mm:ss}  {(entry.IsError ? "ERR" : "INF")}  Paqet · {entry.Message}"));
        var xrayLogs = xrayAdapter.GetRecentLogs()
            .TakeLast(20)
            .Select(entry => (
                entry.OccurredAt,
                Text: $"{entry.OccurredAt:HH:mm:ss}  INF  Xray · {entry.Message}"));
        var logs = TakeRecentLogsWithinBudget(paqetLogs
            .Concat(xrayLogs)
            .OrderBy(entry => entry.OccurredAt)
            .Select(entry => entry.Text));

        return new BrokerSnapshot(
            [status.Paqet, status.Xray, status.ProxiFyre],
            IsRouting: status.State == ConnectionState.Connected,
            IsKillSwitchEnabled: settings?.KillSwitchEnabled == true &&
                                 status.ProxiFyre.State == EngineState.Running,
            status.ObservedAt ?? DateTimeOffset.UtcNow,
            IsConfigured: settings is { HasTransportKey: true } &&
                          !string.IsNullOrWhiteSpace(settings.ServerEndpoint),
            Settings: settings ?? CreateDefaultView(),
            Prerequisites: prerequisites,
            RecentLogs: logs,
            StatusMessage: message,
            ConnectionState: status.State,
            Verification: latestVerification,
            ProfileCatalog: profileCatalog is null ? null : ProfileCatalogView.FromCatalog(profileCatalog));
    }

    private async ValueTask<PaqetFireSettings?> TryLoadSettingsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or CryptographicException or FormatException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "The saved PaqetFire settings could not be loaded.");
            return null;
        }
    }

    private ValueTask<PaqetFireSettings?> LoadActiveSettingsAsync(CancellationToken cancellationToken) =>
        profileCatalog is null
            ? TryLoadSettingsAsync(cancellationToken)
            : ValueTask.FromResult<PaqetFireSettings?>(profileCatalog.ActiveProfile.Settings);

    private async ValueTask SaveLegacySettingsMirrorAsync(
        PaqetFireSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or CryptographicException or
            FormatException or UnauthorizedAccessException)
        {
            // profiles.json is authoritative. The compatibility mirror is repaired
            // from it on the next broker startup and must not roll back the catalog.
            logger.LogWarning(exception, "The legacy active-profile settings mirror could not be updated.");
        }
    }

    private async ValueTask<ProfileCatalog> EnsureProfileCatalogAsync(CancellationToken cancellationToken)
    {
        if (profileCatalog is not null)
        {
            return profileCatalog;
        }

        profileCatalog = await profileCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (profileCatalog is not null)
        {
            return profileCatalog;
        }

        var settings = await TryLoadSettingsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Save a connection profile before managing profiles.");
        profileCatalog = ProfileCatalog.FromLegacySettings(Guid.NewGuid(), settings);
        await profileCatalogStore.SaveAsync(profileCatalog, cancellationToken).ConfigureAwait(false);
        return profileCatalog;
    }

    private static PaqetFireSettings Normalize(PaqetFireSettings settings) => settings with
    {
        ProfileName = settings.ProfileName.Trim(),
        ServerEndpoint = settings.ServerEndpoint.Trim(),
        LanSocksUsername = settings.LanSocksUsername.Trim(),
        KcpMode = settings.KcpMode.Trim().ToLowerInvariant(),
        SelectedApplications = NormalizeList(settings.SelectedApplications),
        UserExclusions = NormalizeList(settings.UserExclusions),
        DirectRouteDestinations = NormalizeList(settings.DirectRouteDestinations),
        LocalTcpFlags = NormalizeFlags(settings.LocalTcpFlags),
        RemoteTcpFlags = NormalizeFlags(settings.RemoteTcpFlags),
    };

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string> values) => values
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static IReadOnlyList<string> NormalizeFlags(IEnumerable<string> values) => values
        .Select(value => value.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private void EnsurePrerequisitesAvailable()
    {
        var missing = snapshotMetadata.GetPrerequisites(prerequisiteInspector.Inspect, forceRefresh: true)
            .Where(item => !item.IsInstalled).ToArray();
        if (missing.Length > 0)
        {
            throw new MissingPrerequisiteException(missing);
        }
    }

    private static IReadOnlyList<string> TakeRecentLogsWithinBudget(IEnumerable<string> source)
    {
        var newestFirst = new List<string>();
        var usedBytes = 0;
        foreach (var line in source.TakeLast(80).Reverse())
        {
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (usedBytes + bytes > RecentLogBudgetBytes)
            {
                continue;
            }

            newestFirst.Add(line);
            usedBytes += bytes;
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    private static PaqetFireSettingsView CreateDefaultView() => new(
        "Default",
        string.Empty,
        false,
        RoutingMode.AllApplications,
        [],
        [],
        [],
        false,
        true,
        true,
        true,
        true,
        RegionalRoutingPreset.IranDirect,
        XrayDomainStrategy.IPIfNonMatch,
        true,
        false,
        true,
        false,
        false,
        1082,
        "paqetfire",
        false,
        false,
        10808,
        "fast",
        ["PA"],
        ["PA"]);
}

public sealed record RuntimePaths(
    string PayloadRoot,
    string PaqetExecutablePath,
    string PaqetConfigurationPath,
    string XrayExecutablePath,
    string XrayConfigurationPath,
    string XrayGeoIpPath,
    string XrayGeoSitePath,
    string ProxiFyreExecutablePath,
    string ProxiFyreConfigurationPath,
    string MachineSettingsPath);

public sealed class MissingPrerequisiteException(
    IReadOnlyList<PrerequisiteStatus> prerequisites) : InvalidOperationException(
        "One or more required packet drivers are missing.")
{
    public IReadOnlyList<PrerequisiteStatus> Prerequisites { get; } = prerequisites;
}
