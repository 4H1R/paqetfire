using System.ComponentModel;
using System.Runtime.CompilerServices;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;
using PaqetFire.Desktop.Ipc;
using PaqetFire.Desktop.Presentation;
using CoreConnectionState = PaqetFire.Core.Connections.ConnectionState;

namespace PaqetFire.Desktop.ViewModels;

public sealed class ConnectionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LifecycleRequestTimeout = TimeSpan.FromMinutes(2);

    private readonly IBrokerClient brokerClient;
    private readonly IUiDispatcher dispatcherQueue;
    private readonly SemaphoreSlim operationLock = new(1, 1);

    private BrokerConnectionState connectionState = BrokerConnectionState.Disconnected;
    private string stateLabel = "CHECKING SYSTEM";
    private string statusText = "Broker not checked";
    private string statusDescription = "PaqetFire will verify its broker and bundled engines.";
    private string paqetStatusText = "Not checked";
    private string xrayStatusText = "Not checked";
    private string routingStatusText = "Not active";
    private string paqetDetailText = "Waiting for the broker readiness check.";
    private string routingDetailText = "Routing is currently inactive.";
    private string lastCheckedText = "Not checked yet";
    private string brokerStatusText = "Checking";
    private string effectiveRoutingText = "Set up a profile to preview which traffic will use PaqetFire.";
    private string? errorMessage;
    private string? lastActivitySummary;
    private bool isBusy;
    private bool disposed;

    public ConnectionViewModel(
        IBrokerClient brokerClient,
        IUiDispatcher dispatcherQueue)
    {
        this.brokerClient = brokerClient ?? throw new ArgumentNullException(nameof(brokerClient));
        this.dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        brokerClient.EventReceived += OnBrokerEventReceived;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? ActivityOccurred;

    public event Action<BrokerSnapshot>? SnapshotReceived;

    public BrokerConnectionState ConnectionState
    {
        get => connectionState;
        private set
        {
            if (SetProperty(ref connectionState, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
                OnPropertyChanged(nameof(CanPrimaryAction));
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string StateLabel
    {
        get => stateLabel;
        private set
        {
            if (SetProperty(ref stateLabel, value))
            {
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public string StatusDescription
    {
        get => statusDescription;
        private set => SetProperty(ref statusDescription, value);
    }

    public string PaqetStatusText
    {
        get => paqetStatusText;
        private set => SetProperty(ref paqetStatusText, value);
    }

    public string RoutingStatusText
    {
        get => routingStatusText;
        private set => SetProperty(ref routingStatusText, value);
    }

    public string XrayStatusText
    {
        get => xrayStatusText;
        private set => SetProperty(ref xrayStatusText, value);
    }

    public string PaqetDetailText
    {
        get => paqetDetailText;
        private set => SetProperty(ref paqetDetailText, value);
    }

    public string RoutingDetailText
    {
        get => routingDetailText;
        private set => SetProperty(ref routingDetailText, value);
    }

    public string LastCheckedText
    {
        get => lastCheckedText;
        private set => SetProperty(ref lastCheckedText, value);
    }

    public string BrokerStatusText
    {
        get => brokerStatusText;
        private set => SetProperty(ref brokerStatusText, value);
    }

    public string EffectiveRoutingText
    {
        get => effectiveRoutingText;
        private set => SetProperty(ref effectiveRoutingText, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetProperty(ref errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanPrimaryAction));
                OnPropertyChanged(nameof(CanSaveAndConnect));
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public bool IsConnected => ConnectionState == BrokerConnectionState.Connected;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool CanConnect =>
        !IsBusy && ConnectionState is
            BrokerConnectionState.Disconnected or
            BrokerConnectionState.Guarded or
            BrokerConnectionState.Degraded or
            BrokerConnectionState.Faulted;

    public bool CanDisconnect =>
        !IsBusy && ConnectionState is BrokerConnectionState.Connected or BrokerConnectionState.Degraded;

    public bool CanRefresh => !IsBusy;

    public bool CanSaveAndConnect => !IsBusy;

    public bool CanPrimaryAction =>
        !IsBusy && ConnectionState is not BrokerConnectionState.Connecting and not BrokerConnectionState.Disconnecting;

    public string PrimaryActionText => IsBusy
        ? ConnectionState == BrokerConnectionState.Disconnecting ? "Disconnecting…" : "Working…"
        : ConnectionState switch
        {
            BrokerConnectionState.Connected or BrokerConnectionState.Degraded => "Disconnect",
            BrokerConnectionState.Guarded => "Reconnect",
            BrokerConnectionState.NotReady when StateLabel == "PROFILE REQUIRED" => "Set up connection",
            BrokerConnectionState.NotReady => "Open diagnostics",
            BrokerConnectionState.Faulted => "Open diagnostics",
            _ => "Connect",
        };

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return;
        }

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpdateUiAsync(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
                StateLabel = "CHECKING SYSTEM";
                StatusText = "Checking PaqetFire Broker…";
                StatusDescription = "Reading engine and routing status.";
                BrokerStatusText = "Checking";
            }).ConfigureAwait(false);

            try
            {
                await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                var snapshot = await brokerClient.GetSnapshotAsync(StatusRequestTimeout, cancellationToken)
                    .ConfigureAwait(false);
                await UpdateUiAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
            }
            finally
            {
                await UpdateUiAsync(() => IsBusy = false).ConfigureAwait(false);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return;
        }

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || ConnectionState is BrokerConnectionState.Connecting or BrokerConnectionState.Connected)
            {
                return;
            }

            await UpdateUiAsync(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
                ConnectionState = BrokerConnectionState.Connecting;
                StateLabel = "CONNECTING";
                StatusText = "Connecting to PaqetFire Broker…";
                StatusDescription = "Paqet starts first, then Xray policy routing, then ProxiFyre application routing.";
                ActivityOccurred?.Invoke("Connection requested.");
            }).ConfigureAwait(false);

            try
            {
                await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                var snapshot = await brokerClient.SetConnectionStateAsync(
                        connected: true,
                        LifecycleRequestTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                await UpdateUiAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
            }
            finally
            {
                await UpdateUiAsync(() => IsBusy = false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await TryUpdateUiAsync(() =>
            {
                ConnectionState = BrokerConnectionState.Disconnected;
                StateLabel = "NOT CONNECTED";
                StatusText = "Connection cancelled";
                StatusDescription = "No routing change was completed.";
                ErrorMessage = null;
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryUpdateUiAsync(() =>
            {
                ConnectionState = BrokerConnectionState.Faulted;
                StateLabel = "BROKER UNAVAILABLE";
                StatusText = "Broker unavailable";
                StatusDescription = "The protected broker service could not be reached.";
                ErrorMessage = GetFriendlyError(exception);
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<bool> SaveSettingsAsync(
        PaqetFireSettings settings,
        bool connectAfterSave = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (disposed)
        {
            return false;
        }

        if (!await operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            await UpdateUiAsync(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
                StateLabel = connectAfterSave ? "CONNECTING" : "SAVING PROFILE";
                StatusText = connectAfterSave ? "Applying configuration and connecting…" : "Applying configuration…";
                StatusDescription = "The broker is validating the profile and generating engine configuration.";
                ActivityOccurred?.Invoke(connectAfterSave
                    ? "Configuration save and connection requested."
                    : "Configuration save requested.");
            }).ConfigureAwait(false);

            try
            {
                await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                var snapshot = await brokerClient.SaveSettingsAsync(
                        settings,
                        connectAfterSave,
                        LifecycleRequestTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                await UpdateUiAsync(() =>
                {
                    ApplySnapshot(snapshot);
                    if (!string.IsNullOrWhiteSpace(snapshot.OperationWarning))
                    {
                        ErrorMessage = snapshot.OperationWarning;
                        ActivityOccurred?.Invoke("The profile was saved, but the connection did not start.");
                    }
                }).ConfigureAwait(false);
                return true;
            }
            catch (BrokerRequestException exception)
            {
                await UpdateUiAsync(() =>
                {
                    ConnectionState = BrokerConnectionState.NotReady;
                    StateLabel = "PROFILE NOT APPLIED";
                    StatusText = "Configuration needs attention";
                    StatusDescription = exception.Message;
                    ErrorMessage = GetFriendlyBrokerError(exception.Error.Code);
                    ActivityOccurred?.Invoke("The broker rejected the configuration.");
                }).ConfigureAwait(false);
                return false;
            }
            catch (Exception exception)
            {
                await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
                return false;
            }
            finally
            {
                await UpdateUiAsync(() => IsBusy = false).ConfigureAwait(false);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return;
        }

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || ConnectionState == BrokerConnectionState.Disconnected)
            {
                return;
            }

            await UpdateUiAsync(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
                ConnectionState = BrokerConnectionState.Disconnecting;
                StateLabel = "DISCONNECTING";
                StatusText = "Disconnecting…";
                StatusDescription = "Application routing stops before the Paqet carrier.";
                ActivityOccurred?.Invoke("Disconnect requested.");
            }).ConfigureAwait(false);

            try
            {
                await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                var snapshot = await brokerClient.SetConnectionStateAsync(
                        connected: false,
                        LifecycleRequestTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                await UpdateUiAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await UpdateUiAsync(() =>
                {
                    ConnectionState = BrokerConnectionState.Faulted;
                    StateLabel = "DISCONNECT INCOMPLETE";
                    StatusText = "Disconnect incomplete";
                    StatusDescription = "Check the broker status before reconnecting.";
                    ErrorMessage = GetFriendlyError(exception);
                }).ConfigureAwait(false);
            }
            finally
            {
                await UpdateUiAsync(() => IsBusy = false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await TryUpdateUiAsync(() =>
            {
                StatusText = "Disconnect cancelled";
                StatusDescription = "The previous routing state may still be active.";
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryUpdateUiAsync(() =>
            {
                ConnectionState = BrokerConnectionState.Faulted;
                StateLabel = "DISCONNECT INCOMPLETE";
                StatusText = "Disconnect incomplete";
                StatusDescription = "Check the broker status before reconnecting.";
                ErrorMessage = GetFriendlyError(exception);
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            brokerClient.EventReceived -= OnBrokerEventReceived;
            await SafeCloseTransportAsync().ConfigureAwait(false);

            if (brokerClient is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Shutdown is best-effort; transport errors are not exposed to the UI.
                }
            }
        }
        finally
        {
            operationLock.Release();
            operationLock.Dispose();
        }
    }

    private void OnBrokerEventReceived(BrokerEvent brokerEvent)
    {
        if (disposed)
        {
            return;
        }

        _ = dispatcherQueue.TryEnqueue(() => ApplyBrokerEvent(brokerEvent));
    }

    private void ApplyBrokerEvent(BrokerEvent brokerEvent)
    {
        if (disposed || brokerEvent.ProtocolVersion != IpcProtocol.Version)
        {
            return;
        }

        if (brokerEvent.Kind == BrokerEventKind.SnapshotChanged && brokerEvent.Snapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
            return;
        }

        if (brokerEvent.Kind == BrokerEventKind.Faulted)
        {
            ConnectionState = BrokerConnectionState.Faulted;
            StateLabel = "BROKER FAULT";
            StatusText = "Broker reported a fault";
            StatusDescription = "A privileged connection operation failed.";
            ErrorMessage = GetFriendlyBrokerError(brokerEvent.Error?.Code);
        }
    }

    private void ApplySnapshot(BrokerSnapshot snapshot)
    {
        ErrorMessage = null;

        var runningCount = snapshot.Engines.Count(engine => engine.State == EngineState.Running);
        var engineSummary = runningCount switch
        {
            0 => "No engines running",
            1 => "1 engine running",
            _ => $"{runningCount} engines running",
        };

        var paqet = snapshot.Engines.FirstOrDefault(engine => engine.Engine == EngineKind.Paqet);
        var xray = snapshot.Engines.FirstOrDefault(engine => engine.Engine == EngineKind.Xray);
        var proxiFyre = snapshot.Engines.FirstOrDefault(engine => engine.Engine == EngineKind.ProxiFyre);
        PaqetStatusText = paqet is null
            ? "Unavailable"
            : EngineStateText.GetDisplayName(paqet.State);
        PaqetDetailText = paqet switch
        {
            null => "The broker did not report a Paqet engine.",
            { State: EngineState.NotInstalled } => "The bundled Paqet engine was not found.",
            { Endpoint: not null } => $"SOCKS5 endpoint · {paqet.Endpoint}",
            { Detail: not null } => paqet.Detail,
            { Version: not null } => $"Version {paqet.Version}",
            _ => "Engine status reported by the broker.",
        };
        XrayStatusText = xray is null
            ? "Unavailable"
            : EngineStateText.GetDisplayName(xray.State);
        RoutingStatusText = snapshot.IsRouting
            ? "Everything · loop exclusions locked"
            : snapshot.IsKillSwitchEnabled
                ? "Kill switch active"
            : proxiFyre is null
                ? "Unavailable"
                : EngineStateText.GetDisplayName(proxiFyre.State);
        RoutingDetailText = snapshot.IsRouting
            ? snapshot.IsKillSwitchEnabled
                ? "Traffic is routed with application-level fail-closed protection."
                : snapshot.Settings?.RegionalPreset == RegionalRoutingPreset.IranDirect
                    ? "Iranian destinations go direct; other selected traffic uses Paqet."
                    : "All selected destinations use Paqet; kill switch is off."
            : snapshot.IsKillSwitchEnabled
                ? "Protected applications stay attached to an unavailable local proxy until you reconnect or turn the kill switch off."
            : proxiFyre switch
            {
                null => "The broker did not report a ProxiFyre engine.",
                { State: EngineState.NotInstalled } => "The bundled ProxiFyre engine was not found.",
                { Detail: not null } => proxiFyre.Detail,
                { Version: not null } => $"Version {proxiFyre.Version}",
                _ => "Application routing is currently inactive.",
            };
        BrokerStatusText = "Running";
        LastCheckedText = $"Updated {snapshot.CapturedAt.ToLocalTime():t}";
        EffectiveRoutingText = CreateEffectiveRoutingText(snapshot.Settings);
        var engineStates = string.Join(", ", snapshot.Engines.Select(engine => $"{engine.Engine} {engine.State}"));
        var activitySummary = $"{snapshot.ConnectionState}:{engineStates}:{snapshot.StatusMessage}";
        if (!string.Equals(lastActivitySummary, activitySummary, StringComparison.Ordinal))
        {
            lastActivitySummary = activitySummary;
            ActivityOccurred?.Invoke($"Status changed: {engineSummary.ToLowerInvariant()} · {engineStates}.");
        }
        SnapshotReceived?.Invoke(snapshot);

        var missingPrerequisites = snapshot.Prerequisites?.Where(item => !item.IsInstalled).ToArray() ?? [];
        if (missingPrerequisites.Length > 0)
        {
            ConnectionState = BrokerConnectionState.NotReady;
            StateLabel = "PREREQUISITE REQUIRED";
            StatusText = missingPrerequisites.Length == 1
                ? $"Install {missingPrerequisites[0].DisplayName}"
                : $"Install {missingPrerequisites.Length} prerequisites";
            StatusDescription = snapshot.StatusMessage ?? "A required packet-capture component is missing.";
        }
        else if (snapshot.Engines.Any(engine => engine.State == EngineState.NotInstalled))
        {
            ConnectionState = BrokerConnectionState.NotReady;
            StateLabel = "REPAIR REQUIRED";
            StatusText = "Bundled engine files are unavailable";
            StatusDescription = snapshot.StatusMessage ?? "Repair the PaqetFire installation, then refresh.";
        }
        else if (!snapshot.IsConfigured)
        {
            ConnectionState = BrokerConnectionState.NotReady;
            StateLabel = "PROFILE REQUIRED";
            StatusText = "Add your Paqet server";
            StatusDescription = snapshot.StatusMessage ?? "Open Paqet connection, enter the server and transport key, then save.";
        }
        else if (snapshot.ConnectionState == CoreConnectionState.Guarded)
        {
            ConnectionState = BrokerConnectionState.Guarded;
            StateLabel = "KILL SWITCH ACTIVE";
            StatusText = "Routed applications are blocked";
            StatusDescription = snapshot.StatusMessage ??
                "The Paqet route is disconnected and protected applications cannot fall back to a direct connection.";
        }
        else if (snapshot.Engines.Any(engine => engine.State == EngineState.Faulted))
        {
            ConnectionState = BrokerConnectionState.Faulted;
            StateLabel = "CONNECTION FAULT";
            StatusText = $"Connection fault · {engineSummary}";
            StatusDescription = "One or more engines need attention before traffic can be protected.";
        }
        else if (snapshot.Engines.Any(engine => engine.State == EngineState.Starting))
        {
            ConnectionState = BrokerConnectionState.Connecting;
            StateLabel = "CONNECTING";
            StatusText = $"Connecting · {engineSummary}";
            StatusDescription = "Waiting for Paqet, Xray, and ProxiFyre to report stable running states.";
        }
        else if (snapshot.Engines.Any(engine => engine.State == EngineState.Stopping))
        {
            ConnectionState = BrokerConnectionState.Disconnecting;
            StateLabel = "DISCONNECTING";
            StatusText = $"Disconnecting · {engineSummary}";
            StatusDescription = "Stopping application routing before the carrier.";
        }
        else if (snapshot.IsRouting && runningCount == snapshot.Engines.Count)
        {
            ConnectionState = BrokerConnectionState.Connected;
            StateLabel = snapshot.IsKillSwitchEnabled ? "ROUTING ACTIVE · KILL SWITCH ON" : "ROUTING ACTIVE";
            StatusText = $"Routing active · {engineSummary}";
            StatusDescription = snapshot.IsKillSwitchEnabled
                ? "Applications covered by the routing policy are protected by the route and fail-closed policy."
                : "Traffic is routed, but applications may reconnect directly if the route fails.";
        }
        else if (runningCount == 0)
        {
            ConnectionState = BrokerConnectionState.Disconnected;
            StateLabel = "NOT CONNECTED";
            StatusText = "Not connected";
            StatusDescription = "No application traffic is being routed through Paqet.";
        }
        else
        {
            ConnectionState = BrokerConnectionState.Degraded;
            StateLabel = "DEGRADED";
            StatusText = $"Connection incomplete · {engineSummary}";
            StatusDescription = "The engine states do not currently form a safe route.";
        }
    }

    private void ApplyTransportFailure(Exception exception)
    {
        ConnectionState = BrokerConnectionState.Faulted;
        StateLabel = "BROKER UNAVAILABLE";
        StatusText = "Broker unavailable";
        StatusDescription = "Install or start the PaqetFire Broker service, then refresh.";
        PaqetStatusText = "Unknown";
        RoutingStatusText = "Unknown";
        PaqetDetailText = "No engine status is available while the broker is unreachable.";
        RoutingDetailText = "No routing status is available while the broker is unreachable.";
        BrokerStatusText = "Unavailable";
        LastCheckedText = $"Failed {DateTimeOffset.Now:t}";
        ErrorMessage = GetFriendlyError(exception);
        ActivityOccurred?.Invoke("Could not contact the broker.");
    }

    private static string CreateEffectiveRoutingText(PaqetFireSettingsView? settings)
    {
        if (settings is null || !settings.HasTransportKey)
        {
            return "Set up a profile to preview which traffic will use PaqetFire.";
        }

        var scope = settings.RoutingMode == RoutingMode.AllApplications
            ? "All supported apps"
            : settings.SelectedApplications.Count switch
            {
                0 => "No apps selected",
                1 => "1 selected app",
                var count => $"{count} selected apps",
            };
        var destination = settings.RegionalPreset == RegionalRoutingPreset.IranDirect
            ? "Iran direct"
            : "All destinations routed";
        var lan = settings.BypassLan ? "LAN bypass on" : "LAN routed";
        var bitTorrent = settings.DirectBitTorrent ? "BitTorrent direct" : "BitTorrent routed";
        var killSwitch = settings.KillSwitchEnabled ? "Kill switch on" : "Kill switch off";
        var customDirect = settings.DirectRouteDestinations.Count switch
        {
            0 => "no custom direct destinations",
            1 => "1 custom direct destination",
            var count => $"{count} custom direct destinations",
        };
        return $"{scope} · {destination} · {lan} · {bitTorrent} · {customDirect} · {killSwitch}";
    }

    private async Task SafeCloseTransportAsync()
    {
        try
        {
            await brokerClient.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Connection cleanup is best-effort after another operation has failed.
        }
    }

    private Task UpdateUiAsync(Action update)
    {
        if (dispatcherQueue.HasThreadAccess)
        {
            update();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    update();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private async Task TryUpdateUiAsync(Action update)
    {
        try
        {
            await UpdateUiAsync(update).ConfigureAwait(false);
        }
        catch
        {
            // The application may already be shutting down.
        }
    }

    private static string GetFriendlyError(Exception exception) => exception switch
    {
        TimeoutException => "The broker did not respond in time.",
        OperationCanceledException => "The operation was cancelled.",
        UnauthorizedAccessException => "PaqetFire does not have permission to contact the broker.",
        InvalidDataException => "The broker returned an incompatible response.",
        BrokerRequestException requestException => GetFriendlyBrokerError(requestException.Error.Code),
        IOException => "The broker connection was interrupted.",
        InvalidOperationException => "The broker connection is not ready.",
        _ => "PaqetFire could not contact the broker.",
    };

    private static string GetFriendlyBrokerError(BrokerErrorCode? code) => code switch
    {
        BrokerErrorCode.IncompatibleProtocol => "The app and broker versions are incompatible.",
        BrokerErrorCode.InvalidRequest => "The broker could not understand the request.",
        BrokerErrorCode.Unauthorized => "The broker denied this operation.",
        BrokerErrorCode.Busy => "The broker is busy. Try again shortly.",
        BrokerErrorCode.ConfigurationInvalid => "The connection or routing settings are invalid.",
        BrokerErrorCode.PrerequisiteMissing => "A required packet-capture component is missing.",
        BrokerErrorCode.EngineFailure => "A bundled network engine reported a failure.",
        _ => "The broker encountered an internal error.",
    };

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
