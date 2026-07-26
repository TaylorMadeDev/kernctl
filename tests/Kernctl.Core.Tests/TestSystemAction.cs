using System.Text.Json;
using Kernctl.Core.Actions;

namespace Kernctl.Core.Tests;

internal sealed class TestSystemAction : ISystemAction
{
    private readonly List<string> operations;
    private readonly TaskCompletionSource? applyEntered;
    private readonly TaskCompletionSource? applyRelease;
    private readonly Action? onApply;

    public TestSystemAction(
        string id,
        List<string>? operations = null,
        bool failApply = false,
        bool partiallyMutateOnApplyFailure = false,
        bool failVerification = false,
        bool failRollback = false,
        bool supportsRollback = true,
        ActionRiskLevel risk = ActionRiskLevel.Low,
        ActionPrivilegeLevel privilege = ActionPrivilegeLevel.StandardUser,
        ActionRestartRequirement restart = ActionRestartRequirement.None,
        TaskCompletionSource? applyEntered = null,
        TaskCompletionSource? applyRelease = null,
        Action? onApply = null)
    {
        this.operations = operations ?? [];
        this.applyEntered = applyEntered;
        this.applyRelease = applyRelease;
        this.onApply = onApply;
        FailApply = failApply;
        PartiallyMutateOnApplyFailure = partiallyMutateOnApplyFailure;
        FailVerification = failVerification;
        FailRollback = failRollback;
        Descriptor = CreateDescriptor(id, supportsRollback, risk, privilege, restart);
    }

    public ActionDescriptor Descriptor { get; }

    public bool FailApply { get; }

    public bool PartiallyMutateOnApplyFailure { get; }

    public bool FailVerification { get; }

    public bool FailRollback { get; }

    public int Value { get; set; }

    public IReadOnlyList<string> Operations => operations;

    public Task<ActionDetectionResult> DetectAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Record("detect");
        return Task.FromResult(ActionDetectionResult.Available(
            Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Test state detected."));
    }

    public Task<ActionPlan> PlanAsync(
        ActionExecutionContext context,
        ActionDetectionResult detection,
        CancellationToken cancellationToken)
    {
        Record("plan");
        return Task.FromResult(new ActionPlan(
            Descriptor.Id,
            Descriptor.SchemaVersion,
            detection.CurrentState,
            "1",
            [new PlannedOperation("Set in-memory value", "Changes only deterministic test state.")],
            [$"memory:{Descriptor.Id}"],
            Descriptor.RiskLevel,
            Descriptor.RequiredPrivilege,
            Descriptor.RestartRequirement,
            Descriptor.SupportsRollback,
            [],
            [],
            "Set a deterministic in-memory value for engine testing."));
    }

    public Task<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        Record("validate");
        return Task.FromResult(ActionValidationResult.Valid);
    }

