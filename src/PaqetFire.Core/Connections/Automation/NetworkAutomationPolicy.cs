namespace PaqetFire.Core.Connections.Automation;

/// <summary>
/// Converts normalized network and route observations into bounded automation
/// decisions. Calls must be serialized and timestamped in non-decreasing order.
/// Platform event subscriptions and execution of returned actions belong to the
/// caller. After a returned Connect action fails, the caller must submit exactly
/// one ConnectionAttemptFailed observation so bounded backoff can advance.
/// </summary>
public sealed class NetworkAutomationPolicy
{
    private readonly NetworkAutomationPolicyOptions _options;
    private DateTimeOffset? _lastObservedAt;
    private string? _lastNetworkId;
    private bool _hasObservedNetwork;
    private bool _captivePortalActive;
    private DateTimeOffset? _manualPauseUntil;
    private DateTimeOffset? _retryNotBefore;
    private int _failedAttempts;
    private bool _connectPending;
    private NetworkAutomationReason _pendingConnectReason;
    private bool _disconnectPending;
    private bool _restoreRoute;
    private NetworkAutomationReason _restoreReason = NetworkAutomationReason.RestoreAfterFailure;

    public NetworkAutomationPolicy(NetworkAutomationPolicyOptions? options = null)
    {
        _options = options ?? new NetworkAutomationPolicyOptions();
        ValidateOptions(_options);
    }

    /// <summary>
    /// Suppresses automated connection attempts until the duration elapses.
    /// This does not disconnect an established route.
    /// </summary>
    public void PauseAutoConnect(DateTimeOffset now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The pause duration must be positive.");
        }

