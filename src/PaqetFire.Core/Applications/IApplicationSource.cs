namespace PaqetFire.Core.Applications;

public interface IApplicationSource
{
    IReadOnlyList<DiscoverableApplication> GetApplications(CancellationToken cancellationToken = default);
}
