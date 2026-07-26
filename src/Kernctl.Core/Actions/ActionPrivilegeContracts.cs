using System.Collections.Immutable;

namespace Kernctl.Core.Actions;

public enum ActionPrivilegeBrokerState
{
    Preparing,
    AwaitingConsent,
    Connecting,
    Verifying,
    Ready,
    PermissionDeclined,
    Failed,
}

public enum ActionPrivilegeOpenStatus
{
    Ready,
    Cancelled,
    Failed,
}

public sealed record ActionPrivilegeBrokerProgress(
    ActionPrivilegeBrokerState State,
    string SafeMessage);

public sealed record ActionPrivilegeSessionRequest(
    Guid TransactionId,
    ImmutableArray<string> ActionIds,
    bool IsRollback);

public sealed record ActionPrivilegeBrokerError(
    string Code,
    string SafeMessage,
    bool RetryPossible);

public sealed record ActionPrivilegeOpenResult(
    ActionPrivilegeOpenStatus Status,
    IActionPrivilegeSession? Session,
    ActionPrivilegeBrokerError? Error)
{
    public static ActionPrivilegeOpenResult Ready(IActionPrivilegeSession session) =>
        new(ActionPrivilegeOpenStatus.Ready, session, null);

    public static ActionPrivilegeOpenResult Cancelled(ActionPrivilegeBrokerError error) =>
        new(ActionPrivilegeOpenStatus.Cancelled, null, error);

    public static ActionPrivilegeOpenResult Failed(ActionPrivilegeBrokerError error) =>
        new(ActionPrivilegeOpenStatus.Failed, null, error);
}

public interface IActionPrivilegeSession : IAsyncDisposable;

public interface IActionPrivilegeBroker
{
    Task<ActionPrivilegeOpenResult> OpenAsync(
        ActionPrivilegeSessionRequest request,
        IProgress<ActionPrivilegeBrokerProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableActionPrivilegeBroker : IActionPrivilegeBroker
{
    public Task<ActionPrivilegeOpenResult> OpenAsync(
        ActionPrivilegeSessionRequest request,
        IProgress<ActionPrivilegeBrokerProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ActionPrivilegeOpenResult.Failed(
            new(
                "ELEVATION_BROKER_UNAVAILABLE",
                "Administrator operations are unavailable in this application build.",
                RetryPossible: false)));
}
