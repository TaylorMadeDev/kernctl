using System.Collections.Immutable;
using System.Text.Json;

namespace Kernctl.Core.Actions;

public enum ActionRiskLevel
{
    Low,
    Moderate,
    High,
}

public enum ActionPrivilegeLevel
{
    StandardUser,
    Administrator,
}

public enum ActionRestartRequirement
{
    None,
    ApplicationRestart,
    SignOut,
    SystemRestart,
}

public enum SystemActionCategory
{
    Storage,
    Performance,
    Network,
    Applications,
    System,
    Other,
}

public enum ActionPlatform
{
    Windows,
}

public enum ActionExecutionStage
{
    Detection,
    Planning,
    Validation,
    Snapshot,
    Apply,
    Verification,
    Rollback,
    Persistence,
    Recovery,
}

public sealed record ActionDescriptor(
    string Id,
    int SchemaVersion,
    string DisplayName,
    string ShortDescription,
    string DetailedExplanation,
    SystemActionCategory Category,
    ActionRiskLevel RiskLevel,
    ActionPrivilegeLevel RequiredPrivilege,
    ActionRestartRequirement RestartRequirement,
    ImmutableArray<ActionPlatform> SupportedPlatforms,
    bool SupportsRollback,
    bool IsAvailable,
    TimeSpan? EstimatedDuration);

public sealed record ActionDetectionResult(
    bool IsAvailable,
    string CurrentState,
    string Summary,
    ImmutableArray<string> UnavailableReasons)
{
    public static ActionDetectionResult Available(string currentState, string summary) =>
        new(true, currentState, summary, []);

    public static ActionDetectionResult Unavailable(
        string currentState,
        string summary,
        params string[] reasons) =>
        new(false, currentState, summary, [.. reasons]);
}

public sealed record ActionPlan(
    string ActionId,
    int ActionSchemaVersion,
    string CurrentState,
    string DesiredState,
    ImmutableArray<PlannedOperation> Operations,
    ImmutableArray<string> AffectedResources,
    ActionRiskLevel RiskLevel,
    ActionPrivilegeLevel RequiredPrivilege,
    ActionRestartRequirement RestartRequirement,
    bool SupportsRollback,
    ImmutableArray<string> Warnings,
    ImmutableArray<string> UnavailableReasons,
    string UserExplanation);

public sealed record PlannedOperation(string Name, string Explanation);

public sealed record ActionValidationIssue(string Code, string Message);

public sealed record ActionValidationResult(
    bool IsValid,
    ImmutableArray<ActionValidationIssue> Issues)
{
    public static ActionValidationResult Valid { get; } = new(true, []);

    public static ActionValidationResult Invalid(params ActionValidationIssue[] issues) =>
        new(false, [.. issues]);
}

/// <summary>
/// Contains only the action-specific, explicitly versioned JSON data needed for rollback.
/// The transaction engine adds ownership, timestamps, size, and integrity metadata.
/// </summary>
public sealed record ActionStatePayload(int SchemaVersion, JsonElement OriginalState)
{
    public static ActionStatePayload From<T>(int schemaVersion, T state) =>
        new(schemaVersion, JsonSerializer.SerializeToElement(state, ActionJson.Options));
}

public sealed record SnapshotIntegrity(string Algorithm, string Digest, int PayloadBytes);

public sealed record ActionStateSnapshot(
    int SnapshotSchemaVersion,
    Guid TransactionId,
    string ActionId,
    int ActionSchemaVersion,
    DateTimeOffset CapturedAtUtc,
    JsonElement OriginalState,
    SnapshotIntegrity Integrity);

public sealed record ActionApplyResult(
    bool Succeeded,
    bool MayHaveMutated,
    string Summary,
    ActionError? Error)
{
    public static ActionApplyResult Success(string summary) => new(true, true, summary, null);

    public static ActionApplyResult Failure(
        string summary,
        ActionError error,
        bool mayHaveMutated = false) =>
        new(false, mayHaveMutated, summary, error);
}

public sealed record ActionVerificationResult(
    bool Succeeded,
    string Summary,
    ActionError? Error)
{
    public static ActionVerificationResult Success(string summary) => new(true, summary, null);

    public static ActionVerificationResult Failure(string summary, ActionError error) =>
        new(false, summary, error);
}

public sealed record ActionRollbackResult(
    bool Succeeded,
    string Summary,
    ActionError? Error)
{
    public static ActionRollbackResult Success(string summary) => new(true, summary, null);

    public static ActionRollbackResult Failure(string summary, ActionError error) =>
        new(false, summary, error);
}

public sealed record ActionError(
    string Code,
    string UserMessage,
    string TechnicalDiagnostic,
    string? ActionId,
    Guid TransactionId,
    ActionExecutionStage Stage,
    bool RetryPossible,
    bool RollbackPossible);

public sealed record ActionProgressUpdate(
    Guid TransactionId,
    string? ActionId,
    string? ActionName,
    ActionExecutionStage Stage,
    string Message,
    int CompletedActions,
    int TotalActions,
    bool IsRollback,
    bool IsCancellationRequested,
    DateTimeOffset TimestampUtc);

public sealed class ActionExecutionContext
{
    private readonly Action<ActionProgressUpdate>? progress;