    public Task<ActionStatePayload> CaptureStateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        Record("capture");
        return Task.FromResult(ActionStatePayload.From(1, new SnapshotValue(Value)));
    }

    public async Task<ActionApplyResult> ApplyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        Record("apply");
        onApply?.Invoke();
        if (PartiallyMutateOnApplyFailure)
        {
            Value = 1;
        }

        applyEntered?.TrySetResult();
        if (applyRelease is not null)
        {
            await applyRelease.Task.WaitAsync(cancellationToken);
        }

        if (FailApply)
        {
            return ActionApplyResult.Failure(
                "The deterministic apply failed.",
                CreateError(
                    context,
                    "TEST_APPLY_FAILURE",
                    ActionExecutionStage.Apply,
                    Descriptor.SupportsRollback),
                PartiallyMutateOnApplyFailure);
        }

        Value = 1;
        return ActionApplyResult.Success("Applied deterministic test state.");
    }

    public Task<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        Record("verify");
        return Task.FromResult(
            FailVerification
                ? ActionVerificationResult.Failure(
                    "The deterministic verification failed.",
                    CreateError(
                        context,
                        "TEST_VERIFICATION_FAILURE",
                        ActionExecutionStage.Verification,
                        Descriptor.SupportsRollback))
                : ActionVerificationResult.Success("Deterministic test state verified."));
    }

    public Task<ActionRollbackResult> RollbackAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        ActionStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Record("rollback");
        if (FailRollback)
        {
            return Task.FromResult(ActionRollbackResult.Failure(
                "The deterministic rollback failed.",
                CreateError(
                    context,
                    "TEST_ROLLBACK_FAILURE",
                    ActionExecutionStage.Rollback,
                    false)));
        }

        var original = snapshot.OriginalState.Deserialize<SnapshotValue>(ActionJson.Options)
            ?? throw new InvalidOperationException("Test snapshot was empty.");
        Value = original.Value;
        return Task.FromResult(ActionRollbackResult.Success("Deterministic state restored."));
    }

    public static ActionDescriptor CreateDescriptor(
        string id,
        bool supportsRollback = true,
        ActionRiskLevel risk = ActionRiskLevel.Low,
        ActionPrivilegeLevel privilege = ActionPrivilegeLevel.StandardUser,
        ActionRestartRequirement restart = ActionRestartRequirement.None) =>
        new(
            id,
            1,
            $"Test {id}",
            "A deterministic test action.",
            "This action changes only in-memory state owned by the test process.",
            SystemActionCategory.Other,
            risk,
            privilege,
            restart,
            [ActionPlatform.Windows],
            supportsRollback,
            true,
            TimeSpan.FromMilliseconds(10));

    private ActionError CreateError(
        ActionExecutionContext context,
        string code,
        ActionExecutionStage stage,
        bool rollbackPossible) =>
        new(
            code,
            "A deterministic test action failed.",
            $"Test failure at {stage}.",
            Descriptor.Id,
            context.TransactionId,
            stage,
            true,
            rollbackPossible);

    private void Record(string operation)
    {
        lock (operations)
        {
            operations.Add($"{operation}:{Descriptor.Id}");
        }
    }

    private sealed record SnapshotValue(int Value);
}

internal sealed class ActionEngineTestFixture : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kernctl-action-engine-tests-{Guid.NewGuid():N}");

    public ActionEngineTestFixture(params ISystemAction[] actions)
        : this(new UnavailableActionPrivilegeBroker(), actions)
    {
    }

    public ActionEngineTestFixture(
        IActionPrivilegeBroker privilegeBroker,
        params ISystemAction[] actions)
    {
        Store = new FileActionJournalStore(new(root, HistoryRetention: 10));
        Registry = new ActionRegistry(actions);
        History = new ActionHistoryService(Store);
        Engine = new ActionTransactionEngine(
            Registry,
            Store,
            History,
            logger: null,
            privilegeBroker: privilegeBroker);
    }

    public string Root => root;

    public FileActionJournalStore Store { get; }

    public ActionRegistry Registry { get; }

    public ActionHistoryService History { get; }

    public ActionTransactionEngine Engine { get; }

    public Task<ActionTransactionPlan> PlanAsync(
        CancellationToken cancellationToken = default,
        params string[] actionIds) =>
        Engine.PlanAsync(
            new ActionTransactionRequest([.. actionIds]),
            cancellationToken);

    public void Dispose()
    {
        Engine.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class FakeActionPrivilegeBroker(
    ActionPrivilegeOpenStatus status = ActionPrivilegeOpenStatus.Ready)
    : IActionPrivilegeBroker
{
    public ActionPrivilegeOpenStatus Status { get; set; } = status;

    public int OpenCount { get; private set; }

    public bool SessionDisposed { get; private set; }

    public List<ActionPrivilegeSessionRequest> Requests { get; } = [];

    public Task<ActionPrivilegeOpenResult> OpenAsync(
        ActionPrivilegeSessionRequest request,
        IProgress<ActionPrivilegeBrokerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        OpenCount++;
        Requests.Add(request);
        progress?.Report(new(
            ActionPrivilegeBrokerState.AwaitingConsent,
            "Awaiting Windows administrator consent."));
        return Task.FromResult(Status switch
        {
            ActionPrivilegeOpenStatus.Ready =>
                ActionPrivilegeOpenResult.Ready(new Session(this)),
            ActionPrivilegeOpenStatus.Cancelled =>
                ActionPrivilegeOpenResult.Cancelled(new(
                    "ELEVATION_CANCELLED",
                    "Administrator permission was declined. No changes were made.",
                    RetryPossible: true)),
            _ => ActionPrivilegeOpenResult.Failed(new(
                "ELEVATION_FAILED",
                "Administrator permission could not be prepared.",
                RetryPossible: true)),
        });
    }

    private sealed class Session(FakeActionPrivilegeBroker owner) : IActionPrivilegeSession
    {
        public ValueTask DisposeAsync()
        {
            owner.SessionDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
