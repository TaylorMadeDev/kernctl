using Kernctl.Core.Actions;

namespace Kernctl.Broker.Client;

public sealed class ActionPrivilegeBroker(IBrokerClient brokerClient) : IActionPrivilegeBroker
{
    public async Task<ActionPrivilegeOpenResult> OpenAsync(
        ActionPrivilegeSessionRequest request,
        IProgress<ActionPrivilegeBrokerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.TransactionId == Guid.Empty || request.ActionIds.IsDefaultOrEmpty)
        {
            return ActionPrivilegeOpenResult.Failed(
                new(
                    "ELEVATION_INVALID_REQUEST",
                    "The administrator request did not identify a valid transaction.",
                    RetryPossible: false));
        }

        var brokerProgress = progress is null
            ? null
            : new ProgressAdapter(progress);
        var result = await brokerClient.OpenAsync(
            "diagnostics",
            brokerProgress,
            cancellationToken);
        var error = result.Error is null
            ? new ActionPrivilegeBrokerError(
                "ELEVATION_BROKER_FAILED",
                "Administrator permission could not be prepared safely.",
                RetryPossible: true)
            : new ActionPrivilegeBrokerError(
                result.Error.Code,
                result.Error.SafeMessage,
                result.Error.RetryPossible);
        return result.Status switch
        {
            BrokerClientOpenStatus.Ready when result.Session is not null =>
                ActionPrivilegeOpenResult.Ready(new ActionPrivilegeSession(result.Session)),
            BrokerClientOpenStatus.Cancelled =>
                ActionPrivilegeOpenResult.Cancelled(error),
            _ => ActionPrivilegeOpenResult.Failed(error),
        };
    }

    private sealed class ActionPrivilegeSession(BrokerSession session)
        : IActionPrivilegeSession
    {
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private sealed class ProgressAdapter(
        IProgress<ActionPrivilegeBrokerProgress> progress)
        : IProgress<BrokerLaunchProgress>
    {
        public void Report(BrokerLaunchProgress value) =>
            progress.Report(new(
                value.Stage switch
                {
                    BrokerLaunchStage.Preparing => ActionPrivilegeBrokerState.Preparing,
                    BrokerLaunchStage.AwaitingConsent => ActionPrivilegeBrokerState.AwaitingConsent,
                    BrokerLaunchStage.Connecting => ActionPrivilegeBrokerState.Connecting,
                    BrokerLaunchStage.Verifying => ActionPrivilegeBrokerState.Verifying,
                    BrokerLaunchStage.Ready => ActionPrivilegeBrokerState.Ready,
                    BrokerLaunchStage.PermissionDeclined =>
                        ActionPrivilegeBrokerState.PermissionDeclined,
                    _ => ActionPrivilegeBrokerState.Failed,
                },
                value.SafeMessage));
    }
}
