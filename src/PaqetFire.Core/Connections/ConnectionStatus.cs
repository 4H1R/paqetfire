using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public sealed record ConnectionStatus(
    ConnectionState State,
    EngineStatus Paqet,
    EngineStatus Xray,
    EngineStatus ProxiFyre,
    string? Detail = null,
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>The exit engine, reported while the profile uses it or it is still running.</summary>
    public EngineStatus? Exit { get; init; }

    public IReadOnlyList<EngineStatus> Engines => Exit is null ? [Paqet, Xray, ProxiFyre] : [Paqet, Xray, Exit, ProxiFyre];
}
