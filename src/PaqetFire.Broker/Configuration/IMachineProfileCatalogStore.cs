using PaqetFire.Core.Profiles;

namespace PaqetFire.Broker.Configuration;

public interface IMachineProfileCatalogStore
{
    ValueTask<ProfileCatalog?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(ProfileCatalog catalog, CancellationToken cancellationToken);
}
