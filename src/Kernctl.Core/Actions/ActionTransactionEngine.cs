using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kernctl.Core.Actions;

public sealed class ActionTransactionEngine(
    IActionRegistry registry,
    IActionJournalStore journalStore,
    IActionHistoryService historyService,
    ILogger<ActionTransactionEngine>? logger = null) : IActionTransactionEngine, IDisposable
{
    private static readonly Action<ILogger, Guid, string, Exception?> LogPlanningFailure =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1001, nameof(LogPlanningFailure)),
            "Action planning failed for transaction {TransactionId} action {ActionId}.");
    private static readonly Action<ILogger, Guid, string, Exception?> LogSnapshotFailure =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1002, nameof(LogSnapshotFailure)),
            "Snapshot capture failed for transaction {TransactionId} action {ActionId}.");
    private static readonly Action<ILogger, Guid, string, Exception?> LogApplyFailure =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(1003, nameof(LogApplyFailure)),
            "Apply failed unexpectedly for transaction {TransactionId} action {ActionId}.");
    private static readonly Action<ILogger, Guid, string, Exception?> LogVerificationFailure =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(1004, nameof(LogVerificationFailure)),
            "Verification failed unexpectedly for transaction {TransactionId} action {ActionId}.");
    private static readonly Action<ILogger, Guid, string, Exception?> LogRollbackFailure =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(1005, nameof(LogRollbackFailure)),
            "Rollback failed unexpectedly for transaction {TransactionId} action {ActionId}.");

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeCancellations = new();
    private readonly IActionHistoryService historyService = historyService;
    private readonly IActionJournalStore journalStore = journalStore;
    private readonly ILogger<ActionTransactionEngine> logger =
        logger ?? NullLogger<ActionTransactionEngine>.Instance;
    private readonly IActionRegistry registry = registry;
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private bool disposed;

    public event EventHandler<ActionProgressUpdate>? ProgressChanged;

    public async Task<ActionTransactionPlan> PlanAsync(
        ActionTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateRequest(request);
        var transactionId = Guid.NewGuid();
        var startedAtUtc = DateTimeOffset.UtcNow;
        var actions = request.ActionIds.Select(registry.GetRequired).ToArray();
        foreach (var action in actions)
        {
            ValidateDescriptor(action.Descriptor);
        }

        var journal = TransactionJournal.Create(
            transactionId,
            request,
            actions.Select(action => action.Descriptor).ToArray(),
            startedAtUtc);
        await journalStore.SaveAsync(journal, cancellationToken);
        journal = await TransitionTransactionAsync(
            journal,
            TransactionState.Planning,
            cancellationToken);

        var context = CreateContext(transactionId, request.IsDryRun, startedAtUtc);
        var plannedActions = ImmutableArray.CreateBuilder<PlannedAction>(actions.Length);
        for (var index = 0; index < actions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[index];
            try
            {
                Report(
                    transactionId,
                    action.Descriptor,
                    ActionExecutionStage.Detection,
                    $"Detecting {action.Descriptor.DisplayName}.",
                    index,
                    actions.Length);
                var detection = await action.DetectAsync(context, cancellationToken);
                journal = await TransitionActionAsync(
                    journal,
                    index,
                    ActionExecutionState.Detected,
                    cancellationToken);

                Report(
                    transactionId,
                    action.Descriptor,
                    ActionExecutionStage.Planning,
                    $"Planning {action.Descriptor.DisplayName}.",
                    index,
                    actions.Length);
                var plan = await action.PlanAsync(context, detection, cancellationToken);
                ValidatePlanCompatibility(action.Descriptor, plan);
                journal = await TransitionActionAsync(
                    journal,
                    index,
                    ActionExecutionState.Planned,
                    cancellationToken,
                    plan: plan);

                Report(
                    transactionId,
                    action.Descriptor,
                    ActionExecutionStage.Validation,
                    $"Validating {action.Descriptor.DisplayName}.",
                    index,
                    actions.Length);
                var validation = await action.ValidateAsync(context, plan, cancellationToken);
                var actionState = !action.Descriptor.IsAvailable || !detection.IsAvailable
                    ? ActionExecutionState.Skipped
                    : validation.IsValid
                        ? ActionExecutionState.Validated
                        : ActionExecutionState.Failed;
                journal = await TransitionActionAsync(
                    journal,
                    index,
                    actionState,
                    cancellationToken,
                    error: validation.IsValid
                        ? null
                        : ValidationError(
                            transactionId,
                            action.Descriptor.Id,
                            validation));
                plannedActions.Add(new(action.Descriptor, detection, plan, validation));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await FailPlanningJournalAsync(
                    journal,
                    index,
                    transactionId,
                    action.Descriptor,
                    "ACTION_CANCELLED",
                    "Planning was cancelled.",
                    ActionExecutionStage.Planning);
                throw;
            }
            catch (Exception exception)
            {
                LogPlanningFailure(logger, transactionId, action.Descriptor.Id, exception);
                var error = UnexpectedError(
                    transactionId,
                    action.Descriptor.Id,
                    ActionExecutionStage.Planning,
                    exception,
                    rollbackPossible: false);
                var fallbackDetection = ActionDetectionResult.Unavailable(
                    "Unknown",
                    "Current state could not be detected.",
                    error.UserMessage);
                var fallbackPlan = UnavailablePlan(action.Descriptor, error.UserMessage);
                var validation = ActionValidationResult.Invalid(
                    new ActionValidationIssue(error.Code, error.UserMessage));
                journal = await TransitionActionAsync(
                    journal,
                    index,
                    ActionExecutionState.Failed,
                    CancellationToken.None,
                    plan: fallbackPlan,
                    error: error);
                plannedActions.Add(new(
                    action.Descriptor,
                    fallbackDetection,
                    fallbackPlan,
                    validation));
            }
        }

        journal = await TransitionTransactionAsync(
            journal,
            TransactionState.Planned,
            cancellationToken);
        journal = await TransitionTransactionAsync(
            journal,
            TransactionState.AwaitingConfirmation,
            cancellationToken);
        return new(
            transactionId,
            startedAtUtc,
            request.IsDryRun,
            plannedActions.MoveToImmutable());
    }

    public async Task<TransactionExecutionResult> DryRunAsync(
        ActionTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(request with { IsDryRun = true }, cancellationToken);
        return await DryRunAsync(plan, cancellationToken);
    }

    public async Task<TransactionExecutionResult> DryRunAsync(
        ActionTransactionPlan plan,
        CancellationToken cancellationToken = default)
    {
        var journal = await journalStore.LoadAsync(plan.TransactionId, cancellationToken);
        ValidateExecutionPlan(plan, journal);
        if (!journal.IsDryRun)
        {
            journal = journal with
            {
                IsDryRun = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await journalStore.SaveAsync(journal, cancellationToken);
        }

        if (plan.CanExecute)
        {
            journal = await TransitionTransactionAsync(
                journal,
                TransactionState.Committed,
                cancellationToken,
                complete: true);
            await journalStore.ArchiveAsync(journal, cancellationToken);
            return Result(
                journal,
                succeeded: true,
                "Dry run completed. No state was captured and no actions were applied.");
        }

        var error = new ActionError(
            "PLAN_NOT_EXECUTABLE",
            "One or more actions cannot be executed.",
            "Dry-run validation found an unavailable or invalid action.",
            null,
            plan.TransactionId,
            ActionExecutionStage.Validation,
            true,
            false);
        journal = journal with { Errors = journal.Errors.Add(error) };
        journal = await TransitionTransactionAsync(
            journal,
            TransactionState.Failed,
            cancellationToken,
            complete: true);
        await journalStore.ArchiveAsync(journal, cancellationToken);
        return Result(journal, succeeded: false, error.UserMessage);
    }

    public async Task<TransactionExecutionResult> ExecuteAsync(
        ActionTransactionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (plan.IsDryRun)
        {
            throw new ActionEngineException("A dry-run plan cannot enter the mutation pipeline.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            var cancelledJournal = await journalStore.LoadAsync(
                plan.TransactionId,
                CancellationToken.None);
            ValidateExecutionPlan(plan, cancelledJournal);
            return await CompletePreMutationCancellationAsync(cancelledJournal);
        }

        if (!await mutationLock.WaitAsync(0, cancellationToken))
        {
            return BusyResult(plan);
        }

        using var transactionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!activeCancellations.TryAdd(plan.TransactionId, transactionCancellation))
        {
            mutationLock.Release();
            return BusyResult(plan);
        }

        try
        {
            var journal = await journalStore.LoadAsync(plan.TransactionId, cancellationToken);
            ValidateExecutionPlan(plan, journal);
            if (!plan.CanExecute)
            {
                var error = new ActionError(
                    "PLAN_NOT_EXECUTABLE",
                    "One or more actions are unavailable or failed validation.",
                    "The immutable transaction plan is not executable.",
                    null,
                    plan.TransactionId,
                    ActionExecutionStage.Validation,
                    true,
                    false);
                journal = journal with { Errors = journal.Errors.Add(error) };
                journal = await TransitionTransactionAsync(
                    journal,
                    TransactionState.Failed,
                    CancellationToken.None,
                    complete: true);
                await journalStore.ArchiveAsync(journal, CancellationToken.None);
                return Result(journal, false, error.UserMessage);
            }

            if (transactionCancellation.IsCancellationRequested)
            {
                return await CompletePreMutationCancellationAsync(journal);
            }

            var context = CreateContext(plan.TransactionId, false, plan.CreatedAtUtc);
            foreach (var planned in plan.Actions)
            {
                var validation = await registry
                    .GetRequired(planned.Descriptor.Id)
                    .ValidateAsync(context, planned.Plan, transactionCancellation.Token);
                if (!validation.IsValid)
                {
                    var error = ValidationError(
                        plan.TransactionId,
                        planned.Descriptor.Id,
                        validation);
                    journal = journal with { Errors = journal.Errors.Add(error) };
                    journal = await TransitionTransactionAsync(
                        journal,
                        TransactionState.Failed,
                        CancellationToken.None,
                        complete: true);
                    await journalStore.ArchiveAsync(journal, CancellationToken.None);
                    return Result(journal, false, error.UserMessage);
                }
            }

            journal = await TransitionTransactionAsync(
                journal,
                TransactionState.Applying,
                CancellationToken.None);

            for (var index = 0; index < plan.Actions.Length; index++)
            {
                if (transactionCancellation.IsCancellationRequested)
                {
                    journal = await MarkCancellationRequestedAsync(journal);
                    return await PerformRollbackAsync(
                        journal,
                        "Cancellation requested. Applied actions were rolled back.");
                }

                var planned = plan.Actions[index];
                var action = registry.GetRequired(planned.Descriptor.Id);
                Report(
                    plan.TransactionId,
                    action.Descriptor,
                    ActionExecutionStage.Snapshot,
                    $"Capturing rollback state for {action.Descriptor.DisplayName}.",
                    index,
                    plan.Actions.Length);

                ActionStateSnapshot snapshot;
                try
                {
                    var payload = await action.CaptureStateAsync(
                        context,
                        planned.Plan,
                        transactionCancellation.Token);
                    snapshot = ActionSnapshotIntegrity.Create(
                        plan.TransactionId,
                        action.Descriptor,
                        payload,
                        DateTimeOffset.UtcNow);
                    ActionSnapshotIntegrity.Validate(snapshot);
                    journal = await TransitionActionAsync(
                        journal,
                        index,
                        ActionExecutionState.SnapshotPersisted,
                        CancellationToken.None,
                        snapshot: snapshot);
                }
                catch (OperationCanceledException)
                    when (transactionCancellation.IsCancellationRequested)
                {
                    journal = await MarkCancellationRequestedAsync(journal);
                    return await PerformRollbackAsync(
                        journal,
                        "Cancellation completed before the next action was applied.");
                }
                catch (Exception exception)
                {
                    LogSnapshotFailure(
                        logger,
                        plan.TransactionId,
                        action.Descriptor.Id,
                        exception);
                    var error = UnexpectedError(
                        plan.TransactionId,
                        action.Descriptor.Id,
                        ActionExecutionStage.Snapshot,
                        exception,
                        rollbackPossible: false);
                    journal = await TransitionActionAsync(
                        journal,
                        index,
                        ActionExecutionState.Failed,
                        CancellationToken.None,
                        error: error);
                    journal = journal with { Errors = journal.Errors.Add(error) };
                    return await PerformRollbackAsync(journal, error.UserMessage);
                }

                journal = await TransitionActionAsync(
                    journal,
                    index,
                    ActionExecutionState.Applying,
                    CancellationToken.None);
                Report(
                    plan.TransactionId,
                    action.Descriptor,
                    ActionExecutionStage.Apply,
                    $"Applying {action.Descriptor.DisplayName}.",
                    index,
                    plan.Actions.Length);

                ActionApplyResult applyResult;
                try
                {
                    applyResult = await action.ApplyAsync(
                        context,
                        planned.Plan,
                        transactionCancellation.Token);
                }
                catch (OperationCanceledException)
                    when (transactionCancellation.IsCancellationRequested)
                {
                    var error = new ActionError(
                        "ACTION_CANCELLED_DURING_APPLY",
                        "The action was cancelled and recovery has started.",
                        "The action observed cancellation during ApplyAsync.",
                        action.Descriptor.Id,
                        plan.TransactionId,
                        ActionExecutionStage.Apply,
                        true,
                        action.Descriptor.SupportsRollback);
                    journal = await TransitionActionAsync(
                        journal,
                        index,
                        ActionExecutionState.Failed,
                        CancellationToken.None,
                        mayHaveMutated: true,
                        error: error);
                    journal = journal with { Errors = journal.Errors.Add(error) };
                    journal = await MarkCancellationRequestedAsync(journal);
                    return await PerformRollbackAsync(journal, error.UserMessage);
                }
                catch (Exception exception)
                {
                    LogApplyFailure(
                        logger,
                        plan.TransactionId,
                        action.Descriptor.Id,
                        exception);
                    var error = UnexpectedError(
                        plan.TransactionId,
                        action.Descriptor.Id,
                        ActionExecutionStage.Apply,
                        exception,
                        action.Descriptor.SupportsRollback);
                    journal = await TransitionActionAsync(
                        journal,
                        index,
                        ActionExecutionState.Failed,
                        CancellationToken.None,
                        mayHaveMutated: true,
                        error: error);
                    journal = journal with { Errors = journal.Errors.Add(error) };
                    return await PerformRollbackAsync(journal, error.UserMessage);
                }

                journal = await TransitionActionAsync(
                    journal,
                    index,
                    applyResult.Succeeded
                        ? ActionExecutionState.Applied
                        : ActionExecutionState.Failed,
                    CancellationToken.None,
                    mayHaveMutated: applyResult.MayHaveMutated,
                    error: applyResult.Error);
                if (!applyResult.Succeeded)
                {
                    var error = applyResult.Error ?? new ActionError(
                        "ACTION_APPLY_FAILED",
                        "The action could not be applied.",
                        applyResult.Summary,
                        action.Descriptor.Id,
                        plan.TransactionId,
                        ActionExecutionStage.Apply,
                        true,
                        action.Descriptor.SupportsRollback);
                    journal = journal with { Errors = journal.Errors.Add(error) };
                    return await PerformRollbackAsync(journal, error.UserMessage);
                }

                if (transactionCancellation.IsCancellationRequested)
                {
                    journal = await MarkCancellationRequestedAsync(journal);
                    return await PerformRollbackAsync(
                        journal,
                        "Cancellation requested. The action was journaled and rollback has started.");
                }

                journal = await TransitionTransactionAsync(
                    journal,
                    TransactionState.Verifying,
                    CancellationToken.None);
                Report(
                    plan.TransactionId,
                    action.Descriptor,
                    ActionExecutionStage.Verification,
                    $"Verifying {action.Descriptor.DisplayName}.",
                    index,
                    plan.Actions.Length);

                ActionVerificationResult verification;
                try
                {
                    verification = await action.VerifyAsync(
                        context,
                        planned.Plan,
                        transactionCancellation.Token);
                }
                catch (OperationCanceledException)
                    when (transactionCancellation.IsCancellationRequested)
                {
                    var error = new ActionError(
                        "ACTION_CANCELLED_DURING_VERIFICATION",
                        "Verification was cancelled and rollback has started.",
                        "The action observed cancellation during VerifyAsync.",
                        action.Descriptor.Id,
                        plan.TransactionId,
                        ActionExecutionStage.Verification,
                        true,
                        action.Descriptor.SupportsRollback);
                    journal = await TransitionActionAsync(
                        journal,
                        index,
                        ActionExecutionState.Failed,
                        CancellationToken.None,
                        mayHaveMutated: true,
                        error: error);
                    journal = journal with { Errors = journal.Errors.Add(error) };
                    journal = await MarkCancellationRequestedAsync(journal);
                    return await PerformRollbackAsync(journal, error.UserMessage);
                }
                catch (Exception exception)
                {
                    LogVerificationFailure(
                        logger,
                        plan.TransactionId,
                        action.Descriptor.Id,
                        exception);
                    verification = ActionVerificationResult.Failure(
                        "Verification failed unexpectedly.",
                        UnexpectedError(
                            plan.TransactionId,
                            action.Descriptor.Id,
                            ActionExecutionStage.Verification,
                            exception,
                            action.Descriptor.SupportsRollback));
                }

                journal = await TransitionActionAsync(
                    journal,
                    index,
                    verification.Succeeded
                        ? ActionExecutionState.Verified
                        : ActionExecutionState.Failed,
                    CancellationToken.None,
                    mayHaveMutated: true,
                    error: verification.Error);
                if (!verification.Succeeded)
                {
                    var error = verification.Error ?? new ActionError(
                        "ACTION_VERIFICATION_FAILED",
                        "The action could not be verified.",
                        verification.Summary,
                        action.Descriptor.Id,
                        plan.TransactionId,
                        ActionExecutionStage.Verification,
                        true,
                        action.Descriptor.SupportsRollback);
                    journal = journal with { Errors = journal.Errors.Add(error) };
                    return await PerformRollbackAsync(journal, error.UserMessage);
                }

                Report(
                    plan.TransactionId,
                    action.Descriptor,
                    ActionExecutionStage.Verification,
                    $"{action.Descriptor.DisplayName} verified.",
                    index + 1,
                    plan.Actions.Length);
                if (index < plan.Actions.Length - 1)
                {
                    journal = await TransitionTransactionAsync(
                        journal,
                        TransactionState.Applying,
                        CancellationToken.None);
                }
            }

            journal = await TransitionTransactionAsync(
                journal,
                TransactionState.Committed,
                CancellationToken.None,
                complete: true);
            await journalStore.ArchiveAsync(journal, CancellationToken.None);
            return Result(journal, true, "All actions were applied and verified.");
        }
        finally
        {
            activeCancellations.TryRemove(plan.TransactionId, out _);
            mutationLock.Release();
        }
    }

    public bool RequestCancellation(Guid transactionId)
    {
        if (!activeCancellations.TryGetValue(transactionId, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }

    public async Task<TransactionExecutionResult> RollbackAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await mutationLock.WaitAsync(cancellationToken);
        try
        {
            var journal = await journalStore.LoadAsync(transactionId, cancellationToken);
            return await PerformRollbackAsync(journal, "Rollback requested.");
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<IReadOnlyList<TransactionRecoveryInfo>> InspectIncompleteAsync(
        CancellationToken cancellationToken = default)
    {
        var scan = await journalStore.ScanActiveAsync(cancellationToken);
        var recoveries = scan.Journals
            .Where(journal => !ActionStateMachine.IsTerminal(journal.State))
            .Select(CreateRecoveryInfo)
            .ToList();
        recoveries.AddRange(scan.Errors.Select(error => new TransactionRecoveryInfo(
            Guid.Empty,
            DateTimeOffset.UtcNow,
            TransactionState.RecoveryRequired,
            [],
            [],
            [],
            [],
            false,
            false,
            true,
            $"Journal '{error.FileName}' is invalid. {error.SafeMessage}")));
        return recoveries;
    }

    public async Task<TransactionExecutionResult> RecoverAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await mutationLock.WaitAsync(cancellationToken);
        try
        {
            var journal = await journalStore.LoadAsync(transactionId, cancellationToken);
            if (ActionStateMachine.IsTerminal(journal.State))
            {
                return Result(journal, journal.State == TransactionState.Committed, "No recovery is required.");
            }

            var mutated = journal.Actions.Any(action =>
                action.MayHaveMutated
                || action.State is ActionExecutionState.Applying
                    or ActionExecutionState.Applied
                    or ActionExecutionState.Verified
                    or ActionExecutionState.RollingBack
                    or ActionExecutionState.RollbackFailed);
            if (!mutated)
            {
                var error = new ActionError(
                    "INTERRUPTED_BEFORE_MUTATION",
                    "The interrupted transaction made no changes.",
                    "Recovery found no action that may have mutated state.",
                    null,
                    transactionId,
                    ActionExecutionStage.Recovery,
                    true,
                    false);
                journal = journal with { Errors = journal.Errors.Add(error) };
                journal = await TransitionTransactionAsync(
                    journal,
                    TransactionState.Failed,
                    CancellationToken.None,
                    complete: true);
                await journalStore.ArchiveAsync(journal, CancellationToken.None);
                return Result(journal, false, error.UserMessage);
            }

            if (journal.State != TransactionState.RecoveryRequired
                && journal.State != TransactionState.RollingBack)
            {
                journal = await TransitionTransactionAsync(
                    journal,
                    TransactionState.RecoveryRequired,
                    CancellationToken.None);
            }

            return await PerformRollbackAsync(journal, "Interrupted transaction recovery started.");
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public Task<IReadOnlyList<TransactionHistoryEntry>> ReadHistoryAsync(
        CancellationToken cancellationToken = default) =>
        historyService.ReadAsync(cancellationToken);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var cancellation in activeCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        activeCancellations.Clear();
        mutationLock.Dispose();
        disposed = true;
    }

    private async Task<TransactionExecutionResult> PerformRollbackAsync(
        TransactionJournal journal,
        string summary)
    {
        if (journal.State != TransactionState.RollingBack)
        {
            journal = await TransitionTransactionAsync(
                journal,
                TransactionState.RollingBack,
                CancellationToken.None,
                rollbackAttempted: true);
        }

        var rollbackFailed = false;
        var candidates = journal.Actions
            .Where(action =>
                action.Snapshot is not null
                && (action.MayHaveMutated
                    || action.State is ActionExecutionState.Applying
                        or ActionExecutionState.Applied
                        or ActionExecutionState.Verified
                        or ActionExecutionState.RollingBack
                        or ActionExecutionState.RollbackFailed))
            .OrderByDescending(action => action.Order)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (candidate.State == ActionExecutionState.RolledBack)
            {
                continue;
            }

            if (!registry.TryGet(candidate.ActionId, out var action)
                || action is null
                || action.Descriptor.SchemaVersion != candidate.ActionSchemaVersion
                || !candidate.Plan!.SupportsRollback)
            {
                rollbackFailed = true;
                var missingError = new ActionError(
                    "ROLLBACK_UNAVAILABLE",
                    $"Rollback is unavailable for {candidate.DisplayName}.",
                    "The action is missing, incompatible, or non-reversible.",
                    candidate.ActionId,
                    journal.TransactionId,
                    ActionExecutionStage.Rollback,
                    false,
                    false);
                journal = await EnsureRollbackFailureAsync(journal, candidate.Order, missingError);
                continue;
            }

            try
            {
                ActionSnapshotIntegrity.Validate(candidate.Snapshot!);
                if (journal.Actions[candidate.Order].State != ActionExecutionState.RollingBack)
                {
                    journal = await TransitionActionAsync(
                        journal,
                        candidate.Order,
                        ActionExecutionState.RollingBack,
                        CancellationToken.None);
                }

                Report(
                    journal.TransactionId,
                    action.Descriptor,
                    ActionExecutionStage.Rollback,
                    $"Rolling back {action.Descriptor.DisplayName}.",
                    candidates.Length - candidate.Order - 1,
                    candidates.Length,
                    isRollback: true);
                ActionRollbackResult rollback;
                try
                {
                    rollback = await action.RollbackAsync(
                        CreateContext(journal.TransactionId, false, journal.StartedAtUtc),
                        candidate.Plan,
                        candidate.Snapshot!,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    LogRollbackFailure(
                        logger,
                        journal.TransactionId,
                        candidate.ActionId,
                        exception);
                    rollback = ActionRollbackResult.Failure(
                        "Rollback failed unexpectedly.",
                        UnexpectedError(
                            journal.TransactionId,
                            candidate.ActionId,
                            ActionExecutionStage.Rollback,
                            exception,
                            rollbackPossible: false));
                }

                journal = await TransitionActionAsync(
                    journal,
                    candidate.Order,
                    rollback.Succeeded
                        ? ActionExecutionState.RolledBack
                        : ActionExecutionState.RollbackFailed,
                    CancellationToken.None,
                    error: rollback.Error);
                if (!rollback.Succeeded)
                {
                    rollbackFailed = true;
                    if (rollback.Error is not null)
                    {
                        journal = journal with { Errors = journal.Errors.Add(rollback.Error) };
                        await journalStore.SaveAsync(journal, CancellationToken.None);
                    }
                }
            }
            catch (Exception exception) when (exception is ActionEngineException)
            {
                rollbackFailed = true;
                var error = UnexpectedError(
                    journal.TransactionId,
                    candidate.ActionId,
                    ActionExecutionStage.Rollback,
                    exception,
                    rollbackPossible: false);
                journal = await EnsureRollbackFailureAsync(journal, candidate.Order, error);
            }
        }

        var finalState = rollbackFailed
            ? TransactionState.PartiallyRolledBack
            : TransactionState.RolledBack;
        journal = await TransitionTransactionAsync(
            journal,
            finalState,
            CancellationToken.None,
            complete: true);
        await journalStore.ArchiveAsync(journal, CancellationToken.None);
        return Result(
            journal,
            succeeded: false,
            rollbackFailed
                ? $"{summary} One or more actions could not be rolled back."
                : summary);
    }

    private async Task<TransactionJournal> EnsureRollbackFailureAsync(
        TransactionJournal journal,
        int actionIndex,
        ActionError error)
    {
        var state = journal.Actions[actionIndex].State;
        if (state != ActionExecutionState.RollingBack)
        {
            journal = await TransitionActionAsync(
                journal,
                actionIndex,
                ActionExecutionState.RollingBack,
                CancellationToken.None);
        }

        journal = await TransitionActionAsync(
            journal,
            actionIndex,
            ActionExecutionState.RollbackFailed,
            CancellationToken.None,
            error: error);
        journal = journal with { Errors = journal.Errors.Add(error) };
        await journalStore.SaveAsync(journal, CancellationToken.None);
        return journal;
    }

    private async Task<TransactionExecutionResult> CompletePreMutationCancellationAsync(
        TransactionJournal journal)
    {
        journal = await MarkCancellationRequestedAsync(journal);
        var error = new ActionError(
            "TRANSACTION_CANCELLED",
            "The transaction was cancelled before any changes were made.",
            "Cancellation was observed before the mutation pipeline.",
            null,
            journal.TransactionId,
            ActionExecutionStage.Apply,
            true,
            false);
        journal = journal with { Errors = journal.Errors.Add(error) };
        journal = await TransitionTransactionAsync(
            journal,
            TransactionState.Failed,
            CancellationToken.None,
            complete: true);
        await journalStore.ArchiveAsync(journal, CancellationToken.None);
        return Result(journal, false, error.UserMessage);
    }

    private async Task<TransactionJournal> MarkCancellationRequestedAsync(
        TransactionJournal journal)
    {
        if (journal.State == TransactionState.CancellationRequested)
        {
            return journal;
        }

        Report(
            journal.TransactionId,
            null,
            ActionExecutionStage.Apply,
            "Cancellation requested. Completing the current safe step.",
            0,
            journal.Actions.Length,
            isCancellationRequested: true);
        return await TransitionTransactionAsync(
            journal,
            TransactionState.CancellationRequested,
            CancellationToken.None);
    }

    private async Task FailPlanningJournalAsync(
        TransactionJournal journal,
        int actionIndex,
        Guid transactionId,
        ActionDescriptor descriptor,
        string code,
        string message,
        ActionExecutionStage stage)
    {
        var error = new ActionError(
            code,
            message,
            "Planning was interrupted before mutation.",
            descriptor.Id,
            transactionId,
            stage,
            true,
            false);
        journal = await TransitionActionAsync(
            journal,
            actionIndex,
            ActionExecutionState.Failed,
            CancellationToken.None,
            plan: journal.Actions[actionIndex].Plan ?? UnavailablePlan(descriptor, message),
            error: error);
        journal = journal with { Errors = journal.Errors.Add(error) };
        journal = await TransitionTransactionAsync(
            journal,
            TransactionState.Failed,
            CancellationToken.None,
            complete: true);
        await journalStore.ArchiveAsync(journal, CancellationToken.None);
    }

    private async Task<TransactionJournal> TransitionTransactionAsync(
        TransactionJournal journal,
        TransactionState state,
        CancellationToken cancellationToken,
        bool complete = false,
        bool? rollbackAttempted = null)
    {
        ActionStateMachine.EnsureTransition(journal.State, state);
        var updated = journal with
        {
            State = state,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = complete ? DateTimeOffset.UtcNow : journal.CompletedAtUtc,
            RollbackAttempted = rollbackAttempted ?? journal.RollbackAttempted,
        };
        await journalStore.SaveAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<TransactionJournal> TransitionActionAsync(
        TransactionJournal journal,
        int actionIndex,
        ActionExecutionState state,
        CancellationToken cancellationToken,
        ActionPlan? plan = null,
        ActionStateSnapshot? snapshot = null,
        bool? mayHaveMutated = null,
        ActionError? error = null)
    {
        var current = journal.Actions[actionIndex];
        ActionStateMachine.EnsureTransition(current.State, state);
        var updatedAction = current with
        {
            State = state,
            Plan = plan ?? current.Plan,
            Snapshot = snapshot ?? current.Snapshot,
            MayHaveMutated = mayHaveMutated ?? current.MayHaveMutated,
            Error = error ?? current.Error,
        };
        var updated = journal with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Actions = journal.Actions.SetItem(actionIndex, updatedAction),
        };
        await journalStore.SaveAsync(updated, cancellationToken);
        return updated;
    }

    private void ValidateExecutionPlan(
        ActionTransactionPlan plan,
        TransactionJournal journal)
    {
        if (plan.TransactionId != journal.TransactionId
            || plan.Actions.Length != journal.Actions.Length
            || journal.State != TransactionState.AwaitingConfirmation)
        {
            throw new ActionEngineException("Transaction plan does not match its journal.");
        }

        for (var index = 0; index < plan.Actions.Length; index++)
        {
            var supplied = plan.Actions[index];
            var persisted = journal.Actions[index];
            var registered = registry.GetRequired(supplied.Descriptor.Id);
            ValidatePlanCompatibility(registered.Descriptor, supplied.Plan);
            if (persisted.ActionId != supplied.Descriptor.Id
                || persisted.ActionSchemaVersion != supplied.Descriptor.SchemaVersion
                || persisted.Plan is null
                || !PlansEqual(persisted.Plan, supplied.Plan))
            {
                throw new ActionEngineException(
                    $"Action plan for '{supplied.Descriptor.Id}' is incompatible with its journal.");
            }
        }
    }

    private TransactionRecoveryInfo CreateRecoveryInfo(TransactionJournal journal)
    {
        var mutated = journal.Actions.Where(action =>
                action.MayHaveMutated
                || action.State is ActionExecutionState.Applying
                    or ActionExecutionState.Applied
                or ActionExecutionState.Verified
                or ActionExecutionState.RollingBack
                or ActionExecutionState.RollbackFailed).ToArray();
        var canRollback = mutated.Length > 0 && mutated.All(action =>
            action.Snapshot is not null
            && action.Plan?.SupportsRollback == true
            && registry.TryGet(action.ActionId, out var registered)
            && registered?.Descriptor.SchemaVersion == action.ActionSchemaVersion);
        return new(
            journal.TransactionId,
            journal.StartedAtUtc,
            journal.State,
            [.. journal.Actions.OrderBy(action => action.Order).Select(action => action.DisplayName)],
            [
                .. journal.Actions
                    .Where(action => action.State is ActionExecutionState.Applied or ActionExecutionState.Verified)
                    .Select(action => action.DisplayName),
            ],
            [
                .. journal.Actions
                    .Where(action => action.State == ActionExecutionState.Verified)
                    .Select(action => action.DisplayName),
            ],
            [
                .. journal.Actions
                    .Where(action => action.Snapshot is not null)
                    .Select(action => action.DisplayName),
            ],
            canRollback,
            journal.Actions.Any(action =>
                action.Plan?.RequiredPrivilege == ActionPrivilegeLevel.Administrator),
            mutated.Length > 0 && !canRollback,
            mutated.Length == 0
                ? "The transaction stopped before any recorded mutation."
                : canRollback
                    ? "Rollback snapshots are available for every action that may have changed state."
                    : "One or more actions cannot be rolled back automatically; manual intervention may be required.");
    }

    private static void ValidateRequest(ActionTransactionRequest request)
    {
        if (request.ActionIds.IsDefaultOrEmpty)
        {
            throw new ActionEngineException("A transaction must contain at least one action.");
        }

        if (request.ActionIds.Any(string.IsNullOrWhiteSpace)
            || request.ActionIds.Distinct(StringComparer.Ordinal).Count() != request.ActionIds.Length)
        {
            throw new ActionEngineException("Transaction action IDs must be non-empty and unique.");
        }
    }

    private static void ValidateDescriptor(ActionDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id)
            || descriptor.Id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
            || descriptor.SchemaVersion <= 0
            || string.IsNullOrWhiteSpace(descriptor.DisplayName)
            || string.IsNullOrWhiteSpace(descriptor.ShortDescription)
            || string.IsNullOrWhiteSpace(descriptor.DetailedExplanation)
            || descriptor.SupportedPlatforms.IsDefaultOrEmpty)
        {
            throw new ActionEngineException("Action descriptor metadata is invalid.");
        }
    }

    private static void ValidatePlanCompatibility(
        ActionDescriptor descriptor,
        ActionPlan plan)
    {
        if (!string.Equals(descriptor.Id, plan.ActionId, StringComparison.Ordinal)
            || descriptor.SchemaVersion != plan.ActionSchemaVersion
            || descriptor.RiskLevel != plan.RiskLevel
            || descriptor.RequiredPrivilege != plan.RequiredPrivilege
            || descriptor.RestartRequirement != plan.RestartRequirement
            || descriptor.SupportsRollback != plan.SupportsRollback)
        {
            throw new ActionEngineException(
                $"Plan for '{plan.ActionId}' is incompatible with the registered action definition.");
        }
    }

    private static ActionPlan UnavailablePlan(ActionDescriptor descriptor, string reason) =>
        new(
            descriptor.Id,
            descriptor.SchemaVersion,
            "Unknown",
            "Unchanged",
            [],
            [],
            descriptor.RiskLevel,
            descriptor.RequiredPrivilege,
            descriptor.RestartRequirement,
            descriptor.SupportsRollback,
            [],
            [reason],
            "The action cannot be planned safely.");

    private static ActionError ValidationError(
        Guid transactionId,
        string actionId,
        ActionValidationResult validation) =>
        new(
            "ACTION_VALIDATION_FAILED",
            "The action did not pass its safety checks.",
            string.Join("; ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")),
            actionId,
            transactionId,
            ActionExecutionStage.Validation,
            true,
            false);

    private static ActionError UnexpectedError(
        Guid transactionId,
        string? actionId,
        ActionExecutionStage stage,
        Exception exception,
        bool rollbackPossible) =>
        new(
            $"UNEXPECTED_{stage.ToString().ToUpperInvariant()}_FAILURE",
            "The operation encountered an unexpected error.",
            $"Unexpected {exception.GetType().Name} during {stage}.",
            actionId,
            transactionId,
            stage,
            true,
            rollbackPossible);

    private static bool PlansEqual(ActionPlan left, ActionPlan right)
    {
        var leftJson = JsonSerializer.SerializeToElement(left, ActionJson.Options);
        var rightJson = JsonSerializer.SerializeToElement(right, ActionJson.Options);
        return JsonElement.DeepEquals(leftJson, rightJson);
    }

    private ActionExecutionContext CreateContext(
        Guid transactionId,
        bool isDryRun,
        DateTimeOffset startedAtUtc) =>
        new(
            transactionId,
            isDryRun,
            startedAtUtc,
            update => ProgressChanged?.Invoke(this, update));

    private void Report(
        Guid transactionId,
        ActionDescriptor? descriptor,
        ActionExecutionStage stage,
        string message,
        int completedActions,
        int totalActions,
        bool isRollback = false,
        bool isCancellationRequested = false) =>
        ProgressChanged?.Invoke(this, new(
            transactionId,
            descriptor?.Id,
            descriptor?.DisplayName,
            stage,
            message,
            completedActions,
            totalActions,
            isRollback,
            isCancellationRequested,
            DateTimeOffset.UtcNow));

    private static TransactionExecutionResult Result(
        TransactionJournal journal,
        bool succeeded,
        string summary) =>
        new(
            journal.TransactionId,
            succeeded,
            journal.IsDryRun,
            journal.State,
            journal.RollbackAttempted,
            journal.RestartRequirement,
            journal.Errors,
            summary);

    private static TransactionExecutionResult BusyResult(ActionTransactionPlan plan)
    {
        var error = new ActionError(
            "MUTATION_TRANSACTION_BUSY",
            "Another system transaction is already running.",
            "The process-wide mutation semaphore is held.",
            null,
            plan.TransactionId,
            ActionExecutionStage.Apply,
            true,
            false);
        return new(
            plan.TransactionId,
            false,
            plan.IsDryRun,
            TransactionState.Failed,
            false,
            plan.RestartRequirement,
            [error],
            error.UserMessage);
    }
}
