using PaqetFire.Broker.Configuration;
using System.Security.Cryptography;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Network;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.Routing;
using PaqetFire.Core.RoutingData;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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
    EngineFirewallRules engineFirewallRules,
    IConnectionVerifier connectionVerifier,
    EngineSocksCredentials engineCredentials,
    TunnelHealthTracker tunnelHealth,
    RuntimePaths paths,
    ILogger<PaqetFireRuntime> logger,
    IBrokerStateStore? stateStore = null,
    ConnectionProgressTracker? progress = null,
    IIpv6CapabilityProbe? ipv6Probe = null,
    TimeProvider? timeProvider = null,
    RoutingDataService? routingData = null,
    PsiphonProcessAdapter? psiphonAdapter = null) : IPaqetFireRuntime
{
    private const int RecentLogBudgetBytes = 24 * 1024;
    private static readonly TimeSpan SystemResolverTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Ipv6ProbeWaitForHealthyTunnel = TimeSpan.FromSeconds(30);
    private static readonly RouteRecoveryOptions RecoveryOptions = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SnapshotMetadataCache snapshotMetadata = new();
    private readonly IBrokerStateStore stateStore = stateStore ?? new InMemoryBrokerStateStore();
    private readonly ConnectionProgressTracker progress = progress ?? new ConnectionProgressTracker(timeProvider);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly RouteRecoveryPolicy recovery = new(RecoveryOptions);
    private readonly PsiphonJsonConfigurationWriter psiphonWriter = new();
    private readonly Lock stateSync = new();
    private readonly SemaphoreSlim stateWriteGate = new(1, 1);
    private readonly Lock operationSync = new();
    private readonly HashSet<CancellationTokenSource> operations = [];
    private ConnectionVerificationReport? latestVerification;
    private ProfileCatalog? profileCatalog;
    private BrokerRuntimeState brokerState = new();
    private volatile bool desiredConnected;
    private ActiveRoute? activeRoute;
    private NetworkFingerprint? lastFingerprint;
    private volatile string? backgroundNotice;
    private long nextSupervisionTicks;

    public DateTimeOffset? NextSupervisionDue => Interlocked.Read(ref nextSupervisionTicks) is var ticks and > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero)
        : null;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        brokerState = stateStore.Load();
        desiredConnected = brokerState.DesiredConnected;

        // Paqet, Xray, and Psiphon are bound to the broker's kill-on-close job, so they
        // rarely survive it. ProxiFyre is not: it may still be blocking routed applications
        // for the kill switch or a route the user left connected, and is replaced only when
        // the broker starts its own instance.
        var orphans = await OrphanedEngineCleanup.TerminateAsync(
                [paths.PaqetExecutablePath, paths.XrayExecutablePath, paths.PsiphonExecutablePath],
                cancellationToken)
            .ConfigureAwait(false);
        if (orphans.Count > 0)
        {
            logger.LogWarning("Stopped engines left by a previous broker process: {Engines}.", string.Join(", ", orphans));
        }

        FlushDnsCache();
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
            // Migrate catalogs with per-profile authentication using the current
            // application's credentials, so later profile switches cannot rotate them.
            profileCatalog = profileCatalog.WithSharingCredentials(settings.LanSocksUsername, settings.LanSocksPassword);
            await profileCatalogStore.SaveAsync(profileCatalog, cancellationToken).ConfigureAwait(false);
            await SaveLegacySettingsMirrorAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        await snapshotMetadata.UpdateSettingsAsync(settings).ConfigureAwait(false);

        if (desiredConnected &&
            (settings is null || profileCatalog?.ActiveProfileId != brokerState.DesiredProfileId ||
             PaqetFireSettingsValidator.Validate(Normalize(settings)).Count > 0))
        {
            logger.LogInformation("The route left connected belongs to a profile that changed; it is not restored.");
            await SetDesiredAsync(false).ConfigureAwait(false);
        }

        if (desiredConnected)
        {
            logger.LogInformation("The route was connected when the broker stopped; it will be restored.");
        }

        if (settings?.KillSwitchEnabled != true && !desiredConnected)
        {
            await TerminateOrphanedCaptureAsync().ConfigureAwait(false);
        }

        _ = engineFirewallRules.EnsureRepairedAsync();
        if (settings?.KillSwitchEnabled != true ||
            !File.Exists(paths.ProxiFyreConfigurationPath))
        {
            return;
        }

        try
        {
            // The guard keeps ProxiFyre running across later connects; refresh its
            // configuration so it carries the engine credentials Xray will expect.
            await configurationStore.WriteAsync(
                    paths.ProxiFyreConfigurationPath,
                    CreateProxiFyreConfiguration(Normalize(settings)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The ProxiFyre configuration could not be refreshed before restoring the kill switch.");
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
        // Registered before waiting so a Disconnect can cancel the connect half. Everything
        // up to and including persistence uses only the caller's token: a Cancel must never
        // be reported as "saved" while the edits were dropped.
        var operation = BeginOperation(cancellationToken);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            EndOperation(operation);
            throw;
        }

        var persisted = false;
        PaqetFireSettings? normalized = null;
        try
        {
            var token = cancellationToken;
            var existing = await LoadActiveSettingsAsync(token).ConfigureAwait(false);
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

            var current = await connectionController.GetStatusAsync(token)
                .ConfigureAwait(false);
            var reconnect = connectAfterSave || current.State == ConnectionState.Connected;
            if (reconnect)
            {
                EnsurePrerequisitesAvailable();
                progress.Begin(ConnectionStage.Preparing);
            }

            var applyEngineConfiguration = reconnect || settings.KillSwitchEnabled || current.State != ConnectionState.Disconnected;
            if (applyEngineConfiguration)
            {
                if (reconnect)
                {
                    progress.Advance(ConnectionStage.VerifyingPayload);
                }

                var inspection = await payloadInspector.InspectAsync(token).ConfigureAwait(false);
                if (inspection.State != PayloadInspectionState.Ready)
                    throw new InvalidOperationException(inspection.Detail);
            }

            normalized = Normalize(settings);
            var savedSettings = normalized;
            latestVerification = null;
            if (reconnect)
            {
                // Only a save that starts the route restarts recovery; a plain save must not
                // turn a pending backoff into an immediate reconnect.
                recovery.Reset();
                SetNextSupervision(null);
            }

            var catalogToSave = profileCatalog is null
                ? ProfileCatalog.Create(Guid.NewGuid(), normalized)
                : profileCatalog.Apply(new ProfileCatalogChange.Update(
                    profileCatalog.ActiveProfileId,
                    normalized));
            catalogToSave = catalogToSave.WithSharingCredentials(normalized.LanSocksUsername, normalized.LanSocksPassword);
            EngineConfigurationSet? configuration = null;
            var activationError = await ConfigurationActivation.ApplyAsync(
                connectionController,
                async () =>
                {
                    // Persisting is not cancelled by a Disconnect; only the activation is.
                    if (applyEngineConfiguration)
                    {
                        configuration = await BuildEngineConfigurationsAsync(
                                savedSettings, requireHotspot: true, ServerResolution.Fresh, null, cancellationToken)
                            .ConfigureAwait(false);
                        await WriteEngineConfigurationsAsync(configuration, cancellationToken).ConfigureAwait(false);
                    }

                    await profileCatalogStore.SaveAsync(catalogToSave, cancellationToken).ConfigureAwait(false);
                    profileCatalog = catalogToSave;
                    persisted = true;
                    await snapshotMetadata.UpdateSettingsAsync(savedSettings).ConfigureAwait(false);
                    await SaveLegacySettingsMirrorAsync(savedSettings, cancellationToken).ConfigureAwait(false);
                },
                current.State != ConnectionState.Disconnected,
                normalized.KillSwitchEnabled,
                reconnect,
                operation.Token,
                stage => progress.Advance(stage)).ConfigureAwait(false);
            if (activationError is not null && reconnect &&
                await RewriteForBundledDataAsync(activationError, savedSettings, configuration, explicitRequest: true, cancellationToken)
                    .ConfigureAwait(false) is { } bundled)
            {
                configuration = bundled;
                activationError = await ConnectAgainAsync(normalized.KillSwitchEnabled, operation.Token).ConfigureAwait(false);
            }

            FlushDnsCache();
            activeRoute = null;
            if (reconnect)
            {
                await CompleteRouteChangeAsync(normalized, configuration, activationError is null).ConfigureAwait(false);
            }

            var operationWarning = activationError is null ? null
                : $"The profile was saved, but the route could not be activated. {activationError.Message}";
            if (activationError is not null)
                logger.LogWarning(activationError, "The saved profile could not be activated.");

            logger.LogInformation(
                "Applied PaqetFire profile {ProfileName} on adapter {InterfaceName}.",
                normalized.ProfileName,
                configuration?.InterfaceName);
            var snapshot = await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(normalized), token).ConfigureAwait(false);
            return snapshot with { OperationWarning = operationWarning ?? snapshot.OperationWarning };
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            progress.Clear();
            if (!persisted || normalized is null)
            {
                // Persistence runs on the caller's token, so this is defensive: unsaved edits
                // must never be reported as saved.
                throw new BrokerOperationCancelledException("The save was cancelled before it was stored.");
            }

            // The edits are stored and the snapshot shows them; only connecting was cancelled.
            FlushDnsCache();
            var snapshot = await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(normalized), CancellationToken.None)
                .ConfigureAwait(false);
            return snapshot with { OperationWarning = "The profile was saved, but connecting was cancelled." };
        }
        catch
        {
            progress.Clear();
            throw;
        }
        finally
        {
            EndOperation(operation);
            _gate.Release();
        }
    }

    public async ValueTask<BrokerSnapshot> ConnectAsync(CancellationToken cancellationToken)
    {
        // Registered before waiting, so a Disconnect sent while this request queues or runs cancels it.
        var operation = BeginOperation(cancellationToken);
        try
        {
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            EndOperation(operation);
            return await CreateCancelledSnapshotAsync().ConfigureAwait(false);
        }
        catch
        {
            EndOperation(operation);
            throw;
        }

        try
        {
            recovery.Reset();
            SetNextSupervision(null);
            var settings = await ConnectCoreAsync(explicitRequest: true, attempt: 1, operation.Token).ConfigureAwait(false);
            return await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(settings), operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("The connection attempt was cancelled.");
            return await CreateCancelledSnapshotAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An explicit connect that failed is reported to the user; it is not retried
            // in the background behind their back.
            await SetDesiredAsync(false).ConfigureAwait(false);
            if (profileCatalog?.ActiveProfile.Settings.KillSwitchEnabled != true)
            {
                await TerminateOrphanedCaptureAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            EndOperation(operation);
            _gate.Release();
        }
    }

    public async ValueTask<BrokerSnapshot> DisconnectAsync(CancellationToken cancellationToken)
    {
        // Stop recovery and any running connect before queueing behind them.
        desiredConnected = false;
        CancelOperations();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            latestVerification = null;
            recovery.Reset();
            SetNextSupervision(null);
            progress.Clear();
            backgroundNotice = null;
            await SetDesiredAsync(false).ConfigureAwait(false);
            var settings = await snapshotMetadata.GetSettingsAsync(TryLoadSettingsAsync, cancellationToken)
                .ConfigureAwait(false);
            await ReleaseRouteAsync(settings?.KillSwitchEnabled == true, cancellationToken).ConfigureAwait(false);
            FlushDnsCache();
            return await CreateSnapshotAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StopEnginesAsync(CancellationToken cancellationToken)
    {
        // Service shutdown keeps the user's choice: a restarted broker reconnects.
        CancelOperations();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            latestVerification = null;
            progress.Clear();
            activeRoute = null;
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
            latestVerification = await connectionVerifier.VerifyAsync(
                    status,
                    Normalize(settings),
                    cancellationToken,
                    DetectNetworkResolvers(settings))
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
        // Supervision re-evaluates after this; an overdue time would only make the monitor spin.
        SetNextSupervision(null);
        try
        {
            if (action.Kind == ProfileActionKind.FactoryReset)
            {
                // Reset deliberately releases the route and guard. It must also work
                // when no connection has been configured yet.
                await connectionController.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                activeRoute = null;
                recovery.Reset();
                backgroundNotice = null;
                await SetDesiredAsync(false).ConfigureAwait(false);
                var defaults = new PaqetFireSettings();
                var emptyCatalog = ProfileCatalog.Create(Guid.NewGuid(), defaults);
                await profileCatalogStore.SaveAsync(emptyCatalog, cancellationToken).ConfigureAwait(false);
                profileCatalog = emptyCatalog;
                latestVerification = null;
                await snapshotMetadata.UpdateSettingsAsync(defaults).ConfigureAwait(false);
                await settingsStore.SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
                if (routingData is not null)
                {
                    // Automatic updates are an installation default; verified data already downloaded stays.
                    await routingData.ApplyAsync(RoutingDataAction.EnableAutomaticUpdates, cancellationToken).ConfigureAwait(false);
                }

                foreach (var path in new[] { paths.PaqetConfigurationPath, paths.XrayConfigurationPath, paths.ProxiFyreConfigurationPath })
                    await configurationStore.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                return await CreateSnapshotAsync(PaqetFireSettingsView.FromSettings(defaults), cancellationToken).ConfigureAwait(false);
            }

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
                case ProfileActionKind.Create:
                    // A new profile has no server or key yet. Selecting it would restart a
                    // running route with it, so it becomes active only while disconnected.
                    updated = catalog.Apply(new ProfileCatalogChange.Add(
                        Guid.NewGuid(),
                        new PaqetFireSettings
                        {
                            ProfileName = action.Name ?? throw new InvalidOperationException("Enter a profile name."),
                        },
                        MakeActive: currentStatus.State == ConnectionState.Disconnected));
                    break;
                case ProfileActionKind.Delete:
                    var deletedId = action.ProfileId ?? throw new InvalidOperationException("Choose a profile to delete.");
                    // Keep the editor usable after deleting the final saved connection.
                    updated = catalog.Profiles.Count == 1 && deletedId == catalog.ActiveProfileId
                        ? ProfileCatalog.Create(Guid.NewGuid(), new PaqetFireSettings())
                        : catalog.Apply(new ProfileCatalogChange.Delete(deletedId));
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
                case ProfileActionKind.Import:
                    if (currentStatus.State != ConnectionState.Disconnected)
                    {
                        throw new InvalidOperationException("Disconnect the route before replacing profiles from an import.");
                    }
                    updated = ProfileCatalogJson.ReadImport(
                        action.Payload ?? throw new InvalidOperationException("The profile import is empty."));
                    break;
                case ProfileActionKind.ImportMerge:
                    // Adds beside the existing profiles; the active profile and the running
                    // route are untouched, so this is allowed while connected.
                    updated = catalog.Merge(
                        ProfileCatalogJson.ReadImport(
                            action.Payload ?? throw new InvalidOperationException("The profile import is empty.")),
                        Guid.NewGuid);
                    break;
                default:
                    throw new InvalidOperationException("The profile action is not supported.");
            }

            var applicationSettings = catalog.ActiveProfile.Settings;
            updated = updated.WithSharingCredentials(applicationSettings.LanSocksUsername, applicationSettings.LanSocksPassword);
            var activeChanged = updated.ActiveProfileId != catalog.ActiveProfileId ||
                                action.Kind is ProfileActionKind.ImportRedacted or ProfileActionKind.Import;
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
            var reconnect = currentStatus.State != ConnectionState.Disconnected;
            // Selecting a profile for editing is allowed before it can connect.
            // Validate only when this action would actually restart the route.
            if (reconnect)
            {
                var errors = PaqetFireSettingsValidator.Validate(selected);
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Disconnect the route before selecting or deleting a profile that needs setup.");
                }
            }

            latestVerification = null;
            string? activationError = null;
            if (reconnect)
            {
                EnsurePrerequisitesAvailable();
                recovery.Reset();
                progress.Begin(ConnectionStage.Preparing);
                EngineConfigurationSet? configuration = null;
                Exception? error;
                try
                {
                    error = await ConfigurationActivation.ApplyAsync(
                        connectionController,
                        async () =>
                        {
                            configuration = await BuildEngineConfigurationsAsync(
                                    selected, requireHotspot: true, ServerResolution.Fresh, null, cancellationToken)
                                .ConfigureAwait(false);
                            await WriteEngineConfigurationsAsync(configuration, cancellationToken).ConfigureAwait(false);
                            await profileCatalogStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                            profileCatalog = updated;
                            await snapshotMetadata.UpdateSettingsAsync(selected).ConfigureAwait(false);
                            await SaveLegacySettingsMirrorAsync(selected, cancellationToken).ConfigureAwait(false);
                        },
                        stopCurrentRoute: true,
                        enableGuard: selected.KillSwitchEnabled,
                        reconnect: true,
                        cancellationToken,
                        stage => progress.Advance(stage)).ConfigureAwait(false);
                    if (error is not null &&
                        await RewriteForBundledDataAsync(error, selected, configuration, explicitRequest: true, cancellationToken)
                            .ConfigureAwait(false) is { } bundled)
                    {
                        configuration = bundled;
                        error = await ConnectAgainAsync(selected.KillSwitchEnabled, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    progress.Clear();
                    throw;
                }

                activationError = error?.Message;
                activeRoute = null;
                FlushDnsCache();
                await CompleteRouteChangeAsync(selected, configuration, error is null).ConfigureAwait(false);
            }
            else
            {
                await profileCatalogStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                profileCatalog = updated;
                await snapshotMetadata.UpdateSettingsAsync(selected).ConfigureAwait(false);
                await SaveLegacySettingsMirrorAsync(selected, cancellationToken).ConfigureAwait(false);
                if (desiredConnected && DesiredProfileId != updated.ActiveProfileId)
                {
                    // A route waiting to be restored belonged to the profile the user just
                    // left; selecting another one while disconnected ends that wish.
                    recovery.Reset();
                    await SetDesiredAsync(false).ConfigureAwait(false);
                    await ReleaseOrphanedCaptureWithoutKillSwitchAsync(selected.KillSwitchEnabled).ConfigureAwait(false);
                }
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
            return ProfileCatalogJson.WritePortableExport(catalog);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RestartTransportAsync(CancellationToken cancellationToken)
    {
        var operation = BeginOperation(cancellationToken);
        if (!await WaitForGateAsync(operation, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var status = await connectionController.GetStatusAsync(operation.Token).ConfigureAwait(false);
            var settings = await LoadActiveSettingsAsync(operation.Token).ConfigureAwait(false);
            if (status.State != ConnectionState.Connected || settings is null)
            {
                return;
            }

            progress.Recovering(Math.Max(1, tunnelHealth.Report.RecoveryAttempts));
            // The adapter address, gateway MAC, or server address may have changed under
            // the stalled carrier. Xray keeps running unless its own inputs changed, so
            // FakeDNS placeholder addresses stay valid.
            await RestartChangedEnginesAsync(Normalize(settings), restartTransport: true, operation.Token)
                .ConfigureAwait(false);
            logger.LogInformation("The Paqet transport was restarted by the tunnel watchdog.");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A user Disconnect superseded the restart; it releases the route itself.
        }
        finally
        {
            progress.ClearIf(ConnectionStage.RecoveringTransport);
            EndOperation(operation);
            _gate.Release();
        }
    }

    public async ValueTask<bool> SuperviseAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        CancellationTokenSource? operation = null;
        try
        {
            var status = await connectionController.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var settings = await LoadActiveSettingsAsync(cancellationToken).ConfigureAwait(false);
            var killSwitch = settings?.KillSwitchEnabled == true;
            // The wish to stay connected belongs to the profile it was made for; another
            // profile that became active meanwhile is never connected in the background.
            var routeWanted = desiredConnected && settings is not null &&
                              profileCatalog?.ActiveProfileId == DesiredProfileId;
            var engines = status.Engines;
            var now = clock.GetUtcNow();
            if (tunnelHealth.Report.LastSuccessAt is { } healthyAt &&
                recovery.LastAttemptAt is { } attemptAt &&
                healthyAt > attemptAt &&
                recovery.Attempts > 0)
            {
                // Traffic passed after the last attempt: the episode is over.
                recovery.Reset();
            }

            var decision = recovery.Evaluate(
                now,
                routeWanted,
                status.State,
                killSwitch,
                engines);
            SetNextSupervision(decision.RetryAt);
            switch (decision.Action)
            {
                case RouteRecoveryAction.None when decision.IsWaiting:
                    progress.Recovering(decision.Attempt);
                    return true;

                case RouteRecoveryAction.None:
                    progress.ClearIf(ConnectionStage.RecoveringTransport);
                    return true;

                case RouteRecoveryAction.RestartTransport:
                    operation = BeginOperation(cancellationToken);
                    // A Disconnect that ran before the operation was registered found
                    // nothing to cancel; it has cleared the wish by now.
                    if (!desiredConnected)
                    {
                        return true;
                    }

                    recovery.RecordAttempt(now);
                    SetNextSupervision(recovery.NextAttemptAt);
                    progress.Recovering(decision.Attempt);
                    logger.LogWarning(
                        "Paqet stopped while the route was connected; restarting it (attempt {Attempt} of {Limit}).",
                        decision.Attempt,
                        RecoveryOptions.MaximumConsecutiveFailures);
                    try
                    {
                        await RestartChangedEnginesAsync(Normalize(settings!), restartTransport: true, operation.Token)
                            .ConfigureAwait(false);
                        progress.Advance(ConnectionStage.ProbingTunnel);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Paqet could not be restarted.");
                    }

                    return true;

                case RouteRecoveryAction.Reconnect:
                    operation = BeginOperation(cancellationToken);
                    if (!desiredConnected)
                    {
                        return true;
                    }

                    recovery.RecordAttempt(now);
                    SetNextSupervision(recovery.NextAttemptAt);
                    logger.LogInformation(
                        "Restoring the route the user left connected (attempt {Attempt} of {Limit}).",
                        decision.Attempt,
                        RecoveryOptions.MaximumConsecutiveFailures);
                    try
                    {
                        await ConnectCoreAsync(explicitRequest: false, decision.Attempt, operation.Token).ConfigureAwait(false);
                    }
                    catch (ConfigurationValidationException exception)
                    {
                        // A profile that cannot connect will not fix itself by retrying.
                        logger.LogWarning("The saved profile is incomplete; the route is not restored: {Errors}",
                            string.Join(" ", exception.Errors));
                        await SetDesiredAsync(false).ConfigureAwait(false);
                        await ReleaseOrphanedCaptureWithoutKillSwitchAsync(killSwitch).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "The route could not be restored.");
                        await ReleaseOrphanedCaptureWithoutKillSwitchAsync(killSwitch).ConfigureAwait(false);
                    }

                    return true;

                case RouteRecoveryAction.SafeDisconnect:
                    progress.Clear();
                    activeRoute = null;
                    if (recovery.IsSuspended)
                    {
                        backgroundNotice =
                            $"PaqetFire stopped retrying after {RecoveryOptions.MaximumConsecutiveFailures} failed attempts. " +
                            "It tries again when the network changes; you can also connect again now.";
                        logger.LogWarning(
                            "The route failed {Attempts} recovery attempts; restoring the configured safe state.",
                            recovery.Attempts);
                    }
                    else
                    {
                        logger.LogWarning("The engine chain is unhealthy; restoring the configured safe state.");
                    }

                    await ReleaseRouteAsync(killSwitch, cancellationToken).ConfigureAwait(false);
                    FlushDnsCache();
                    return true;
            }

            return true;
        }
        catch (OperationCanceledException) when (operation?.IsCancellationRequested == true &&
                                                  !cancellationToken.IsCancellationRequested)
        {
            // A user Disconnect cancelled this recovery; it releases the route itself.
            progress.Clear();
            return true;
        }
        finally
        {
            EndOperation(operation);
            _gate.Release();
        }
    }

    public async ValueTask HandleNetworkChangeAsync(bool resumed, CancellationToken cancellationToken)
    {
        var operation = BeginOperation(cancellationToken);
        if (!await WaitForGateAsync(operation, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var token = operation.Token;
            var settings = await LoadActiveSettingsAsync(token).ConfigureAwait(false);
            var fingerprint = settings is null ? null : TryDetectFingerprint(Normalize(settings));
            var changed = fingerprint != lastFingerprint;
            if (!changed && !resumed)
            {
                return;
            }

            // A new network or a resume makes recovery worthwhile again.
            tunnelHealth.ResetBackoff();
            recovery.Reset();
            SetNextSupervision(null);
            var status = await connectionController.GetStatusAsync(token).ConfigureAwait(false);
            if (status.State != ConnectionState.Connected || settings is null || fingerprint is null)
            {
                // Without a usable adapter the carrier cannot be rebuilt yet; the next change
                // retries. A route that is down is reconnected by supervision if still wanted.
                lastFingerprint = fingerprint;
                return;
            }

            logger.LogInformation(resumed
                ? "Windows resumed; restarting the Paqet transport."
                : "The network changed; checking which engines need its new details.");
            progress.Recovering(1);
            await RestartChangedEnginesAsync(Normalize(settings), restartTransport: resumed, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A user Disconnect superseded the adjustment.
        }
        finally
        {
            progress.ClearIf(ConnectionStage.RecoveringTransport);
            EndOperation(operation);
            _gate.Release();
        }
    }

    public async ValueTask<string> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadActiveSettingsAsync(cancellationToken).ConfigureAwait(false);
            var status = await connectionController.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var payload = await payloadInspector.InspectAsync(cancellationToken).ConfigureAwait(false);
            DiagnosticNetwork network;
            try
            {
                var detected = networkDetector.Detect(settings?.NetworkInterfaceGuid, KnownServerAddress(settings?.ServerEndpoint));
                network = new DiagnosticNetwork(detected.InterfaceName, detected.LocalIpv4Address,
                    !string.IsNullOrEmpty(detected.GatewayMacAddress), detected.DnsServers);
            }
            catch (InvalidOperationException exception)
            {
                network = new DiagnosticNetwork(string.Empty, string.Empty, false, [], exception.Message);
            }

            var logs = paqetAdapter.GetRecentLogs()
                .Select(entry => (entry.OccurredAt, Text: $"{entry.OccurredAt:HH:mm:ss}  {(entry.IsError ? "ERR" : "INF")}  Paqet · {entry.Message}"))
                .Concat(xrayAdapter.GetRecentLogs()
                    .Select(entry => (entry.OccurredAt, Text: $"{entry.OccurredAt:HH:mm:ss}  INF  Xray · {entry.Message}")))
                .Concat((psiphonAdapter?.GetRecentLogs() ?? [])
                    .Select(entry => (entry.OccurredAt, Text: $"{entry.OccurredAt:HH:mm:ss}  INF  Psiphon · {entry.Message}")))
                .OrderBy(entry => entry.OccurredAt)
                .Select(entry => entry.Text)
                .TakeLast(120)
                .ToArray();

            List<string> notes =
            [
                $"Route wanted: {(desiredConnected ? "yes" : "no")} · recovery attempts {recovery.Attempts}{(recovery.IsSuspended ? " (stopped retrying)" : string.Empty)}",
            ];
            if (settings is not null && Ipv6SupportFor(settings.ServerEndpoint) is { } ipv6)
            {
                notes.Add($"Server IPv6: {(ipv6 ? "reachable" : "unreachable; AAAA answers are skipped")}");
            }

            if (backgroundNotice is { } notice)
            {
                notes.Add(notice);
            }

            if (activeRoute?.RoutingData is { } runningData)
            {
                notes.Add($"Running Xray data: {(runningData.IsUpdated ? runningData.Version : "bundled")} · malware blocking {(activeRoute.BlockMalware ? "active" : "inactive")}");
            }

            return DiagnosticBundle.Build(new DiagnosticBundleInput(
                    settings is null ? null : Normalize(settings),
                    profileCatalog?.Profiles.Count ?? 0,
                    status,
                    status.State == ConnectionState.Connected ? tunnelHealth.Report : TunnelHealthReport.Unknown,
                    $"{payload.State} · {payload.Detail}",
                    snapshotMetadata.GetPrerequisites(prerequisiteInspector.Inspect),
                    network,
                    latestVerification,
                    logs,
                    [settings?.TransportKey, settings?.LanSocksPassword, engineCredentials.Password],
                    notes,
                    routingData?.Status),
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts the chain for the active profile. Must be called under the gate.</summary>
    /// <param name="explicitRequest">
    /// A user's own connect keeps hard errors such as a missing hotspot; a background
    /// restore degrades instead, so the main route still comes back.
    /// </param>
    private async ValueTask<PaqetFireSettings> ConnectCoreAsync(
        bool explicitRequest,
        int attempt,
        CancellationToken cancellationToken)
    {
        progress.Begin(ConnectionStage.Preparing, attempt);
        try
        {
            var settings = await LoadActiveSettingsAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Save a valid Paqet profile before connecting.");
            settings = Normalize(settings);
            var errors = PaqetFireSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new ConfigurationValidationException(errors);
            }

            var settingsKey = CreateSettingsKey(settings);
            var status = await connectionController.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.State == ConnectionState.Connected && activeRoute?.SettingsKey == settingsKey)
            {
                // A repeated Connect must not tear down a working tunnel.
                progress.Clear();
                await SetDesiredAsync(true, profileCatalog?.ActiveProfileId).ConfigureAwait(false);
                return settings;
            }

            EnsurePrerequisitesAvailable();

            progress.Advance(ConnectionStage.VerifyingPayload);
            var inspection = await payloadInspector.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (inspection.State != PayloadInspectionState.Ready)
            {
                throw new InvalidOperationException(inspection.Detail);
            }

            latestVerification = null;
            var configuration = await BuildEngineConfigurationsAsync(
                    settings,
                    requireHotspot: explicitRequest,
                    ServerResolution.Fresh,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteEngineConfigurationsAsync(configuration, cancellationToken).ConfigureAwait(false);
            backgroundNotice = configuration.Notice;

            if (status.State == ConnectionState.Connected)
            {
                // Running with other settings: every engine must restart to read the new files.
                if (settings.KillSwitchEnabled)
                {
                    await connectionController.GuardAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await connectionController.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (settings.KillSwitchEnabled && status.State != ConnectionState.Guarded)
            {
                await connectionController.GuardAsync(cancellationToken).ConfigureAwait(false);
            }

            activeRoute = null;
            try
            {
                await connectionController.ConnectAsync(stage => progress.Advance(stage), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The controller rolled back what it started; the cancelling Disconnect
                // applies the configured safe state next.
                throw;
            }
            catch (Exception error)
            {
                if (await RewriteForBundledDataAsync(error, settings, configuration, explicitRequest, cancellationToken)
                        .ConfigureAwait(false) is not { } bundled)
                {
                    if (settings.KillSwitchEnabled)
                    {
                        await connectionController.GuardAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    throw;
                }

                // The retry restores the kill switch guard itself if it fails as well.
                if (await ConnectAgainAsync(settings.KillSwitchEnabled, cancellationToken).ConfigureAwait(false) is { } retryError)
                {
                    ExceptionDispatchInfo.Capture(retryError).Throw();
                }

                configuration = bundled;
            }

            activeRoute = ActiveRoute.From(settingsKey, configuration);
            lastFingerprint = configuration.Fingerprint;
            FlushDnsCache();
            await snapshotMetadata.UpdateSettingsAsync(settings).ConfigureAwait(false);
            await SetDesiredAsync(true, profileCatalog?.ActiveProfileId).ConfigureAwait(false);
            StartTunnelProbe(newSession: true);
            StartIpv6Probe(settings);
            return settings;
        }
        catch
        {
            progress.Clear();
            throw;
        }
    }

    /// <summary>Records the outcome of a save or profile switch that restarted the route.</summary>
    private async ValueTask CompleteRouteChangeAsync(
        PaqetFireSettings settings,
        EngineConfigurationSet? configuration,
        bool succeeded)
    {
        if (!succeeded || configuration is null)
        {
            progress.Clear();
            await SetDesiredAsync(false).ConfigureAwait(false);
            return;
        }

        activeRoute = ActiveRoute.From(CreateSettingsKey(settings), configuration);
        lastFingerprint = configuration.Fingerprint;
        backgroundNotice = configuration.Notice;
        await SetDesiredAsync(true, profileCatalog?.ActiveProfileId).ConfigureAwait(false);
        StartTunnelProbe(newSession: true);
        StartIpv6Probe(settings);
    }

    /// <summary>
    /// Rebuilds the engine configurations from the current network and restarts Paqet when
    /// asked or when its inputs changed, plus Xray when its own inputs (LAN address,
    /// resolvers, hotspot inbound) changed. ProxiFyre is never restarted here.
    /// </summary>
    private async ValueTask RestartChangedEnginesAsync(
        PaqetFireSettings settings,
        bool restartTransport,
        CancellationToken cancellationToken)
    {
        // Keep the running session's IPv6 DNS choice and routing data so a capability
        // learned or a data update installed meanwhile does not force an Xray restart;
        // they apply from the next connect.
        var configuration = await BuildEngineConfigurationsAsync(
                settings,
                requireHotspot: false,
                ServerResolution.Cached,
                activeRoute?.ResolveIpv6,
                cancellationToken,
                activeRoute?.RoutingData)
            .ConfigureAwait(false);
        var restartPolicy = activeRoute is null ||
                            !string.Equals(configuration.XrayText, activeRoute.XrayText, StringComparison.Ordinal);
        if (restartPolicy && activeRoute?.RoutingData is not null)
        {
            // Xray restarts anyway, so it may as well load the newest verified data.
            configuration = await BuildEngineConfigurationsAsync(
                    settings,
                    requireHotspot: false,
                    ServerResolution.Cached,
                    activeRoute.ResolveIpv6,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        restartTransport |= restartPolicy ||
                            !string.Equals(configuration.PaqetText, activeRoute?.PaqetText, StringComparison.Ordinal);
        lastFingerprint = configuration.Fingerprint;
        if (!restartTransport)
        {
            logger.LogInformation("The network change does not affect the running engines.");
            return;
        }

        await WriteEngineConfigurationsAsync(configuration, cancellationToken).ConfigureAwait(false);
        backgroundNotice = configuration.Notice;
        try
        {
            await connectionController.RestartTransportAsync(restartPolicy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (restartPolicy && error is not OperationCanceledException)
        {
            if (await RewriteForBundledDataAsync(error, settings, configuration, explicitRequest: false, cancellationToken)
                    .ConfigureAwait(false) is not { } bundled)
            {
                throw;
            }

            configuration = bundled;
            await connectionController.RestartTransportAsync(includePolicy: true, cancellationToken).ConfigureAwait(false);
        }

        if (restartPolicy)
        {
            // FakeDNS placeholders belonged to the previous Xray process.
            FlushDnsCache();
            logger.LogInformation("Restarted Paqet and Xray for the changed network.");
        }

        activeRoute = ActiveRoute.From(activeRoute?.SettingsKey ?? CreateSettingsKey(settings), configuration);
        StartTunnelProbe();
    }

    /// <summary>
    /// When Xray could not start with downloaded routing data (its files failed verification
    /// or loading them took too long), records that and rewrites the configurations for the
    /// bundled data, without the malware rules it cannot serve. Returns null for any other failure.
    /// </summary>
    private async ValueTask<EngineConfigurationSet?> RewriteForBundledDataAsync(
        Exception error,
        PaqetFireSettings settings,
        EngineConfigurationSet? configuration,
        bool explicitRequest,
        CancellationToken cancellationToken)
    {
        if (configuration is not { RoutingData.IsUpdated: true } ||
            RoutingDataService.FindCause<RoutingDataUnavailableException>(error) is not { } cause)
        {
            return null;
        }

        if (routingData is not null)
        {
            await routingData.ReportLaunchFailureAsync(configuration.RoutingData, cause).ConfigureAwait(false);
        }

        logger.LogWarning(cause, "Xray could not start with the downloaded routing data; retrying with the bundled data.");
        var bundled = await BuildEngineConfigurationsAsync(
                Normalize(settings),
                requireHotspot: explicitRequest,
                ServerResolution.Cached,
                configuration.ResolveIpv6,
                cancellationToken,
                RoutingDataSet.Bundled)
            .ConfigureAwait(false);
        await WriteEngineConfigurationsAsync(bundled, cancellationToken).ConfigureAwait(false);
        return bundled;
    }

    /// <returns>Null when the route started; otherwise the failure, with the kill switch guard restored.</returns>
    private async ValueTask<Exception?> ConnectAgainAsync(bool killSwitchEnabled, CancellationToken cancellationToken)
    {
        try
        {
            await connectionController.ConnectAsync(stage => progress.Advance(stage), cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (killSwitchEnabled)
            {
                try
                {
                    await connectionController.GuardAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception guardError) when (guardError is not OperationCanceledException)
                {
                    logger.LogError(guardError, "The routing kill switch could not be restored.");
                }
            }

            return error;
        }
    }

    private async ValueTask ReleaseRouteAsync(bool killSwitchEnabled, CancellationToken cancellationToken)
    {
        activeRoute = null;
        if (killSwitchEnabled)
        {
            await connectionController.GuardAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await connectionController.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await TerminateOrphanedCaptureAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops a ProxiFyre kept from an earlier broker for a route that is no longer wanted;
    /// without the kill switch it would otherwise keep routed applications blocked.
    /// </summary>
    private async ValueTask TerminateOrphanedCaptureAsync()
    {
        // The cleanup matches by installed path, so it would also kill the adapter's own
        // instance; only act when the broker does not run ProxiFyre itself.
        var status = await connectionController.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (status.ProxiFyre.State is EngineState.Running or EngineState.Starting or EngineState.Stopping)
        {
            return;
        }

        if (await OrphanedEngineCleanup.TerminateAsync([paths.ProxiFyreExecutablePath], CancellationToken.None)
                .ConfigureAwait(false) is { Count: > 0 })
        {
            logger.LogWarning("Stopped a ProxiFyre instance left by a previous broker process.");
        }
    }

    /// <summary>
    /// A background reconnect that failed without the kill switch leaves nothing that should
    /// block applications, including a ProxiFyre kept from an earlier broker for that reconnect.
    /// </summary>
    private async ValueTask ReleaseOrphanedCaptureWithoutKillSwitchAsync(bool killSwitchEnabled)
    {
        if (!killSwitchEnabled)
        {
            await TerminateOrphanedCaptureAsync().ConfigureAwait(false);
        }
    }

    /// <param name="newSession">A new route starts with fresh health; a restart keeps the watchdog's backoff.</param>
    private void StartTunnelProbe(bool newSession = false)
    {
        if (newSession)
        {
            tunnelHealth.Reset();
        }

        progress.Advance(ConnectionStage.ProbingTunnel);
        tunnelHealth.RequestProbe();
    }

    private void StartIpv6Probe(PaqetFireSettings settings)
    {
        if (ipv6Probe is null || !settings.RouteIpv6)
        {
            return;
        }

        var endpoint = settings.ServerEndpoint;
        _ = Task.Run(async () =>
        {
            try
            {
                // A failed IPv6 request only means something while IPv4 traffic is proven to pass.
                var waiting = Stopwatch.StartNew();
                while (tunnelHealth.Report.State != TunnelHealthState.Healthy)
                {
                    if (waiting.Elapsed > Ipv6ProbeWaitForHealthyTunnel || activeRoute is null)
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }

                var result = await ipv6Probe.ProbeAsync(CancellationToken.None).ConfigureAwait(false);
                if (result == Ipv6Capability.Unknown || tunnelHealth.Report.State != TunnelHealthState.Healthy)
                {
                    return;
                }

                await RecordIpv6SupportAsync(endpoint, result == Ipv6Capability.Supported).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "The server IPv6 capability check failed.");
            }
        });
    }

    private async ValueTask RecordIpv6SupportAsync(string endpoint, bool supported)
    {
        var previous = Ipv6SupportFor(endpoint);
        if (!supported)
        {
            backgroundNotice = previous == false && activeRoute?.ResolveIpv6 == false
                ? "The Paqet server has no IPv6 route, so IPv6 DNS answers are skipped and applications use IPv4."
                : "The Paqet server has no IPv6 route. From the next connection, IPv6 DNS answers are skipped so applications use IPv4.";
            if (previous != false)
            {
                logger.LogWarning("The Paqet server did not reach an IPv6 destination; AAAA answers will be skipped.");
            }
        }
        else if (previous == false)
        {
            logger.LogInformation("The Paqet server reaches IPv6 again; AAAA answers return from the next connection.");
        }

        if (previous == supported)
        {
            return;
        }

        lock (stateSync)
        {
            var support = new Dictionary<string, bool>(brokerState.Ipv6Support, StringComparer.OrdinalIgnoreCase)
            {
                [endpoint] = supported,
            };
            brokerState = brokerState with { Ipv6Support = support };
        }

        await SaveStateAsync().ConfigureAwait(false);
    }

    private bool? Ipv6SupportFor(string endpoint)
    {
        lock (stateSync)
        {
            return brokerState.Ipv6Support.TryGetValue(endpoint, out var supported) ? supported : null;
        }
    }

    private async ValueTask SetDesiredAsync(bool desired, Guid? profileId = null)
    {
        desiredConnected = desired;
        lock (stateSync)
        {
            var targetProfile = desired ? profileId : null;
            if (brokerState.DesiredConnected == desired && brokerState.DesiredProfileId == targetProfile)
            {
                return;
            }

            brokerState = brokerState with { DesiredConnected = desired, DesiredProfileId = targetProfile };
        }

        await SaveStateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the latest in-memory state. Writes are serialized and each one reads the state
    /// only once it holds the write lock, so a slower earlier write (such as the IPv6 result
    /// recorded off the gate) can never land after, and undo, a newer Disconnect.
    /// </summary>
    private async ValueTask SaveStateAsync()
    {
        await stateWriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            BrokerRuntimeState latest;
            lock (stateSync)
            {
                latest = brokerState;
            }

            await stateStore.SaveAsync(latest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogWarning(exception, "The broker connection state could not be saved.");
        }
        finally
        {
            stateWriteGate.Release();
        }
    }

    private Guid? DesiredProfileId
    {
        get
        {
            lock (stateSync)
            {
                return brokerState.DesiredProfileId;
            }
        }
    }

    /// <summary>
    /// Waits for the runtime gate on behalf of a background operation. A Disconnect that
    /// cancels the operation while it queues is a normal outcome, not an error.
    /// </summary>
    /// <returns>True when the gate is held; false when the operation was cancelled and ended.</returns>
    private async ValueTask<bool> WaitForGateAsync(CancellationTokenSource operation, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            EndOperation(operation);
            return false;
        }
        catch
        {
            EndOperation(operation);
            throw;
        }
    }

    private void SetNextSupervision(DateTimeOffset? due) =>
        Interlocked.Exchange(ref nextSupervisionTicks, due?.UtcTicks ?? 0);

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (operationSync)
        {
            operations.Add(operation);
        }

        return operation;
    }

    private void EndOperation(CancellationTokenSource? operation)
    {
        if (operation is null)
        {
            return;
        }

        lock (operationSync)
        {
            operations.Remove(operation);
        }

        operation.Dispose();
    }

    private void CancelOperations()
    {
        CancellationTokenSource[] running;
        lock (operationSync)
        {
            running = [.. operations];
        }

        foreach (var operation in running)
        {
            try
            {
                operation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation finished meanwhile.
            }
        }
    }

    private async ValueTask<BrokerSnapshot> CreateCancelledSnapshotAsync()
    {
        progress.Clear();
        var settings = await snapshotMetadata.GetSettingsAsync(TryLoadSettingsAsync, CancellationToken.None)
            .ConfigureAwait(false);
        var snapshot = await CreateSnapshotAsync(settings, CancellationToken.None).ConfigureAwait(false);
        return snapshot with { OperationWarning = "The connection attempt was cancelled." };
    }

    private async ValueTask<EngineConfigurationSet> BuildEngineConfigurationsAsync(
        PaqetFireSettings settings,
        bool requireHotspot,
        ServerResolution serverResolution,
        bool? resolveIpv6Override,
        CancellationToken cancellationToken,
        RoutingDataSet? routingDataOverride = null)
    {
        var network = networkDetector.Detect(settings.NetworkInterfaceGuid, KnownServerAddress(settings.ServerEndpoint));
        var serverEndpoint = serverResolution == ServerResolution.Cached && TryGetKnownEndpoint(settings.ServerEndpoint) is { } known
            ? known
            : await ResolveServerEndpointAsync(settings.ServerEndpoint, network, cancellationToken).ConfigureAwait(false);
        var paqetProfile = new PaqetProfile(
            serverEndpoint,
            "127.0.0.1:1080",
            network.InterfaceName,
            network.InterfaceGuid,
            network.LocalIpv4Address,
            network.GatewayMacAddress,
            settings.LocalTcpFlags,
            settings.RemoteTcpFlags,
            settings.KcpMode);
        paqetProfile = settings.Advanced.ApplyTo(paqetProfile) with
        {
            SocksUsername = engineCredentials.Username,
            SocksPassword = engineCredentials.Password,
        };

        var notices = new List<string>();
        LanSocksShare? hotspotShare = null;
        string? hotspotAddress = null;
        if (settings.ShareViaHotspot)
        {
            if (hotspotDetector.TryDetect() is { } hotspot)
            {
                hotspotAddress = hotspot.Address;
                hotspotShare = new LanSocksShare(hotspot.Address, settings.HotspotSocksPort, settings.LanSocksUsername, settings.LanSocksPassword);
            }
            else if (requireHotspot)
            {
                throw new InvalidOperationException(
                    "No active Windows hotspot network was found. Turn on Mobile hotspot in Windows Settings, connect this laptop via Ethernet or Wi-Fi first, then re-detect.");
            }
            else
            {
                // Windows turns Mobile hotspot off on its own when no device is joined. That
                // must not block recovery of the main route; the share returns with the hotspot.
                notices.Add("Mobile hotspot is off, so the hotspot share is paused until it is turned back on.");
            }
        }

        var resolveIpv6 = resolveIpv6Override ?? (settings.RouteIpv6 && Ipv6SupportFor(settings.ServerEndpoint) != false);
        if (settings.RouteIpv6 && !resolveIpv6)
        {
            notices.Add("The Paqet server has no IPv6 route, so IPv6 DNS answers are skipped and applications use IPv4.");
        }

        // The rules Xray is given must match the data it will load: categories the bundled
        // files lack would fail Xray's configuration check.
        var data = routingDataOverride ??
                   (routingData is null
                       ? RoutingDataSet.Bundled
                       : await routingData.ResolveForLaunchAsync(cancellationToken).ConfigureAwait(false));
        var blockMalware = settings.BlockMalware && data.SupportsMalwareCategories;
        var paqetText = paqetWriter.Write(paqetProfile, settings.TransportKey);
        var psiphonText = settings.ExitProxy == ExitProxyKind.Psiphon
            ? psiphonWriter.Write(new PsiphonClientProfile(
                PsiphonJsonConfigurationWriter.LocalSocksPort,
                XrayJsonConfigurationWriter.PaqetPort,
                engineCredentials,
                settings.Psiphon?.EgressRegion ?? string.Empty))
            : null;
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
            settings.DirectRouteDestinations,
            new XrayDnsPolicy(
                settings.DnsResolver,
                network.DnsServers,
                ResolveIpv4: settings.RouteIpv4,
                ResolveIpv6: resolveIpv6,
                UseFakeDns: settings.FakeDns),
            engineCredentials,
            blockMalware,
            psiphonText is null ? null : new ExitProxyOutbound(PsiphonJsonConfigurationWriter.LocalSocksPort)));
        return new EngineConfigurationSet(
            paqetText,
            xrayText,
            CreateProxiFyreConfiguration(settings),
            network.InterfaceName,
            resolveIpv6,
            NetworkFingerprint.From(network, hotspotAddress),
            notices.Count == 0 ? null : string.Join(" ", notices),
            data,
            blockMalware)
        {
            PsiphonText = psiphonText,
        };
    }

    private async ValueTask WriteEngineConfigurationsAsync(
        EngineConfigurationSet configuration,
        CancellationToken cancellationToken)
    {
        await configurationStore.WriteAsync(paths.PaqetConfigurationPath, configuration.PaqetText, cancellationToken)
            .ConfigureAwait(false);
        await configurationStore.WriteAsync(paths.ProxiFyreConfigurationPath, configuration.ProxiFyreText, cancellationToken)
            .ConfigureAwait(false);
        await configurationStore.WriteAsync(paths.XrayConfigurationPath, configuration.XrayText, cancellationToken)
            .ConfigureAwait(false);
        xrayAdapter.UseRoutingData(configuration.RoutingData);
        if (configuration.PsiphonText is { } psiphonText)
        {
            await configurationStore.WriteAsync(paths.PsiphonConfigurationPath, psiphonText, cancellationToken)
                .ConfigureAwait(false);
        }

        // Xray's configuration above dials the exit only when it is selected.
        psiphonAdapter?.UseExit(configuration.PsiphonText is not null);
    }

    private NetworkFingerprint? TryDetectFingerprint(PaqetFireSettings settings)
    {
        try
        {
            var network = networkDetector.Detect(settings.NetworkInterfaceGuid, KnownServerAddress(settings.ServerEndpoint));
            return NetworkFingerprint.From(network, settings.ShareViaHotspot ? hotspotDetector.TryDetect()?.Address : null);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogInformation("No usable network after the change: {Detail}", exception.Message);
            return null;
        }
    }

    private IReadOnlyList<IPAddress> DetectNetworkResolvers(PaqetFireSettings settings)
    {
        try
        {
            return networkDetector.Detect(settings.NetworkInterfaceGuid, KnownServerAddress(settings.ServerEndpoint))
                .DnsServers.Select(IPAddress.Parse).ToArray();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private void FlushDnsCache()
    {
        if (!WindowsDnsCache.TryFlush())
        {
            logger.LogDebug("The Windows DNS cache could not be flushed.");
        }
    }

    /// <summary>
    /// Paqet resolves a host name once at startup through Windows, whose lookups ProxiFyre
    /// may be capturing into a route that is not up yet. Hand it an address instead.
    /// </summary>
    private async ValueTask<string> ResolveServerEndpointAsync(
        string endpoint,
        NetworkEnvironment network,
        CancellationToken cancellationToken)
    {
        if (!TrySplitHostEndpoint(endpoint, out var host, out var port))
        {
            return endpoint;
        }

        var servers = network.DnsServers.Select(IPAddress.Parse).ToArray();
        var addresses = await DirectDnsResolver.ResolveIpv4Async(host, servers, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(SystemResolverTimeout);
                addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException ||
                                                  exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                addresses = [];
            }
        }

        if (addresses.Count > 0)
        {
            await RememberResolvedServerAsync(host, addresses[0]).ConfigureAwait(false);
            return $"{addresses[0]}:{port}";
        }

        if (TryGetKnownEndpoint(endpoint) is { } previous)
        {
            logger.LogWarning("The server host name could not be resolved; reusing its last known address.");
            return previous;
        }

        logger.LogWarning("The server host name could not be resolved; Paqet will resolve it at startup.");
        return endpoint;
    }

    private async ValueTask RememberResolvedServerAsync(string host, IPAddress address)
    {
        lock (stateSync)
        {
            var text = address.ToString();
            if (brokerState.ResolvedServers.TryGetValue(host, out var known) && known == text)
            {
                return;
            }

            var resolved = new Dictionary<string, string>(brokerState.ResolvedServers, StringComparer.OrdinalIgnoreCase)
            {
                [host] = text,
            };
            brokerState = brokerState with { ResolvedServers = resolved };
        }

        await SaveStateAsync().ConfigureAwait(false);
    }

    /// <summary>The endpoint with an address Paqet can use without a lookup, if one is known.</summary>
    private string? TryGetKnownEndpoint(string endpoint)
    {
        if (!TrySplitHostEndpoint(endpoint, out var host, out var port))
        {
            return endpoint;
        }

        lock (stateSync)
        {
            return brokerState.ResolvedServers.TryGetValue(host, out var address) ? $"{address}:{port}" : null;
        }
    }

    private IPAddress? KnownServerAddress(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || TryGetKnownEndpoint(endpoint.Trim()) is not { } known)
        {
            return null;
        }

        var separator = known.LastIndexOf(':');
        return separator > 0 && IPAddress.TryParse(known[..separator].Trim('[', ']'), out var address) ? address : null;
    }

    private static bool TrySplitHostEndpoint(string endpoint, out string host, out string port)
    {
        host = port = string.Empty;
        var separator = endpoint.LastIndexOf(':');
        if (endpoint.StartsWith('[') || separator <= 0 || IPAddress.TryParse(endpoint[..separator], out _))
        {
            return false;
        }

        host = endpoint[..separator];
        port = endpoint[(separator + 1)..];
        return true;
    }

    private string CreateProxiFyreConfiguration(PaqetFireSettings settings)
    {
        var policy = new RoutingPolicy(
            settings.RoutingMode,
            $"127.0.0.1:{XrayJsonConfigurationWriter.InboundPort}",
            settings.SelectedApplications,
            settings.UserExclusions,
            BypassLan: settings.BypassLan,
            RouteTcp: settings.RouteTcp,
            RouteUdp: settings.RouteUdp,
            RouteIpv4: settings.RouteIpv4,
            RouteIpv6: settings.RouteIpv6,
            Username: engineCredentials.Username,
            Password: engineCredentials.Password);
        var brokerExecutablePath = Path.Combine(AppContext.BaseDirectory, "PaqetFire.Broker.exe");
        // Psiphon's sockets go to Paqet over loopback; capturing any of them would loop.
        string[] engineExecutables = [paths.XrayExecutablePath, paths.PsiphonExecutablePath, brokerExecutablePath];
        var lockedExclusions = RoutingPolicyCompiler.CreateLockedExclusions(
            paths.PaqetExecutablePath,
            paths.ProxiFyreExecutablePath,
            engineExecutables);
        var routePlan = RoutingPolicyCompiler.Compile(
            policy,
            paths.PaqetExecutablePath,
            paths.ProxiFyreExecutablePath,
            engineExecutables);
        return proxiFyreWriter.Write(routePlan, lockedExclusions);
    }

    private async ValueTask<BrokerSnapshot> CreateSnapshotAsync(
        PaqetFireSettingsView? settings,
        CancellationToken cancellationToken)
    {
        var status = await connectionController.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentProgress = progress.Current;
        // While Paqet alone is being restarted the route is recovering, not broken.
        var recoveringTransport = currentProgress?.Stage == ConnectionStage.RecoveringTransport &&
                                  ConnectionRecoveryPolicy.IsTransportOnlyFault(status.Engines);
        var connectionState = recoveringTransport ? ConnectionState.Connecting : status.State;
        var prerequisites = snapshotMetadata.GetPrerequisites(prerequisiteInspector.Inspect);
        var missing = prerequisites.Where(item => !item.IsInstalled).Select(item => item.DisplayName).ToArray();
        var message = missing.Length > 0
            ? $"Install the missing prerequisite{(missing.Length == 1 ? string.Empty : "s")}: {string.Join(", ", missing)}."
            : settings is null
                ? "Add your server and transport key, then save the profile."
                : recoveringTransport
                    ? $"Paqet stopped; restarting the transport (attempt {currentProgress!.Attempt}). Routed applications stay on the route meanwhile."
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
        var psiphonLogs = (psiphonAdapter?.GetRecentLogs() ?? [])
            .TakeLast(20)
            .Select(entry => (
                entry.OccurredAt,
                Text: $"{entry.OccurredAt:HH:mm:ss}  INF  Psiphon · {entry.Message}"));
        var logs = TakeRecentLogsWithinBudget(paqetLogs
            .Concat(xrayLogs)
            .Concat(psiphonLogs)
            .OrderBy(entry => entry.OccurredAt)
            .Select(entry => entry.Text));

        var exitProxy = status.Exit is { State: EngineState.Running } && psiphonAdapter?.Session is { } session
            ? new ExitProxyStatus(ExitProxyKind.Psiphon, session.TunnelActive, session.ConnectedRegion, session.AvailableRegions)
            : null;
        return new BrokerSnapshot(
            status.Engines,
            IsRouting: status.State == ConnectionState.Connected,
            IsKillSwitchEnabled: settings?.KillSwitchEnabled == true &&
                                 status.ProxiFyre.State == EngineState.Running,
            status.ObservedAt ?? DateTimeOffset.UtcNow,
            IsConfigured: settings is not null &&
                          (settings.HasTransportKey || !PaqetConfigurationValidator.RequiresTransportKey(settings.Advanced.KcpBlock)) &&
                          !string.IsNullOrWhiteSpace(settings.ServerEndpoint),
            Settings: settings ?? CreateDefaultView(),
            Prerequisites: prerequisites,
            RecentLogs: logs,
            StatusMessage: message,
            ConnectionState: connectionState,
            OperationWarning: backgroundNotice,
            Verification: latestVerification,
            ProfileCatalog: profileCatalog is null ? null : ProfileCatalogView.FromCatalog(profileCatalog),
            Tunnel: status.State == ConnectionState.Connected ? tunnelHealth.Report : null,
            Progress: currentProgress,
            RoutingData: routingData?.Status)
        {
            ExitProxy = exitProxy,
        };
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

        var settings = await TryLoadSettingsAsync(cancellationToken).ConfigureAwait(false) ?? new PaqetFireSettings();
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
        Psiphon = new PsiphonExitOptions((settings.Psiphon?.EgressRegion ?? string.Empty).Trim().ToUpperInvariant()),
        SelectedApplications = NormalizeList(settings.SelectedApplications),
        UserExclusions = NormalizeList(settings.UserExclusions),
        DirectRouteDestinations = NormalizeList(settings.DirectRouteDestinations),
        LocalTcpFlags = NormalizeFlags(settings.LocalTcpFlags),
        RemoteTcpFlags = NormalizeFlags(settings.RemoteTcpFlags),
    };

    /// <summary>A digest of everything that shapes the route, without keeping another copy of its secrets.</summary>
    private static string CreateSettingsKey(PaqetFireSettings settings) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string> values) => values
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static IReadOnlyList<string> NormalizeFlags(IEnumerable<string> values) => values
        .Select(value => value.Trim().ToUpperInvariant())
        .ToArray();

    private void EnsurePrerequisitesAvailable()
    {
        var missing = snapshotMetadata.GetPrerequisites(prerequisiteInspector.Inspect, forceRefresh: true)
            .Where(item => !item.IsInstalled).ToArray();
        if (missing.Length > 0)
        {
            throw new MissingPrerequisiteException(missing);
        }

        // Runs alongside the connect; ProxiFyre waits for it before it starts capturing.
        _ = engineFirewallRules.EnsureRepairedAsync();
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

    private enum ServerResolution
    {
        /// <summary>Look the server up again; a connect should not keep a stale address.</summary>
        Fresh,

        /// <summary>Reuse the last address that resolved, so a recovery never waits on DNS.</summary>
        Cached,
    }

    private sealed record ActiveRoute(
        string SettingsKey,
        string PaqetText,
        string XrayText,
        bool ResolveIpv6,
        RoutingDataSet RoutingData,
        bool BlockMalware)
    {
        public static ActiveRoute From(string settingsKey, EngineConfigurationSet configuration) => new(
            settingsKey,
            configuration.PaqetText,
            configuration.XrayText,
            configuration.ResolveIpv6,
            configuration.RoutingData,
            configuration.BlockMalware);
    }

    private sealed record EngineConfigurationSet(
        string PaqetText,
        string XrayText,
        string ProxiFyreText,
        string InterfaceName,
        bool ResolveIpv6,
        NetworkFingerprint Fingerprint,
        string? Notice,
        RoutingDataSet RoutingData,
        bool BlockMalware)
    {
        /// <summary>The exit engine's configuration; null when the profile uses no exit.</summary>
        public string? PsiphonText { get; init; }
    }
}

/// <summary>The network details the engines are configured from; any change may need a restart.</summary>
public sealed record NetworkFingerprint(
    string InterfaceGuid,
    string LocalIpv4Address,
    string GatewayIpv4Address,
    string GatewayMacAddress,
    string DnsServers,
    string? HotspotAddress)
{
    public static NetworkFingerprint From(NetworkEnvironment network, string? hotspotAddress) => new(
        network.InterfaceGuid,
        network.LocalIpv4Address,
        network.GatewayIpv4Address,
        network.GatewayMacAddress,
        string.Join(",", network.DnsServers),
        hotspotAddress);
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
    string MachineSettingsPath)
{
    public string PsiphonExecutablePath { get; init; } =
        Path.Combine(PayloadRoot, "engines", "psiphon", "x64", "psiphon-tunnel-core-i686.exe");

    public string PsiphonConfigurationPath { get; init; } =
        Path.Combine(PayloadRoot, "engines", "psiphon", "x64", "config.json");

    /// <summary>Psiphon's server list and data store, beside the machine settings.</summary>
    public string PsiphonDataDirectory { get; init; } =
        Path.Combine(Path.GetDirectoryName(MachineSettingsPath) ?? PayloadRoot, "psiphon");
}

/// <summary>A request was cancelled, typically by a Disconnect, before it took effect.</summary>
public sealed class BrokerOperationCancelledException(string message) : InvalidOperationException(message);

public sealed class MissingPrerequisiteException(
    IReadOnlyList<PrerequisiteStatus> prerequisites) : InvalidOperationException(
        "One or more required packet drivers are missing.")
{
    public IReadOnlyList<PrerequisiteStatus> Prerequisites { get; } = prerequisites;
}
