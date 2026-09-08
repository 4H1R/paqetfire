namespace PaqetFire.Core.Applications;

public interface IApplicationDiscoveryService
{
    Task<IReadOnlyList<DiscoverableApplication>> DiscoverAsync(
        string? searchText = null,
        CancellationToken cancellationToken = default);
}