    public ActionExecutionContext(
        Guid transactionId,
        bool isDryRun,
        DateTimeOffset startedAtUtc,
        Action<ActionProgressUpdate>? progress = null)
    {
        TransactionId = transactionId;
        IsDryRun = isDryRun;
        StartedAtUtc = startedAtUtc;
        this.progress = progress;
    }

    public Guid TransactionId { get; }

    public bool IsDryRun { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public void Report(
        ActionDescriptor descriptor,
        ActionExecutionStage stage,
        string message,
        int completedActions = 0,
        int totalActions = 0,
        bool isRollback = false,
        bool isCancellationRequested = false) =>
        progress?.Invoke(new(
            TransactionId,
            descriptor.Id,
            descriptor.DisplayName,
            stage,
            message,
            completedActions,
            totalActions,
            isRollback,
            isCancellationRequested,
            DateTimeOffset.UtcNow));
}

public interface ISystemAction
{
    ActionDescriptor Descriptor { get; }

    Task<ActionDetectionResult> DetectAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken);

    Task<ActionPlan> PlanAsync(
        ActionExecutionContext context,
        ActionDetectionResult detection,
        CancellationToken cancellationToken);

    Task<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken);

    Task<ActionStatePayload> CaptureStateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken);

    Task<ActionApplyResult> ApplyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken);

    Task<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken);

    Task<ActionRollbackResult> RollbackAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        ActionStateSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IActionRegistry
{
    IReadOnlyCollection<ISystemAction> Actions { get; }

    ISystemAction GetRequired(string actionId);

    bool TryGet(string actionId, out ISystemAction? action);
}

public sealed class ActionRegistry(IEnumerable<ISystemAction> actions) : IActionRegistry
{
    private readonly Dictionary<string, ISystemAction> actions = actions.ToDictionary(
        action => action.Descriptor.Id,
        StringComparer.Ordinal);

    public IReadOnlyCollection<ISystemAction> Actions => actions.Values;

    public ISystemAction GetRequired(string actionId) =>
        actions.TryGetValue(actionId, out var action)
            ? action
            : throw new ActionEngineException($"Action '{actionId}' is not registered.");

    public bool TryGet(string actionId, out ISystemAction? action) =>
        actions.TryGetValue(actionId, out action);
}

public sealed record ActionTransactionRequest(
    ImmutableArray<string> ActionIds,
    bool IsDryRun = false);

public sealed record PlannedAction(
    ActionDescriptor Descriptor,
    ActionDetectionResult Detection,
    ActionPlan Plan,
    ActionValidationResult Validation);

public sealed record ActionTransactionPlan(
    Guid TransactionId,
    DateTimeOffset CreatedAtUtc,
    bool IsDryRun,
    ImmutableArray<PlannedAction> Actions)
{
    public bool CanExecute => Actions.Length > 0
        && Actions.All(action =>
            action.Descriptor.IsAvailable
            && action.Detection.IsAvailable
            && action.Validation.IsValid
            && action.Plan.UnavailableReasons.IsEmpty);

    public ActionRestartRequirement RestartRequirement =>
        Actions.Select(action => action.Plan.RestartRequirement).DefaultIfEmpty().Max();
}

public sealed record TransactionExecutionResult(
    Guid TransactionId,
    bool Succeeded,
    bool IsDryRun,
    TransactionState FinalState,
    bool RollbackAttempted,
    ActionRestartRequirement RestartRequirement,
    ImmutableArray<ActionError> Errors,
    string Summary);

public sealed record TransactionRecoveryInfo(
    Guid TransactionId,
    DateTimeOffset StartedAtUtc,
    TransactionState State,
    ImmutableArray<string> ActionNames,
    ImmutableArray<string> AppliedActions,
    ImmutableArray<string> VerifiedActions,
    ImmutableArray<string> SnapshotsAvailable,
    bool CanRollback,
    bool RequiresAdministrator,
    bool ManualInterventionMayBeRequired,
    string Explanation);

public sealed record TransactionHistoryEntry(
    Guid TransactionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ImmutableArray<string> ActionNames,
    TransactionState FinalState,
    bool IsDryRun,
    bool RollbackOccurred,
    ActionRestartRequirement RestartRequirement,
    ImmutableArray<string> ErrorSummaries);

public interface IActionTransactionEngine
{
    event EventHandler<ActionProgressUpdate>? ProgressChanged;

    Task<ActionTransactionPlan> PlanAsync(
        ActionTransactionRequest request,
        CancellationToken cancellationToken = default);

    Task<TransactionExecutionResult> DryRunAsync(
        ActionTransactionRequest request,
        CancellationToken cancellationToken = default);

    Task<TransactionExecutionResult> DryRunAsync(
        ActionTransactionPlan plan,
        CancellationToken cancellationToken = default);

    Task<TransactionExecutionResult> ExecuteAsync(
        ActionTransactionPlan plan,
        CancellationToken cancellationToken = default);

    bool RequestCancellation(Guid transactionId);

    Task<TransactionExecutionResult> RollbackAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransactionRecoveryInfo>> InspectIncompleteAsync(
        CancellationToken cancellationToken = default);

    Task<TransactionExecutionResult> RecoverAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransactionHistoryEntry>> ReadHistoryAsync(
        CancellationToken cancellationToken = default);
}

public class ActionEngineException(string message, Exception? innerException = null)
    : Exception(message, innerException);