        EnsureTimestampOrder(now);
        _connectPending = false;
        _manualPauseUntil = AddClamped(now, duration);
    }

    public void ResumeAutoConnect() => _manualPauseUntil = null;

    public NetworkAutomationDecision Evaluate(NetworkAutomationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Engines);
        ArgumentNullException.ThrowIfNull(observation.Settings);
        EnsureTimestampOrder(observation.Timestamp);
        ValidateNetwork(observation.Network);
        ValidateSettings(observation.Settings);

        var now = observation.Timestamp;
        var routeWasActive = IsRouteActive(observation.ConnectionState);
        var networkChanged = HasNetworkChanged(observation.Network);
        var explicitNetworkChange = observation.Trigger == NetworkAutomationTrigger.NetworkChanged;
        var resumed = observation.Trigger == NetworkAutomationTrigger.Resumed;
        var captivePortalNow = observation.Network?.Connectivity == NetworkConnectivity.CaptivePortal;
        var captivePortalDetected = captivePortalNow && !_captivePortalActive;
        var captivePortalCleared = !captivePortalNow && _captivePortalActive;

        RememberNetwork(observation.Network);
        _captivePortalActive = captivePortalNow;

        var topologyChanged = networkChanged || explicitNetworkChange;
        if (topologyChanged)
        {
            BeginNewNetworkEpisode();
            DelayUntil(now, _options.NetworkSettleDelay);
        }

        if (resumed)
        {
            _connectPending = false;
            _disconnectPending = false;
            DelayUntil(now, _options.ResumeSettleDelay);

        }

        if ((topologyChanged || resumed) && routeWasActive)
        {
            var restoreReason = resumed
                ? NetworkAutomationReason.RestoreAfterResume
                : NetworkAutomationReason.RestoreAfterNetworkChange;
            var disconnectReason = resumed
                ? NetworkAutomationReason.ComputerResumed
                : NetworkAutomationReason.NetworkChanged;
            RequestRestore(restoreReason);
            return RequestDisconnect(disconnectReason);
        }

        if (captivePortalDetected && routeWasActive && observation.IsKillSwitchEnabled)
        {
            // A guarded disconnect would still block the portal, while fully stopping
            // capture would weaken an explicit fail-closed choice. Keep the route in
            // place and require the user to pause/disable protection deliberately.
            return None(NetworkAutomationReason.CaptivePortal);
        }

        if (captivePortalDetected && routeWasActive)
        {
            _connectPending = false;
            RequestRestore(NetworkAutomationReason.RestoreAfterCaptivePortal);
            return RequestDisconnect(NetworkAutomationReason.CaptivePortalDetected);
        }

        if (captivePortalDetected)
        {
            _connectPending = false;
        }

        if (captivePortalCleared)
        {
            DelayUntil(now, _options.CaptivePortalGrace);
            if (_restoreRoute)
            {
                _restoreReason = NetworkAutomationReason.RestoreAfterCaptivePortal;
            }
        }

        if (observation.Trigger == NetworkAutomationTrigger.ConnectionAttemptFailed)
        {
            var failedRestore = _connectPending && IsRestoreReason(_pendingConnectReason);
            _connectPending = false;
            _failedAttempts++;
            if (failedRestore)
            {
                RequestRestore(NetworkAutomationReason.RestoreAfterFailure);
            }

            if (_failedAttempts < _options.MaximumReconnectAttempts)
            {
                DelayUntil(now, ReconnectBackoff(_failedAttempts));
            }
        }

        if (ConnectionRecoveryPolicy.RequiresSafeRecovery(
                observation.ConnectionState,
                observation.IsKillSwitchEnabled,
                observation.Engines))
        {
            _connectPending = false;
            RequestRestore(NetworkAutomationReason.RestoreAfterFailure);
            return RequestDisconnect(NetworkAutomationReason.SafeRecoveryRequired);
        }

        if (observation.ConnectionState == ConnectionState.Connected)
        {
            ResetAfterSuccessfulConnection();
            return None(NetworkAutomationReason.AlreadyConnected);
        }

        if (observation.ConnectionState == ConnectionState.NotReady)
        {
            _connectPending = false;
            return None(NetworkAutomationReason.ConnectionNotReady);
        }

        if (observation.ConnectionState is ConnectionState.Connecting or ConnectionState.Disconnecting)
        {
            return None(NetworkAutomationReason.ConnectionTransitionInProgress);
        }

        if (observation.ConnectionState is ConnectionState.Disconnected or ConnectionState.Guarded)
        {
            _disconnectPending = false;
        }

        if (_disconnectPending)
        {
            return None(NetworkAutomationReason.ConnectionTransitionInProgress);
        }

        if (_manualPauseUntil is { } pauseUntil)
        {
            if (now < pauseUntil)
            {
                return None(NetworkAutomationReason.ManualPause, pauseUntil);
            }

            _manualPauseUntil = null;
        }

        if (observation.Network is null ||
            observation.Network.Connectivity is NetworkConnectivity.Offline or NetworkConnectivity.LocalOnly)
        {
            _connectPending = false;
            return None(NetworkAutomationReason.NetworkUnavailable);
        }

        if (captivePortalNow)
        {
            return None(NetworkAutomationReason.CaptivePortal);
        }

        if (observation.Network.IsHostingHotspot && !observation.IsHotspotShareEnabled)
        {
            _connectPending = false;
            return None(NetworkAutomationReason.HotspotHostingInterlock);
        }

        var rule = FindRule(observation.Settings, observation.Network.NetworkId);
        var automaticConnectionAllowed = observation.Settings.IsEnabled &&
                                         (rule?.IsTrusted != true || observation.Settings.ConnectOnTrustedNetworks);

        if (!_restoreRoute && !automaticConnectionAllowed)
        {
            return None(
                !observation.Settings.IsEnabled
                    ? NetworkAutomationReason.AutomationDisabled
                    : NetworkAutomationReason.TrustedNetwork);
        }

        if (_failedAttempts >= _options.MaximumReconnectAttempts)
        {
            return None(NetworkAutomationReason.RetryLimitReached);
        }

        if (_retryNotBefore is { } retryAt && now < retryAt)
        {
            var reason = captivePortalCleared
                ? NetworkAutomationReason.CaptivePortalGrace
                : observation.Trigger == NetworkAutomationTrigger.Resumed
                    ? NetworkAutomationReason.ResumeSettleDelay
                    : networkChanged || explicitNetworkChange
                        ? NetworkAutomationReason.NetworkSettleDelay
                        : _failedAttempts > 0
                            ? NetworkAutomationReason.RetryBackoff
                            : _restoreReason switch
                            {
                                NetworkAutomationReason.RestoreAfterResume => NetworkAutomationReason.ResumeSettleDelay,
                                NetworkAutomationReason.RestoreAfterCaptivePortal => NetworkAutomationReason.CaptivePortalGrace,
                                _ => NetworkAutomationReason.NetworkSettleDelay,
                            };
            return None(reason, retryAt);
        }

        if (_connectPending)
        {
            return None(NetworkAutomationReason.ConnectAlreadyRequested);
        }

        _connectPending = true;
        var reasonForConnect = _restoreRoute
            ? _restoreReason
            : rule?.IsTrusted == true
                ? NetworkAutomationReason.AutoConnectTrustedNetwork
                : NetworkAutomationReason.AutoConnectUntrustedNetwork;
        _pendingConnectReason = reasonForConnect;

        return new NetworkAutomationDecision(
            NetworkAutomationAction.Connect,
            reasonForConnect,
            rule?.ProfileId ?? observation.Settings.DefaultProfileId,
            AttemptNumber: _failedAttempts + 1);
    }

    private NetworkAutomationDecision RequestDisconnect(NetworkAutomationReason reason)
    {
        if (_disconnectPending)
        {
            return None(NetworkAutomationReason.ConnectionTransitionInProgress);
        }

        _disconnectPending = true;
        return new NetworkAutomationDecision(NetworkAutomationAction.Disconnect, reason);
    }

    private void RequestRestore(NetworkAutomationReason reason)
    {
        _restoreRoute = true;
        _restoreReason = reason;
    }

    private void BeginNewNetworkEpisode()
    {
        _failedAttempts = 0;
        _connectPending = false;
        _disconnectPending = false;
        _retryNotBefore = null;
    }

    private void ResetAfterSuccessfulConnection()
    {
        _failedAttempts = 0;
        _connectPending = false;
        _disconnectPending = false;
        _restoreRoute = false;
        _retryNotBefore = null;
    }

    private bool HasNetworkChanged(NetworkContext? network)
    {
        if (!_hasObservedNetwork)
        {
            return false;
        }

        return !string.Equals(_lastNetworkId, network?.NetworkId, StringComparison.Ordinal);
    }

    private void RememberNetwork(NetworkContext? network)
    {
        _hasObservedNetwork = true;
        _lastNetworkId = network?.NetworkId;
    }

    private void DelayUntil(DateTimeOffset now, TimeSpan delay)
    {
        var candidate = AddClamped(now, delay);
        if (_retryNotBefore is null || candidate > _retryNotBefore)
        {
            _retryNotBefore = candidate;
        }
    }

    private TimeSpan ReconnectBackoff(int failedAttempts)
    {
        var exponent = Math.Min(failedAttempts - 1, 30);
        var multiplier = 1L << exponent;
        var initialTicks = _options.InitialReconnectBackoff.Ticks;
        var maximumTicks = _options.MaximumReconnectBackoff.Ticks;
        var ticks = initialTicks > maximumTicks / multiplier
            ? maximumTicks
            : Math.Min(initialTicks * multiplier, maximumTicks);
        return TimeSpan.FromTicks(ticks);
    }

    private void EnsureTimestampOrder(DateTimeOffset timestamp)
    {
        if (_lastObservedAt is { } last && timestamp < last)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                "Network automation observations must be timestamped in non-decreasing order.");
        }

        _lastObservedAt = timestamp;
    }

    private static NetworkAutomationRule? FindRule(NetworkAutomationSettings settings, string networkId) =>
        settings.NetworkRules.FirstOrDefault(rule =>
            string.Equals(rule.NetworkId, networkId, StringComparison.Ordinal));

    private static bool IsRouteActive(ConnectionState state) =>
        state is ConnectionState.Connecting or
            ConnectionState.Connected or
            ConnectionState.Degraded or
            ConnectionState.Faulted;

    private static bool IsRestoreReason(NetworkAutomationReason reason) =>
        reason is NetworkAutomationReason.RestoreAfterNetworkChange or
            NetworkAutomationReason.RestoreAfterResume or
            NetworkAutomationReason.RestoreAfterCaptivePortal or
            NetworkAutomationReason.RestoreAfterFailure;

    private static NetworkAutomationDecision None(
        NetworkAutomationReason reason,
        DateTimeOffset? retryAt = null) =>
        new(NetworkAutomationAction.None, reason, RetryAt: retryAt);

    private static DateTimeOffset AddClamped(DateTimeOffset timestamp, TimeSpan duration)
    {
        var maximum = DateTimeOffset.MaxValue - timestamp;
        return duration >= maximum ? DateTimeOffset.MaxValue : timestamp + duration;
    }

    private static void ValidateOptions(NetworkAutomationPolicyOptions options)
    {
        ValidateNonNegative(options.NetworkSettleDelay, nameof(options.NetworkSettleDelay));
        ValidateNonNegative(options.ResumeSettleDelay, nameof(options.ResumeSettleDelay));
        ValidateNonNegative(options.CaptivePortalGrace, nameof(options.CaptivePortalGrace));

        if (options.InitialReconnectBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.InitialReconnectBackoff),
                "The initial reconnect backoff must be positive.");
        }

        if (options.MaximumReconnectBackoff < options.InitialReconnectBackoff)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaximumReconnectBackoff),
                "The maximum reconnect backoff cannot be shorter than the initial backoff.");
        }

        if (options.MaximumReconnectAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaximumReconnectAttempts),
                "At least one automated connect attempt must be allowed.");
        }
    }

    private static void ValidateSettings(NetworkAutomationSettings settings)
    {
        var duplicate = settings.NetworkRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.NetworkId))
            .GroupBy(rule => rule.NetworkId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Network automation contains more than one rule for '{duplicate.Key}'.",
                nameof(settings));
        }

        if (settings.NetworkRules.Any(rule => string.IsNullOrWhiteSpace(rule.NetworkId)))
        {
            throw new ArgumentException("Network rule identifiers cannot be empty.", nameof(settings));
        }

        if (settings.DefaultProfileId == Guid.Empty ||
            settings.NetworkRules.Any(rule => rule.ProfileId == Guid.Empty))
        {
            throw new ArgumentException("Profile identifiers cannot be empty GUIDs.", nameof(settings));
        }
    }

    private static void ValidateNetwork(NetworkContext? network)
    {
        if (network is not null && string.IsNullOrWhiteSpace(network.NetworkId))
        {
            throw new ArgumentException("The active network identifier cannot be empty.", nameof(network));
        }
    }

    private static void ValidateNonNegative(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The delay cannot be negative.");
        }
    }
}
