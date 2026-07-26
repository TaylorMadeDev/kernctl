namespace Kernctl.Core.Actions;

public enum TransactionState
{
    Created,
    Planning,
    Planned,
    AwaitingConfirmation,
    Applying,
    Verifying,
    Committed,
    CancellationRequested,
    RollingBack,
    RolledBack,
    PartiallyRolledBack,
    Failed,
    RecoveryRequired,
}

public enum ActionExecutionState
{
    Pending,
    Detected,
    Planned,
    Validated,
    SnapshotPersisted,
    Applying,
    Applied,
    Verified,
    RollingBack,
    RolledBack,
    Failed,
    RollbackFailed,
    Skipped,
}

public static class ActionStateMachine
{
    private static readonly Dictionary<TransactionState, HashSet<TransactionState>>
        TransactionTransitions = new Dictionary<TransactionState, HashSet<TransactionState>>
        {
            [TransactionState.Created] = [TransactionState.Planning, TransactionState.Failed],
            [TransactionState.Planning] = [TransactionState.Planned, TransactionState.Failed],
            [TransactionState.Planned] = [TransactionState.AwaitingConfirmation, TransactionState.Failed],
            [TransactionState.AwaitingConfirmation] =
            [
                TransactionState.Applying,
                TransactionState.Committed,
                TransactionState.CancellationRequested,
                TransactionState.Failed,
            ],
            [TransactionState.Applying] =
            [
                TransactionState.Verifying,
                TransactionState.CancellationRequested,
                TransactionState.RollingBack,
                TransactionState.Failed,
                TransactionState.RecoveryRequired,
            ],
            [TransactionState.Verifying] =
            [
                TransactionState.Applying,
                TransactionState.Committed,
                TransactionState.CancellationRequested,
                TransactionState.RollingBack,
                TransactionState.Failed,
                TransactionState.RecoveryRequired,
            ],
            [TransactionState.CancellationRequested] =
            [
                TransactionState.RollingBack,
                TransactionState.RolledBack,
                TransactionState.Failed,
                TransactionState.RecoveryRequired,
            ],
            [TransactionState.RollingBack] =
            [
                TransactionState.RolledBack,
                TransactionState.PartiallyRolledBack,
                TransactionState.RecoveryRequired,
                TransactionState.Failed,
            ],
            [TransactionState.RecoveryRequired] =
            [
                TransactionState.RollingBack,
                TransactionState.RolledBack,
                TransactionState.PartiallyRolledBack,
                TransactionState.Failed,
            ],
            [TransactionState.Committed] = [TransactionState.RollingBack],
            [TransactionState.RolledBack] = [],
            [TransactionState.PartiallyRolledBack] = [],
            [TransactionState.Failed] = [],
        };

    private static readonly Dictionary<ActionExecutionState, HashSet<ActionExecutionState>>
        ActionTransitions = new Dictionary<ActionExecutionState, HashSet<ActionExecutionState>>
        {
            [ActionExecutionState.Pending] =
            [
                ActionExecutionState.Detected,
                ActionExecutionState.Failed,
                ActionExecutionState.Skipped,
            ],
            [ActionExecutionState.Detected] =
            [
                ActionExecutionState.Planned,
                ActionExecutionState.Failed,
                ActionExecutionState.Skipped,
            ],
            [ActionExecutionState.Planned] =
            [
                ActionExecutionState.Validated,
                ActionExecutionState.Failed,
                ActionExecutionState.Skipped,
            ],
            [ActionExecutionState.Validated] =
            [
                ActionExecutionState.SnapshotPersisted,
                ActionExecutionState.Failed,
                ActionExecutionState.Skipped,
            ],
            [ActionExecutionState.SnapshotPersisted] =
            [
                ActionExecutionState.Applying,
                ActionExecutionState.RollingBack,
                ActionExecutionState.Failed,
            ],
            [ActionExecutionState.Applying] =
            [
                ActionExecutionState.Applied,
                ActionExecutionState.Failed,
                ActionExecutionState.RollingBack,
            ],
            [ActionExecutionState.Applied] =
            [
                ActionExecutionState.Verified,
                ActionExecutionState.RollingBack,
                ActionExecutionState.Failed,
            ],
            [ActionExecutionState.Verified] = [ActionExecutionState.RollingBack],
            [ActionExecutionState.Failed] = [ActionExecutionState.RollingBack],
            [ActionExecutionState.RollingBack] =
            [
                ActionExecutionState.RolledBack,
                ActionExecutionState.RollbackFailed,
            ],
            [ActionExecutionState.RolledBack] = [],
            [ActionExecutionState.RollbackFailed] = [],
            [ActionExecutionState.Skipped] = [],
        };

    public static bool CanTransition(TransactionState from, TransactionState to) =>
        TransactionTransitions[from].Contains(to);

    public static bool CanTransition(ActionExecutionState from, ActionExecutionState to) =>
        ActionTransitions[from].Contains(to);

    public static void EnsureTransition(TransactionState from, TransactionState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException(
                $"Transaction cannot transition from {from} to {to}.");
        }
    }

    public static void EnsureTransition(ActionExecutionState from, ActionExecutionState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException(
                $"Action cannot transition from {from} to {to}.");
        }
    }

    public static bool IsTerminal(TransactionState state) =>
        state is TransactionState.Committed
            or TransactionState.RolledBack
            or TransactionState.PartiallyRolledBack
            or TransactionState.Failed;
}

public sealed class InvalidStateTransitionException(string message) : ActionEngineException(message);
