using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Broker.Ipc;

public sealed class ConnectionBrokerRequestHandler(
    IPaqetFireRuntime runtime,
    ILiveStatsService liveStats,
    RoutingDataService? routingData = null,
    ExitEngineInstaller? exitEngines = null,
    IDataUsageService? dataUsage = null) : IBrokerRequestHandler
{
    public ValueTask<BrokerResponse> HandleAsync(
        BrokerRequest request,
        CancellationToken cancellationToken) =>
        HandleAsync(request, BrokerRequester.Unknown, cancellationToken);

    public async ValueTask<BrokerResponse> HandleAsync(
        BrokerRequest request,
        BrokerRequester requester,
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

                case BrokerCommand.GetLiveStats:
                    return new BrokerResponse(
                        request.RequestId,
                        IpcProtocol.Version,
                        true,
                        LiveStats: await liveStats.GetAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.GetDiagnostics:
                    return new BrokerResponse(
                        request.RequestId,
                        IpcProtocol.Version,
                        true,
                        Diagnostics: await runtime.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.ManageRoutingData:
                    if (request.RoutingDataAction is not { } routingDataAction || !Enum.IsDefined(routingDataAction))
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.InvalidRequest,
                            "A routing data action is required.");
                    }

                    if (routingData is null)
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.EngineFailure,
                            "Routing data updates are not available in this broker.");
                    }

                    // Update now returns as soon as the download starts; progress and the
                    // result arrive in snapshots.
                    await routingData.ApplyAsync(routingDataAction, cancellationToken).ConfigureAwait(false);
                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.ManageExitEngine:
                    if (request.ExitEngineAction is not { } exitEngineAction)
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.InvalidRequest,
                            "An exit engine action is required.");
                    }

                    if (exitEngines is null)
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.EngineFailure,
                            "Exit engines cannot be installed by this broker.");
                    }

                    // A download returns as soon as it starts; its result arrives in snapshots.
                    await exitEngines.ApplyAsync(exitEngineAction, requester.OpenFileAsUser, cancellationToken)
                        .ConfigureAwait(false);
                    return BrokerResponse.Succeeded(
                        request.RequestId,
                        await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));

                case BrokerCommand.GetDataUsage:
                case BrokerCommand.ResetDataUsage:
                    if (dataUsage is null)
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.EngineFailure,
                            "Data usage is not recorded by this broker.");
                    }

                    if (request.Command == BrokerCommand.ResetDataUsage && request.DataUsageReset is null)
                    {
                        return BrokerResponse.Failed(
                            request.RequestId,
                            BrokerErrorCode.InvalidRequest,
                            "A data usage reset scope is required.");
                    }

                    return new BrokerResponse(
                        request.RequestId,
                        IpcProtocol.Version,
                        true,
                        DataUsage: request.DataUsageReset is { } reset
                            ? await dataUsage.ResetAsync(reset, cancellationToken).ConfigureAwait(false)
                            : await dataUsage.GetReportAsync(cancellationToken).ConfigureAwait(false));

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
        catch (BrokerOperationCancelledException exception)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.Busy,
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
        catch (RoutingDataBusyException exception)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.Busy,
                exception.Message);
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
