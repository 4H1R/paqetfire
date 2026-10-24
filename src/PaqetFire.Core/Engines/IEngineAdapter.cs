namespace PaqetFire.Core.Engines;

public interface IEngineAdapter
{
    EngineKind Kind { get; }

    ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>An exit engine after Paqet, which only the profiles that select it run.</summary>
public interface IExitEngineAdapter : IEngineAdapter
{
    /// <summary>Whether the configuration written for the next start uses this exit.</summary>
    bool IsEnabled { get; }
}
