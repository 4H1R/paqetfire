using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Ipc;

public interface IBrokerRequestHandler
{
    ValueTask<BrokerResponse> HandleAsync(
        BrokerRequest request,
        CancellationToken cancellationToken);

    ValueTask<BrokerResponse> HandleAsync(
        BrokerRequest request,
        BrokerRequester requester,
        CancellationToken cancellationToken) =>
        HandleAsync(request, cancellationToken);
}
