using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Routing;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.RoutingData;
using PaqetFire.Core.Usage;
using PaqetFire.Desktop.Ipc;
using PaqetFire.Desktop.Presentation;
using CoreConnectionState = PaqetFire.Core.Connections.ConnectionState;
using TunnelHealthState = PaqetFire.Core.Connections.TunnelHealthState;

namespace PaqetFire.Desktop.ViewModels;

public sealed class ConnectionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LifecycleRequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UserTransitionGrace = TimeSpan.FromSeconds(10);

    private readonly IBrokerClient brokerClient;
    private readonly IUiDispatcher dispatcherQueue;
    private readonly ConnectionViewModelOptions options;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();

    private BrokerConnectionState connectionState = BrokerConnectionState.Disconnected;
    private string stateLabel = "CHECKING SYSTEM";
    private string statusText = "Broker not checked";
    private string statusDescription = "PaqetFire will verify its broker and bundled engines.";
    private string paqetStatusText = "Not checked";
    private string xrayStatusText = "Not checked";
    private string exitStatusText = "Not used";
    private string routingStatusText = "Not active";
    private string paqetDetailText = "Waiting for the broker readiness check.";
    private string routingDetailText = "Routing is currently inactive.";
    private string lastCheckedText = "Not checked yet";
    private string brokerStatusText = "Checking";
    private string effectiveRoutingText = "Set up a profile to preview which traffic will use PaqetFire.";
    private string progressSummary = string.Empty;
    private string? errorMessage;
    private string? notice;
    private string? dismissedNotice;
    private RoutingDataStatus? routingData;
    private ErrorKind errorKind;
    private FailureAction failureAction;
    private ReadinessIssue readiness;
    private string? lastActivitySummary;
    private bool isBusy;
    private bool isTunnelStalled;
    private bool isBrokerReachable;
    private bool isReconnecting;
    private bool connectInFlight;
    private bool cancelRequested;
    private bool liveStatsPaused;
    private bool disposed;
    private ConnectionProgress? progress;
    private BrokerSnapshot? lastSnapshot;
    private DateTimeOffset lastUserOperationAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? livePolling;
    private CancellationTokenSource? progressTicker;
    private int heartbeatStarted;
    private int reconnectRunning;
    private volatile bool reconnectRequested;

    public ConnectionViewModel(
        IBrokerClient brokerClient,
        IUiDispatcher dispatcherQueue,
        ConnectionViewModelOptions? options = null)
    {
        this.brokerClient = brokerClient ?? throw new ArgumentNullException(nameof(brokerClient));
        this.dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        this.options = options ?? new ConnectionViewModelOptions();
        time = this.options.TimeProvider;
        brokerClient.EventReceived += OnBrokerEventReceived;
        brokerClient.ConnectionLost += OnConnectionLost;
    }

    private enum ErrorKind
    {
        None,
        Transport,
        Operation,
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? ActivityOccurred;

    public event Action<BrokerSnapshot>? SnapshotReceived;

    public LiveStatsViewModel Live { get; } = new();

    public ObservableCollection<ConnectionProgressStep> ProgressSteps { get; } = [];

    public BrokerSnapshot? LastSnapshot => lastSnapshot;

    private bool UsesExitProxy => lastSnapshot?.Settings?.ExitProxy is ExitProxyKind.Psiphon or ExitProxyKind.OpenVpn;

    public BrokerConnectionState ConnectionState
    {
        get => connectionState;
        private set
        {
            if (SetProperty(ref connectionState, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsFaulted));
                RaiseActionStateChanged();
                UpdateLivePolling();
            }
        }
    }

    /// <summary>The engines run, but the watchdog cannot reach the internet through Paqet.</summary>
    public bool IsTunnelStalled
    {
        get => isTunnelStalled;
        private set => SetProperty(ref isTunnelStalled, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string StateLabel
    {
        get => stateLabel;
        private set => SetProperty(ref stateLabel, value);
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

    /// <summary>The exit proxy engine, or "Not used" when the profile has no exit.</summary>
    public string ExitStatusText
    {
        get => exitStatusText;
        private set => SetProperty(ref exitStatusText, value);
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
                if (value is not null && string.Equals(value, notice, StringComparison.Ordinal))
                {
                    // The same text is now reported as the error; show it once.
                    Notice = null;
                }
            }
        }
    }

    /// <summary>
    /// A lasting broker notice (hotspot share paused, no IPv6 on the server, recovery
    /// stopped retrying). Informational, never an error; once dismissed, the same text
    /// stays hidden until the broker clears it.
    /// </summary>
    public string? Notice
    {
        get => notice;
        private set
        {
            if (SetProperty(ref notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => Notice is not null;

    /// <summary>The broker's GeoIP/GeoSite data status, from the latest snapshot or routing data request.</summary>
    public RoutingDataStatus? RoutingData
    {
        get => routingData;
        private set => SetProperty(ref routingData, value);
    }

    public void DismissNotice()
    {
        dismissedNotice = notice;
        Notice = null;
    }

    /// <summary>The follow-up the current error offers (install software, copy diagnostics, ...).</summary>
    public FailureAction FailureAction
    {
        get => failureAction;
        private set
        {
            if (SetProperty(ref failureAction, value))
            {
                OnPropertyChanged(nameof(FailureActionText));
                OnPropertyChanged(nameof(HasFailureAction));
            }
        }
    }

    public bool HasFailureAction => FailureAction != FailureAction.None;

    public string FailureActionText => FailureAction switch
    {
        FailureAction.OpenRequiredSoftware => "Open required software",
        FailureAction.CopyDiagnostics => "Copy diagnostics",
        FailureAction.ReviewProfile => "Review profile",
        _ => string.Empty,
    };

    public ReadinessIssue Readiness
    {
        get => readiness;
        private set
        {
            if (SetProperty(ref readiness, value))
            {
                RaiseActionStateChanged();
            }
        }
    }

    public bool IsBrokerReachable
    {
        get => isBrokerReachable;
        private set
        {
            if (SetProperty(ref isBrokerReachable, value))
            {
                RaiseActionStateChanged();
            }
        }
    }

    public bool IsReconnecting
    {
        get => isReconnecting;
        private set => SetProperty(ref isReconnecting, value);
    }

    /// <summary>True when the last mutation was refused because another operation was still running.</summary>
    public bool LastRequestWasBusy { get; private set; }

    /// <summary>Whether the last <see cref="SaveSettingsAsync"/> stored the profile; see <see cref="ViewModels.SaveOutcome"/>.</summary>
    public SaveOutcome LastSaveOutcome { get; private set; }

    public ConnectionProgress? Progress
    {
        get => progress;
        private set
        {
            if (EqualityComparer<ConnectionProgress?>.Default.Equals(progress, value))
            {
                return;
            }

            var stageChanged = progress?.Stage != value?.Stage || progress?.Attempt != value?.Attempt;
            progress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProgress));
            if (stageChanged)
            {
                ProgressSteps.Clear();
                foreach (var step in ConnectionProgressText.CreateSteps(value, UsesExitProxy))
                {
                    ProgressSteps.Add(step);
                }
            }

            UpdateProgressSummary();
            UpdateProgressTicker();
            RaiseActionStateChanged();
        }
    }

    public bool HasProgress => Progress is not null;

    /// <summary>"Step 3 of 6 · Starting Paqet transport · 12 s", refreshed every second while connecting.</summary>
    public string ProgressSummary
    {
        get => progressSummary;
        private set => SetProperty(ref progressSummary, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                if (!value)
                {
                    lastUserOperationAt = time.GetUtcNow();
                }

                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanSaveAndConnect));
                RaiseActionStateChanged();
            }
        }
    }

    public bool IsConnected => ConnectionState == BrokerConnectionState.Connected;

    public bool IsFaulted => ConnectionState == BrokerConnectionState.Faulted;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool CanConnect =>
        !IsBusy && ConnectionState is
            BrokerConnectionState.Disconnected or
            BrokerConnectionState.Guarded or
            BrokerConnectionState.Degraded or
            BrokerConnectionState.Faulted;

    public bool CanDisconnect =>
        !IsBusy && ConnectionState is BrokerConnectionState.Connected or BrokerConnectionState.Degraded;

    /// <summary>A connect is running and can be stopped with a Disconnect request.</summary>
    public bool CanCancelConnect =>
        !cancelRequested &&
        ConnectionState == BrokerConnectionState.Connecting &&
        Progress?.Stage != ConnectionStage.RecoveringTransport;

    public bool CanVerify =>
        !IsBusy && ConnectionState is BrokerConnectionState.Connected or BrokerConnectionState.Degraded;

    public bool CanRefresh => !IsBusy;

    public bool CanSaveAndConnect => !IsBusy;

    public PrimaryConnectionAction PrimaryAction =>
        IsBusy || ConnectionState is BrokerConnectionState.Connecting or BrokerConnectionState.Disconnecting
            ? PrimaryConnectionAction.None
            : ConnectionState switch
            {
                BrokerConnectionState.Connected or BrokerConnectionState.Degraded => PrimaryConnectionAction.Disconnect,
                BrokerConnectionState.NotReady when Readiness == ReadinessIssue.ProfileRequired => PrimaryConnectionAction.SetUpProfile,
                BrokerConnectionState.NotReady when Readiness == ReadinessIssue.PrerequisiteMissing => PrimaryConnectionAction.OpenRequiredSoftware,
                BrokerConnectionState.NotReady => PrimaryConnectionAction.OpenDiagnostics,
                BrokerConnectionState.Faulted when !IsBrokerReachable => PrimaryConnectionAction.RetryBroker,
                _ => PrimaryConnectionAction.Connect,
            };

    public bool CanPrimaryAction => PrimaryAction != PrimaryConnectionAction.None;

    public string PrimaryActionText => PrimaryAction switch
    {
        PrimaryConnectionAction.None => ConnectionState switch
        {
            BrokerConnectionState.Connecting => "Connecting…",
            BrokerConnectionState.Disconnecting => "Disconnecting…",
            _ => "Working…",
        },
        PrimaryConnectionAction.Disconnect => "Disconnect",
        PrimaryConnectionAction.SetUpProfile => "Set up connection",
        PrimaryConnectionAction.OpenRequiredSoftware => "Install required software",
        PrimaryConnectionAction.OpenDiagnostics => "Open diagnostics",
        PrimaryConnectionAction.RetryBroker => "Retry",
        _ => ConnectionState switch
        {
            BrokerConnectionState.Guarded => "Reconnect",
            BrokerConnectionState.Faulted => "Retry connection",
            _ => "Connect",
        },
    };

    /// <summary>A second, quieter path to Diagnostics while Retry is the primary action.</summary>
    public bool ShowDiagnosticsShortcut => ConnectionState == BrokerConnectionState.Faulted && !IsBusy;

    /// <summary>Route transitions within a short window after a user action are expected, not news.</summary>
    public bool IsUserTransition => IsBusy || cancelRequested || time.GetUtcNow() - lastUserOperationAt < UserTransitionGrace;

    /// <summary>Stops the live-stats poll while the window is hidden; lifecycle state still arrives as events.</summary>
    public void SetLiveStatsPaused(bool paused)
    {
        if (liveStatsPaused == paused)
        {
            return;
        }

        liveStatsPaused = paused;
        UpdateLivePolling();
    }

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
                if (errorKind == ErrorKind.Transport)
                {
                    ClearError();
                }

                BrokerStatusText = "Checking";
                if (lastSnapshot is null || !IsBrokerReachable)
                {
                    // Only the first check replaces the status card; afterwards a refresh must
                    // not briefly turn an active route into "checking".
                    IsBusy = true;
                    StateLabel = "CHECKING SYSTEM";
                    StatusText = "Checking PaqetFire Broker…";
                    StatusDescription = "Reading engine and routing status.";
                }
            }).ConfigureAwait(false);

            try
            {
                await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
                var snapshot = await brokerClient.GetSnapshotAsync(StatusRequestTimeout, cancellationToken)
                    .ConfigureAwait(false);
                await UpdateUiAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            }
            catch (BrokerRequestException exception)
            {
                await UpdateUiAsync(() => ApplyRequestFailure(exception)).ConfigureAwait(false);
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
            EnsureHeartbeat();
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
                connectInFlight = true;
                cancelRequested = false;
                IsBusy = true;
                ClearError();
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

                await UpdateUiAsync(() =>
                {
                    connectInFlight = false;
                    ApplySnapshot(snapshot);
                }).ConfigureAwait(false);
            }
            catch (BrokerRequestException exception)
            {
                var wasCancelled = cancelRequested;
                var snapshot = await TryGetSnapshotAsync().ConfigureAwait(false);
                await UpdateUiAsync(() =>
                {
                    connectInFlight = false;
                    if (snapshot is not null)
                    {
                        ApplySnapshot(snapshot);
                    }

                    if (wasCancelled)
                    {
                        ActivityOccurred?.Invoke("Connection cancelled.");
                        return;
                    }

                    ApplyRequestFailure(exception);
                    if (ConnectionState is BrokerConnectionState.Disconnected or BrokerConnectionState.Degraded)
                    {
                        ConnectionState = BrokerConnectionState.Faulted;
                        StateLabel = "CONNECTION FAILED";
                        StatusText = "Could not connect";
                        StatusDescription = FailureAction switch
                        {
                            FailureAction.OpenRequiredSoftware => "Install the missing required software, then connect again.",
                            FailureAction.CopyDiagnostics => "Retry, or copy diagnostics to see what the engine reported.",
                            FailureAction.ReviewProfile => "Review the connection profile, then connect again.",
                            _ => "Retry the connection, or open Diagnostics for details.",
                        };
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await UpdateUiAsync(() =>
                {
                    connectInFlight = false;
                    ApplyTransportFailure(exception);
                }).ConfigureAwait(false);
            }
            finally
            {
                await UpdateUiAsync(() =>
                {
                    connectInFlight = false;
                    cancelRequested = false;
                    IsBusy = false;
                    RaiseActionStateChanged();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await TryUpdateUiAsync(() =>
            {
                connectInFlight = false;
                ConnectionState = BrokerConnectionState.Disconnected;
                StateLabel = "NOT CONNECTED";
                StatusText = "Connection cancelled";
                StatusDescription = "No routing change was completed.";
                ClearError();
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryUpdateUiAsync(() =>
            {
                connectInFlight = false;
                ApplyTransportFailure(exception);
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>
    /// Stops a connect in progress by asking the broker to disconnect. A token cancel would
    /// abandon the request mid-pipe and leave the broker finishing the connect anyway.
    /// </summary>
    public async Task CancelConnectAsync()
    {
        if (disposed || !CanCancelConnect)
        {
            return;
        }

        await UpdateUiAsync(() =>
        {
            cancelRequested = true;
            StatusText = "Cancelling…";
            StatusDescription = "PaqetFire is stopping the engines that already started.";
            ActivityOccurred?.Invoke("Connection cancel requested.");
            RaiseActionStateChanged();
        }).ConfigureAwait(false);

        try
        {
            await brokerClient.OpenAsync(ConnectTimeout, CancellationToken.None).ConfigureAwait(false);
            var snapshot = await brokerClient.SetConnectionStateAsync(
                    connected: false,
                    LifecycleRequestTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await UpdateUiAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
        }
        catch (BrokerRequestException exception)
        {
            await UpdateUiAsync(() => ApplyRequestFailure(exception)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
        }
        finally
        {
            await TryUpdateUiAsync(() =>
            {
                if (!connectInFlight)
                {
                    cancelRequested = false;
                }

                RaiseActionStateChanged();
            }).ConfigureAwait(false);
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
            LastRequestWasBusy = true;
            LastSaveOutcome = SaveOutcome.NotStored;
            return false;
        }
        try
        {
            LastSaveOutcome = SaveOutcome.None;
            var wasRouting = lastSnapshot?.IsRouting == true;
            await UpdateUiAsync(() =>
            {
                LastRequestWasBusy = false;
                connectInFlight = connectAfterSave;
                cancelRequested = false;
                IsBusy = true;
                ClearError();
                StateLabel = connectAfterSave ? "CONNECTING" : "SAVING PROFILE";
                StatusText = connectAfterSave ? "Applying configuration and connecting…" : "Applying configuration…";
                StatusDescription = "The broker is validating the profile and generating engine configuration.";
                if (connectAfterSave)
                {
                    ConnectionState = BrokerConnectionState.Connecting;
                }

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
                var stored = false;
                await UpdateUiAsync(() =>
                {
                    connectInFlight = false;
                    var cancelled = cancelRequested;
                    ApplySnapshot(snapshot);
                    // A success reply only proves the save if it carries the submitted profile;
                    // otherwise the form keeps its edits and secrets.
                    stored = SaveConfirmation.Reflects(settings, snapshot.Settings);
                    if (!stored)
                    {
                        LastSaveOutcome = cancelled ? SaveOutcome.Cancelled : SaveOutcome.NotStored;
                        SetError(
                            cancelled
                                ? "Save was cancelled — nothing was stored. Your changes are still pending."
                                : "The broker did not confirm that the profile was stored. Your changes are still pending; save again.",
                            ErrorKind.Operation,
                            FailureAction.None);
                        ActivityOccurred?.Invoke(cancelled ? "The save was cancelled before it was stored." : "The save was not confirmed by the broker.");
                        return;
                    }

                    LastSaveOutcome = cancelled ? SaveOutcome.SavedConnectionCancelled : SaveOutcome.Saved;
                    if (cancelled)
                    {
                        ActivityOccurred?.Invoke("The profile was saved; the connection was cancelled.");
                    }

                    // Only a route this save was meant to (re)start makes the warning a failure;
                    // otherwise it is a lasting notice, shown as such.
                    if (!string.IsNullOrWhiteSpace(snapshot.OperationWarning) && !cancelled &&
                        (connectAfterSave || wasRouting) && !snapshot.IsRouting)
                    {
                        SetError(snapshot.OperationWarning, ErrorKind.Operation, FailureAction.CopyDiagnostics);
                        ActivityOccurred?.Invoke("The profile was saved, but the connection did not start.");
                    }
                }).ConfigureAwait(false);
                return stored;
            }
            catch (BrokerRequestException exception)
            {
                await UpdateUiAsync(() =>
                {
                    var cancelled = cancelRequested;
                    LastSaveOutcome = cancelled ? SaveOutcome.Cancelled : SaveOutcome.NotStored;
                    connectInFlight = false;
                    if (lastSnapshot is { } previous)
                    {
                        // The broker kept its previous state; show that, not a guess.
                        ApplySnapshot(previous);
                    }
                    else
                    {
                        ConnectionState = BrokerConnectionState.NotReady;
                    }

                    if (cancelled)
                    {
                        StateLabel = "SAVE CANCELLED";
                        StatusText = "Save cancelled";
                        StatusDescription = "Nothing was stored; the broker kept the previous configuration.";
                        SetError("Save was cancelled — nothing was stored. Your changes are still pending.", ErrorKind.Operation, FailureAction.None);
                        ActivityOccurred?.Invoke("The save was cancelled before it was stored.");
                        return;
                    }

                    StateLabel = "PROFILE NOT APPLIED";
                    StatusText = "Configuration needs attention";
                    StatusDescription = "The broker kept the previous configuration.";
                    ApplyRequestFailure(exception);
                    ActivityOccurred?.Invoke("The broker rejected the configuration.");
                }).ConfigureAwait(false);
                return false;
            }
            catch (Exception exception)
            {
                await UpdateUiAsync(() =>
                {
                    LastSaveOutcome = SaveOutcome.NotStored;
                    connectInFlight = false;
                    ApplyTransportFailure(exception);
                }).ConfigureAwait(false);
                return false;
            }
            finally
            {
                await UpdateUiAsync(() =>
                {
                    connectInFlight = false;
                    cancelRequested = false;
                    IsBusy = false;
                    RaiseActionStateChanged();
                }).ConfigureAwait(false);
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
                ClearError();
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
            catch (BrokerRequestException exception)
            {
                var snapshot = await TryGetSnapshotAsync().ConfigureAwait(false);
                await UpdateUiAsync(() =>
                {
                    if (snapshot is not null)
                    {
                        ApplySnapshot(snapshot);
                    }

                    ApplyRequestFailure(exception);
                }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await UpdateUiAsync(() =>
                {
                    ConnectionState = BrokerConnectionState.Faulted;
                    StateLabel = "DISCONNECT INCOMPLETE";
                    StatusText = "Disconnect incomplete";
                    StatusDescription = "Check the broker status before reconnecting.";
                    SetError(GetFriendlyError(exception), ErrorKind.Transport, FailureAction.None);
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
                SetError(GetFriendlyError(exception), ErrorKind.Transport, FailureAction.None);
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<ConnectionVerificationReport?> VerifyConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return null;
        }

        if (!await operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            LastRequestWasBusy = true;
            return null;
        }

        try
        {
            await UpdateUiAsync(() =>
            {
                LastRequestWasBusy = false;
                IsBusy = true;
                ClearError();
                ActivityOccurred?.Invoke("Connection verification started.");
            }).ConfigureAwait(false);
            await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var snapshot = await brokerClient.VerifyConnectionAsync(LifecycleRequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            await UpdateUiAsync(() =>
            {
                ApplySnapshot(snapshot);
                ActivityOccurred?.Invoke(snapshot.Verification?.Summary ?? "Connection verification completed.");
            }).ConfigureAwait(false);
            return snapshot.Verification;
        }
        catch (BrokerRequestException exception)
        {
            await UpdateUiAsync(() => ApplyRequestFailure(exception)).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
            return null;
        }
        finally
        {
            await TryUpdateUiAsync(() => IsBusy = false).ConfigureAwait(false);
            operationLock.Release();
        }
    }

    public async Task<bool> ManageProfileAsync(
        ProfileAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (disposed)
        {
            return false;
        }

        if (!await operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            LastRequestWasBusy = true;
            return false;
        }

        try
        {
            var wasRouting = lastSnapshot?.IsRouting == true;
            await UpdateUiAsync(() =>
            {
                LastRequestWasBusy = false;
                IsBusy = true;
                ClearError();
            }).ConfigureAwait(false);
            await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var snapshot = await brokerClient.ManageProfilesAsync(
                    action,
                    LifecycleRequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            await UpdateUiAsync(() =>
            {
                ApplySnapshot(snapshot);
                // A profile switch that took the route down reports why; anything else is a notice.
                if (!string.IsNullOrWhiteSpace(snapshot.OperationWarning) && wasRouting && !snapshot.IsRouting)
                {
                    SetError(snapshot.OperationWarning, ErrorKind.Operation, FailureAction.CopyDiagnostics);
                }
            }).ConfigureAwait(false);
            return true;
        }
        catch (BrokerRequestException exception)
        {
            await UpdateUiAsync(() => ApplyRequestFailure(exception)).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception)
        {
            await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
            return false;
        }
        finally
        {
            await TryUpdateUiAsync(() => IsBusy = false).ConfigureAwait(false);
            operationLock.Release();
        }
    }

    /// <summary>
    /// Sends a routing data action. It is not serialized with route operations: an update
    /// runs in the broker's background and never touches the running engines.
    /// </summary>
    /// <returns>Null on success, otherwise a message for the Settings page.</returns>
    public async Task<string?> ManageRoutingDataAsync(
        RoutingDataAction action,
        CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return "PaqetFire is closing.";
        }

        try
        {
            await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var snapshot = await brokerClient.ManageRoutingDataAsync(action, LifecycleRequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            await UpdateUiAsync(() => RoutingData = snapshot.RoutingData ?? RoutingData).ConfigureAwait(false);
            return null;
        }
        catch (BrokerRequestException exception)
        {
            return GetBrokerErrorMessage(exception.Error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return GetFriendlyError(exception);
        }
    }

    /// <summary>
    /// Downloads, imports, or removes an exit engine. Like routing data, it is not serialized
    /// with route operations; a download runs in the broker's background.
    /// </summary>
    /// <returns>Null on success, otherwise a message for the exit proxy card.</returns>
    public async Task<string?> ManageExitEngineAsync(
        ExitEngineAction action,
        CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return "PaqetFire is closing.";
        }

        try
        {
            await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var snapshot = await brokerClient.ManageExitEngineAsync(action, LifecycleRequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            await UpdateUiAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
            return null;
        }
        catch (BrokerRequestException exception)
        {
            return GetBrokerErrorMessage(exception.Error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return GetFriendlyError(exception);
        }
    }

    private static string ExitName(EngineKind engine) => engine == EngineKind.OpenVpn ? "OpenVPN" : engine.ToString();

    public async Task<string?> ExportProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return null;
        }

        if (!await operationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            LastRequestWasBusy = true;
            return null;
        }

        try
        {
            await UpdateUiAsync(() =>
            {
                LastRequestWasBusy = false;
                IsBusy = true;
                ClearError();
            }).ConfigureAwait(false);
            await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            return await brokerClient.ExportProfilesAsync(
                    LifecycleRequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BrokerRequestException exception)
        {
            await UpdateUiAsync(() => ApplyRequestFailure(exception)).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
            return null;
        }
        finally
        {
            await TryUpdateUiAsync(() => IsBusy = false).ConfigureAwait(false);
            operationLock.Release();
        }
    }

    public async Task<string?> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            return await brokerClient.GetDiagnosticsAsync(LifecycleRequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerRequestException exception)
        {
            await UpdateUiAsync(() => ApplyRequestFailure(exception)).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            await UpdateUiAsync(() => ApplyTransportFailure(exception)).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Reads recorded data usage, first clearing it when <paramref name="reset"/> is given.
    /// Failures are returned for the usage page to show; they are not connection errors.
    /// </summary>
    public async Task<DataUsageResult> GetDataUsageAsync(DataUsageReset? reset = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await brokerClient.OpenAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var report = reset is null
                ? await brokerClient.GetDataUsageAsync(StatusRequestTimeout, cancellationToken).ConfigureAwait(false)
                : await brokerClient.ResetDataUsageAsync(reset, StatusRequestTimeout, cancellationToken).ConfigureAwait(false);
            return new DataUsageResult(report, null);
        }
        catch (BrokerRequestException exception)
        {
            return new DataUsageResult(null, exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new DataUsageResult(null, "PaqetFire could not reach its broker service to read data usage.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        // Stop background loops first so none of them is holding the pipe.
        lifetime.Cancel();
        await operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            livePolling?.Cancel();
            livePolling = null;
            progressTicker?.Cancel();
            progressTicker = null;
            brokerClient.EventReceived -= OnBrokerEventReceived;
            brokerClient.ConnectionLost -= OnConnectionLost;
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
            SetError(
                GetBrokerErrorMessage(brokerEvent.Error),
                ErrorKind.Operation,
                GetFailureAction(brokerEvent.Error?.Code));
        }
    }

    private void OnConnectionLost()
    {
        if (disposed)
        {
            return;
        }

        reconnectRequested = true;
        if (Interlocked.CompareExchange(ref reconnectRunning, 1, 0) == 0)
        {
            _ = ReconnectLoopAsync();
        }
    }

    /// <summary>Reopens the pipe with backoff after the broker went away, then resyncs from a snapshot.</summary>
    private async Task ReconnectLoopAsync()
    {
        var token = CancellationToken.None;
        try
        {
            token = lifetime.Token;
            await TryUpdateUiAsync(() =>
            {
                IsReconnecting = true;
                BrokerStatusText = "Reconnecting";
                StateLabel = "RECONNECTING";
                StatusText = "Reconnecting to broker…";
                StatusDescription = "The app lost contact with the PaqetFire Broker service. The service keeps managing the route meanwhile.";
                ActivityOccurred?.Invoke("Lost contact with the broker; reconnecting.");
            }).ConfigureAwait(false);

            var backoff = options.ReconnectBackoff.Count > 0 ? options.ReconnectBackoff : [TimeSpan.FromSeconds(5)];
            var attempt = 0;
            while (!disposed && !token.IsCancellationRequested)
            {
                reconnectRequested = false;
                await Task.Delay(backoff[Math.Min(attempt, backoff.Count - 1)], time, token).ConfigureAwait(false);
                attempt++;
                try
                {
                    await brokerClient.OpenAsync(ConnectTimeout, token).ConfigureAwait(false);
                    var snapshot = await brokerClient.GetSnapshotAsync(StatusRequestTimeout, token).ConfigureAwait(false);
                    await UpdateUiAsync(() =>
                    {
                        IsReconnecting = false;
                        ApplySnapshot(snapshot);
                        ActivityOccurred?.Invoke("Reconnected to the broker.");
                    }).ConfigureAwait(false);
                    if (!reconnectRequested)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    if (attempt == backoff.Count)
                    {
                        // Keep retrying at the capped interval, but stop claiming it is a blip.
                        await TryUpdateUiAsync(() =>
                        {
                            ApplyTransportFailure(exception);
                            IsReconnecting = true;
                            BrokerStatusText = "Reconnecting";
                        }).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The view model is shutting down.
        }
        catch (ObjectDisposedException)
        {
            // The view model was disposed during a retry.
        }
        finally
        {
            Interlocked.Exchange(ref reconnectRunning, 0);
            if (reconnectRequested && !disposed && !token.IsCancellationRequested)
            {
                OnConnectionLost();
            }
        }
    }

    private void EnsureHeartbeat()
    {
        // Refresh can complete on any thread; exactly one loop may ever start.
        if (disposed || options.HeartbeatInterval <= TimeSpan.Zero ||
            Interlocked.CompareExchange(ref heartbeatStarted, 1, 0) != 0)
        {
            return;
        }

        _ = HeartbeatLoopAsync(lifetime.Token);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.HeartbeatInterval, time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref reconnectRunning) == 1)
                {
                    continue;
                }

                try
                {
                    var snapshot = await brokerClient.GetSnapshotAsync(StatusRequestTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    await TryUpdateUiAsync(() =>
                    {
                        // A user operation reports its own result; do not overwrite its labels.
                        if (!IsBusy)
                        {
                            ApplySnapshot(snapshot);
                        }
                    }).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    // The pipe is closed or was never opened (broker started after the app).
                    OnConnectionLost();
                }
                catch (Exception exception) when (
                    exception is TimeoutException or BrokerRequestException or InvalidDataException)
                {
                    // A slow or busy broker is not a lost connection.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown.
        }
    }

    private async Task<BrokerSnapshot?> TryGetSnapshotAsync()
    {
        try
        {
            return await brokerClient.GetSnapshotAsync(StatusRequestTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void ApplySnapshot(BrokerSnapshot snapshot)
    {
        lastSnapshot = snapshot;
        IsBrokerReachable = true;
        IsReconnecting = false;
        if (errorKind == ErrorKind.Transport)
        {
            ClearError();
        }

        var runningCount = snapshot.Engines.Count(engine => engine.State == EngineState.Running);
        var fullyRouted = snapshot.IsRouting && runningCount == snapshot.Engines.Count;
        if (errorKind == ErrorKind.Operation && fullyRouted && snapshot.Progress is null &&
            snapshot.Tunnel?.State is not (TunnelHealthState.Stalled or TunnelHealthState.Recovering))
        {
            // A working route supersedes an earlier failure report.
            ClearError();
        }

        // While Paqet alone restarts, the broker no longer reports the chain as routing.
        IsTunnelStalled = snapshot.Progress?.Stage == ConnectionStage.RecoveringTransport ||
            (snapshot.IsRouting && snapshot.Tunnel?.State is TunnelHealthState.Stalled or TunnelHealthState.Recovering);
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
        var exit = snapshot.Engines.FirstOrDefault(engine => engine.Engine.IsExit());
        ExitStatusText = exit is not null
            ? $"{ExitName(exit.Engine)} · {EngineStateText.GetDisplayName(exit.State)}"
            : snapshot.Settings?.ExitProxy is ExitProxyKind.Psiphon or ExitProxyKind.OpenVpn ? "Stopped" : "Not used";
        RoutingStatusText = snapshot.IsRouting
            ? $"{RoutingScopeText.Create(snapshot.Settings)} · loop exclusions locked"
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
        Live.ApplyProfile(snapshot.Settings);
        RoutingData = snapshot.RoutingData;
        RefreshNotice();
        var previousState = ConnectionState;
        Progress = snapshot.Progress;
        var engineStates = string.Join(", ", snapshot.Engines.Select(engine => $"{engine.Engine} {engine.State}"));
        var activitySummary = $"{snapshot.ConnectionState}:{engineStates}:{snapshot.StatusMessage}";
        if (!string.Equals(lastActivitySummary, activitySummary, StringComparison.Ordinal))
        {
            lastActivitySummary = activitySummary;
            ActivityOccurred?.Invoke($"Status changed: {engineSummary.ToLowerInvariant()} · {engineStates}.");
        }

        var missingPrerequisites = snapshot.Prerequisites?.Where(item => !item.IsInstalled).ToArray() ?? [];
        if (missingPrerequisites.Length > 0)
        {
            Readiness = ReadinessIssue.PrerequisiteMissing;
            ConnectionState = BrokerConnectionState.NotReady;
            StateLabel = "PREREQUISITE REQUIRED";
            StatusText = missingPrerequisites.Length == 1
                ? $"Install {missingPrerequisites[0].DisplayName}"
                : $"Install {missingPrerequisites.Length} prerequisites";
            StatusDescription = snapshot.StatusMessage ?? "A required packet-capture component is missing.";
        }
        else if (snapshot.Engines.Any(engine => engine.State == EngineState.NotInstalled))
        {
            Readiness = ReadinessIssue.RepairRequired;
            ConnectionState = BrokerConnectionState.NotReady;
            StateLabel = "REPAIR REQUIRED";
            StatusText = "Bundled engine files are unavailable";
            StatusDescription = snapshot.StatusMessage ?? "Repair the PaqetFire installation, then refresh.";
        }
        else if (!snapshot.IsConfigured)
        {
            Readiness = ReadinessIssue.ProfileRequired;
            ConnectionState = BrokerConnectionState.NotReady;
            StateLabel = "PROFILE REQUIRED";
            StatusText = "Add your Paqet server";
            StatusDescription = snapshot.StatusMessage ?? "Open Paqet connection, enter the server and transport key, then save.";
        }
        else
        {
            Readiness = ReadinessIssue.None;
            ApplyRouteState(snapshot, runningCount, engineSummary, previousState);
        }

        SnapshotReceived?.Invoke(snapshot);
    }

    private void RefreshNotice()
    {
        var warning = string.IsNullOrWhiteSpace(lastSnapshot?.OperationWarning) ? null : lastSnapshot.OperationWarning;
        if (warning is null)
        {
            dismissedNotice = null;
        }

        // An operation that failed reports the same text as its error; show it once.
        Notice = warning is not null &&
                 !string.Equals(warning, dismissedNotice, StringComparison.Ordinal) &&
                 !string.Equals(warning, ErrorMessage, StringComparison.Ordinal)
            ? warning
            : null;
    }

    private void ApplyRouteState(
        BrokerSnapshot snapshot,
        int runningCount,
        string engineSummary,
        BrokerConnectionState previousState)
    {
        if (snapshot.Progress is { Stage: ConnectionStage.RecoveringTransport } recovery)
        {
            // The user still has a route; Paqet alone is restarting. Disconnect stays the
            // action on offer, not a connect's Cancel.
            ConnectionState = BrokerConnectionState.Connected;
            StateLabel = "RECONNECTING TRANSPORT";
            StatusText = ConnectionProgressText.GetStageLabel(recovery.Stage, recovery.Attempt);
            StatusDescription = "PaqetFire lost the Paqet server and is restarting the transport. Routed traffic waits on the route meanwhile.";
        }
        else if (snapshot.Progress is { Stage: ConnectionStage.ProbingTunnel } && snapshot.IsRouting &&
                 previousState == BrokerConnectionState.Connected && !connectInFlight)
        {
            // The tunnel test after a transport restart, not a new connection.
            ConnectionState = BrokerConnectionState.Connected;
            StateLabel = "RECONNECTING TRANSPORT";
            StatusText = $"{ConnectionProgressText.GetStageLabel(ConnectionStage.ProbingTunnel)}…";
            StatusDescription = "The Paqet transport restarted; PaqetFire is confirming that traffic passes again.";
        }
        else if (snapshot.Progress is { } connecting)
        {
            ConnectionState = BrokerConnectionState.Connecting;
            StateLabel = "CONNECTING";
            StatusText = cancelRequested
                ? "Cancelling…"
                : $"{ConnectionProgressText.GetStageLabel(connecting.Stage, connecting.Attempt)}…";
            StatusDescription = "Paqet starts first, then Xray policy routing, then ProxiFyre application routing.";
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
            if (!HasError)
            {
                SetError(
                    snapshot.StatusMessage ?? "An engine stopped unexpectedly. Copy diagnostics to see what it reported, then retry.",
                    ErrorKind.Operation,
                    FailureAction.CopyDiagnostics);
            }
            else if (FailureAction == FailureAction.None)
            {
                FailureAction = FailureAction.CopyDiagnostics;
            }
        }
        else if (snapshot.Engines.Any(engine => engine.State == EngineState.Starting) ||
                 (connectInFlight && !snapshot.IsRouting))
        {
            // While our own connect request runs, an intermediate "nothing running yet"
            // snapshot must not flip the card back to "not connected".
            ConnectionState = BrokerConnectionState.Connecting;
            StateLabel = "CONNECTING";
            StatusText = cancelRequested ? "Cancelling…" : $"Connecting · {engineSummary}";
            StatusDescription = "Waiting for Paqet, Xray, and ProxiFyre to report stable running states.";
        }
        else if (snapshot.Engines.Any(engine => engine.State == EngineState.Stopping))
        {
            ConnectionState = BrokerConnectionState.Disconnecting;
            StateLabel = "DISCONNECTING";
            StatusText = $"Disconnecting · {engineSummary}";
            StatusDescription = "Stopping application routing before the carrier.";
        }
        else if (snapshot.IsRouting && runningCount == snapshot.Engines.Count &&
                 snapshot.Tunnel?.State is TunnelHealthState.Stalled or TunnelHealthState.Recovering)
        {
            ConnectionState = BrokerConnectionState.Connected;
            StateLabel = "TUNNEL NOT RESPONDING";
            StatusText = snapshot.Tunnel.State == TunnelHealthState.Recovering
                ? "Restarting the Paqet transport"
                : "Waiting for the Paqet server";
            StatusDescription = snapshot.Tunnel.LastSuccessAt is { } lastSuccess
                ? $"No traffic has passed through Paqet since {lastSuccess.ToLocalTime():HH:mm:ss}. PaqetFire restarts the transport automatically; routed traffic stays on the route meanwhile."
                : "Paqet is running but its server has not answered yet. PaqetFire restarts the transport automatically; routed traffic stays on the route meanwhile.";
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

    private void UpdateLivePolling()
    {
        if (IsConnected && !disposed && !liveStatsPaused)
        {
            if (livePolling is null)
            {
                livePolling = new CancellationTokenSource();
                _ = PollLiveStatsAsync(livePolling.Token);
            }

            return;
        }

        livePolling?.Cancel();
        livePolling = null;
        if (!IsConnected)
        {
            Live.Reset();
        }
    }

    private async Task PollLiveStatsAsync(CancellationToken cancellationToken)
    {
        // Not serialized with user operations: the pipe correlates replies by request ID and
        // serializes writes, and a timed-out stats request no longer resets the pipe.
        using var timer = new PeriodicTimer(options.LiveStatsInterval, time);
        try
        {
            do
            {
                if (cancellationToken.IsCancellationRequested || Volatile.Read(ref reconnectRunning) == 1)
                {
                    continue;
                }

                LiveConnectionStats? stats = null;
                try
                {
                    stats = await brokerClient.GetLiveStatsAsync(StatusRequestTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    OnConnectionLost();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Live telemetry is best-effort; lifecycle state comes from snapshots.
                }

                if (stats is not null)
                {
                    _ = dispatcherQueue.TryEnqueue(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            Live.Apply(stats);
                        }
                    });
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Polling stops when the route leaves the connected state.
        }
        catch (ObjectDisposedException)
        {
            // The view model was disposed while a tick was pending.
        }
    }

    private void UpdateProgressSummary() =>
        ProgressSummary = ConnectionProgressText.CreateSummary(Progress, time.GetUtcNow(), UsesExitProxy);

    private void UpdateProgressTicker()
    {
        if (Progress is not null && !disposed)
        {
            if (progressTicker is null)
            {
                progressTicker = new CancellationTokenSource();
                _ = TickProgressAsync(progressTicker.Token);
            }

            return;
        }

        progressTicker?.Cancel();
        progressTicker = null;
    }

    private async Task TickProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = dispatcherQueue.TryEnqueue(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        UpdateProgressSummary();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The operation finished.
        }
    }

    private void ApplyRequestFailure(BrokerRequestException exception)
    {
        // The broker answered, so it is running; its message is written for users.
        IsBrokerReachable = true;
        BrokerStatusText = "Running";
        SetError(GetBrokerErrorMessage(exception.Error), ErrorKind.Operation, GetFailureAction(exception.Error.Code));
        ActivityOccurred?.Invoke($"The broker refused the request: {exception.Error.Code}.");
    }

    private void ApplyTransportFailure(Exception exception)
    {
        IsBrokerReachable = false;
        ConnectionState = BrokerConnectionState.Faulted;
        StateLabel = "BROKER UNAVAILABLE";
        StatusText = "Broker unavailable";
        StatusDescription = "Install or start the PaqetFire Broker service, then retry.";
        PaqetStatusText = "Unknown";
        RoutingStatusText = "Unknown";
        PaqetDetailText = "No engine status is available while the broker is unreachable.";
        RoutingDetailText = "No routing status is available while the broker is unreachable.";
        BrokerStatusText = "Unavailable";
        LastCheckedText = $"Failed {DateTimeOffset.Now:t}";
        Progress = null;
        SetError(GetFriendlyError(exception), ErrorKind.Transport, FailureAction.None);
        ActivityOccurred?.Invoke("Could not contact the broker.");
    }

    private void SetError(string message, ErrorKind kind, FailureAction action)
    {
        errorKind = kind;
        ErrorMessage = message;
        FailureAction = action;
    }

    private void ClearError()
    {
        errorKind = ErrorKind.None;
        ErrorMessage = null;
        FailureAction = FailureAction.None;
    }

    private void RaiseActionStateChanged()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanCancelConnect));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(PrimaryAction));
        OnPropertyChanged(nameof(CanPrimaryAction));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ShowDiagnosticsShortcut));
    }

    private static string CreateEffectiveRoutingText(PaqetFireSettingsView? settings)
    {
        if (settings is null || (!settings.HasTransportKey && PaqetConfigurationValidator.RequiresTransportKey(settings.Advanced.KcpBlock)))
        {
            return "Set up a profile to preview which traffic will use PaqetFire.";
        }

        var scope = RoutingScopeText.Create(settings);
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

    private static FailureAction GetFailureAction(BrokerErrorCode? code) => code switch
    {
        BrokerErrorCode.PrerequisiteMissing => FailureAction.OpenRequiredSoftware,
        BrokerErrorCode.EngineFailure => FailureAction.CopyDiagnostics,
        BrokerErrorCode.ConfigurationInvalid => FailureAction.ReviewProfile,
        BrokerErrorCode.InternalError => FailureAction.CopyDiagnostics,
        _ => FailureAction.None,
    };

    /// <summary>The broker's own message for errors it writes for users; generic text for protocol problems.</summary>
    public static string GetBrokerErrorMessage(BrokerError? error) => error switch
    {
        { Code: BrokerErrorCode.EngineFailure or BrokerErrorCode.ConfigurationInvalid or
            BrokerErrorCode.PrerequisiteMissing or BrokerErrorCode.Busy } when !string.IsNullOrWhiteSpace(error.Message)
            => error.Message,
        _ => GetFriendlyBrokerError(error?.Code),
    };

    private static string GetFriendlyError(Exception exception) => exception switch
    {
        TimeoutException => "The broker did not respond in time.",
        OperationCanceledException => "The operation was cancelled.",
        UnauthorizedAccessException => "PaqetFire does not have permission to contact the broker.",
        InvalidDataException => "The broker returned an incompatible response.",
        BrokerRequestException requestException => GetBrokerErrorMessage(requestException.Error),
        IOException => "The broker connection was interrupted.",
        InvalidOperationException => "The broker connection is not ready.",
        _ => "PaqetFire could not contact the broker.",
    };

    private static string GetFriendlyBrokerError(BrokerErrorCode? code) => code switch
    {
        BrokerErrorCode.IncompatibleProtocol => "The app and broker versions are incompatible. Reinstall or update PaqetFire.",
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
