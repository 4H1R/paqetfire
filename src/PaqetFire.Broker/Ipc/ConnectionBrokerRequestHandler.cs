using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Broker.Ipc;

public sealed class ConnectionBrokerRequestHandler(
    IPaqetFireRuntime runtime) : IBrokerRequestHandler
{
    public async ValueTask<BrokerResponse> HandleAsync(
        BrokerRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Command)
            {
                case BrokerCommand.Connect:
                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.ConnectAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.Disconnect:
                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.DisconnectAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.VerifyConnection:
                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.VerifyConnectionAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.ManageProfiles:
                    if (request.ProfileAction is null)
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.InvalidRequest,
                            "A profile action is required.");
                    }
                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.ManageProfilesAsync(request.ProfileAction, cancellationToken).ConfigureAwait(false));

                case BrokerCommand.ExportProfiles:
                    return new BrokerResponse(
                        request.RequestId,
                        IpcProtocol.Version,
                        true,
                        ExportedProfiles: await runtime.ExportProfilesAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.SaveSettings:
                    if (request.Settings is null)
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.InvalidRequest,
                            "Connection settings are required.");
                    }

                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.SaveSettingsAsync(request.Settings, request.ConnectAfterSave, cancellationToken)
                            .ConfigureAwait(false));

                case BrokerCommand.GetSnapshot:
                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));

                default:
                    return BrokerResponse.Failed(
                        request.RequestId,
                        BrokerErrorCode.InvalidRequest,
                        "The broker command is not supported.");
            }

        }
        catch (ConfigurationValidationException exception)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.ConfigurationInvalid,
                string.Join(Environment.NewLine, exception.Errors));
        }
        catch (ProfileCatalogException exception)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.ConfigurationInvalid,
                exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.ConfigurationInvalid,
                exception.Message);
        }
        catch (MissingPrerequisiteException exception)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.PrerequisiteMissing,
                string.Join(", ", exception.Prerequisites.Select(item => item.DisplayName)) +
                " must be installed before connecting.");
        }
        catch (ConnectionTransitionException exception)
        {
            var detail = exception.InnerException?.Message;
            var message = string.IsNullOrWhiteSpace(detail)
                ? $"The connection could not complete at the {exception.Engine} engine."
                : $"The connection could not complete at the {exception.Engine} engine. {detail}";
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.EngineFailure,
                message);
        }
        catch (InvalidOperationException exception)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.EngineFailure,
                exception.Message);
        }
    }
}
