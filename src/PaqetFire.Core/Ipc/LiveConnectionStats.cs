namespace PaqetFire.Core.Ipc;

/// <summary>
/// Lightweight connection telemetry polled by the desktop while the route is connected.
/// Byte counters cover only traffic sent through the Paqet outbound, not direct routes.
/// </summary>
public sealed record LiveConnectionStats(
    DateTimeOffset CapturedAt,
    bool IsConnected,
    DateTimeOffset? ConnectedSince = null,
    long? UplinkBytes = null,
    long? DownlinkBytes = null,
    int? LatencyMilliseconds = null,
    DateTimeOffset? LatencyMeasuredAt = null,
    string? PublicAddress = null,
    string? PublicAddressCountryCode = null);
