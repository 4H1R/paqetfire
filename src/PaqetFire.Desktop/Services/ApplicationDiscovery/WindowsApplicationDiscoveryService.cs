using PaqetFire.Core.Applications;

namespace PaqetFire.Desktop.Services.ApplicationDiscovery;

public sealed class WindowsApplicationDiscoveryService : IApplicationDiscoveryService
{
    private readonly ApplicationDiscoveryService _inner = new([
        new WindowsInstalledApplicationSource(),
        new WindowsRunningApplicationSource(),
    ]);

    public Task<IReadOnlyList<DiscoverableApplication>> DiscoverAsync(
        string? searchText = null,
        CancellationToken cancellationToken = default) =>
        _inner.DiscoverAsync(searchText, cancellationToken);
}
