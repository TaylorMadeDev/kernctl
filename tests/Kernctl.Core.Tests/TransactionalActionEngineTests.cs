using Kernctl.Core.Actions;

namespace Kernctl.Core.Tests;

public sealed class TransactionalActionEngineTests
{
    [Fact]
    public void StateMachinesAcceptExpectedTransitionsAndRejectInvalidOnes()
    {
        Assert.True(ActionStateMachine.CanTransition(
            TransactionState.Applying,
            TransactionState.Verifying));
        Assert.True(ActionStateMachine.CanTransition(
            ActionExecutionState.Applying,
            ActionExecutionState.Applied));

        Assert.Throws<InvalidStateTransitionException>(
            () => ActionStateMachine.EnsureTransition(
                TransactionState.Created,
                TransactionState.Committed));
        Assert.Throws<InvalidStateTransitionException>(
            () => ActionStateMachine.EnsureTransition(
                ActionExecutionState.Pending,
                ActionExecutionState.Verified));
    }

    [Fact]
    public async Task SuccessfulSingleActionIsAppliedVerifiedCommittedAndHistorized()
    {
        var action = new TestSystemAction("single");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);
        var history = await fixture.Engine.ReadHistoryAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(TransactionState.Committed, result.FinalState);
        Assert.Equal(1, action.Value);
        Assert.Equal(
            ["detect:single", "plan:single", "validate:single", "validate:single", "capture:single", "apply:single", "verify:single"],
            action.Operations);
        Assert.Equal(TransactionState.Committed, Assert.Single(history).FinalState);
    }

    [Fact]
    public async Task MultipleActionsExecuteInDeclaredOrder()
    {
        var operations = new List<string>();
        var first = new TestSystemAction("first", operations);
        var second = new TestSystemAction("second", operations);
        using var fixture = new ActionEngineTestFixture(first, second);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            first.Descriptor.Id,
            second.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(
            operations.IndexOf("apply:first") < operations.IndexOf("apply:second"));
        Assert.True(
            operations.IndexOf("verify:first") < operations.IndexOf("apply:second"));
    }

    [Fact]
    public async Task VerificationFailureRollsBackInReverseOrder()
    {
        var operations = new List<string>();
        var first = new TestSystemAction("first", operations);
        var second = new TestSystemAction("second", operations, failVerification: true);
        using var fixture = new ActionEngineTestFixture(first, second);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            first.Descriptor.Id,
            second.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(TransactionState.RolledBack, result.FinalState);
        Assert.Equal(0, first.Value);
        Assert.Equal(0, second.Value);
        Assert.True(
            operations.IndexOf("rollback:second") < operations.IndexOf("rollback:first"));
    }

    [Fact]
    public async Task SnapshotIsPersistedBeforeApplyBegins()
    {
        var snapshotWasPersisted = false;
        ActionEngineTestFixture? fixture = null;
        TestSystemAction? action = null;
        action = new TestSystemAction(
            "snapshot-order",
            onApply: () =>
            {
                var activeDirectory = Path.Combine(fixture!.Root, "active");
                var journalPath = Assert.Single(Directory.EnumerateFiles(activeDirectory, "*.json"));
                var json = File.ReadAllText(journalPath);
                snapshotWasPersisted =
                    json.Contains("\"snapshot\":", StringComparison.Ordinal)
                    && json.Contains("\"snapshotPersisted\"", StringComparison.Ordinal)
                        || json.Contains("\"applying\"", StringComparison.Ordinal);
            });
        using (fixture = new ActionEngineTestFixture(action))
        {
            var plan = await fixture.PlanAsync(
                TestContext.Current.CancellationToken,
                action.Descriptor.Id);
            var result = await fixture.Engine.ExecuteAsync(
                plan,
                TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
            Assert.True(snapshotWasPersisted);
        }
    }

    [Fact]
    public async Task ApplyFailureRollsBackPreviouslyAppliedActions()
    {
        var operations = new List<string>();
        var first = new TestSystemAction("first", operations);
        var failing = new TestSystemAction("failing", operations, failApply: true);
        using var fixture = new ActionEngineTestFixture(first, failing);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            first.Descriptor.Id,
            failing.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, first.Value);
        Assert.DoesNotContain("rollback:failing", operations);
        Assert.Contains("rollback:first", operations);
    }

    [Fact]
    public async Task PartialApplyFailureAttemptsRollbackOfFailingAction()
    {
        var action = new TestSystemAction(
            "partial",
            failApply: true,
            partiallyMutateOnApplyFailure: true);
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.RolledBack, result.FinalState);
        Assert.Equal(0, action.Value);
        Assert.Contains("rollback:partial", action.Operations);
    }

    [Fact]
    public async Task RollbackFailureDoesNotPreventRemainingReverseRollbacks()
    {
        var operations = new List<string>();
        var first = new TestSystemAction("first", operations);
        var rollbackFails = new TestSystemAction(
            "rollback-fails",
            operations,
            failVerification: true,
            failRollback: true);
        using var fixture = new ActionEngineTestFixture(first, rollbackFails);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            first.Descriptor.Id,
            rollbackFails.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.PartiallyRolledBack, result.FinalState);
        Assert.Contains("rollback:rollback-fails", operations);
        Assert.Contains("rollback:first", operations);
        Assert.Equal(0, first.Value);
        Assert.Equal(1, rollbackFails.Value);
    }

    [Fact]
    public async Task DryRunNeverCapturesAppliesVerifiesOrRollsBack()
    {
        var action = new TestSystemAction("dry-run");
        using var fixture = new ActionEngineTestFixture(action);

        var result = await fixture.Engine.DryRunAsync(
            new ActionTransactionRequest([action.Descriptor.Id]),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.IsDryRun);
        Assert.Equal(0, action.Value);
        Assert.DoesNotContain(action.Operations, operation =>
            operation.StartsWith("capture:", StringComparison.Ordinal)
            || operation.StartsWith("apply:", StringComparison.Ordinal)
            || operation.StartsWith("verify:", StringComparison.Ordinal)
            || operation.StartsWith("rollback:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationBeforeMutationProducesSafeTerminalResult()
    {
        var action = new TestSystemAction("cancel-before");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fixture.Engine.ExecuteAsync(plan, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(TransactionState.Failed, result.FinalState);
        Assert.Equal(0, action.Value);
        Assert.DoesNotContain("apply:cancel-before", action.Operations);
    }

    [Fact]
    public async Task CancellationDuringApplyUsesIndependentRollback()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var action = new TestSystemAction(
            "cancel-during",
            partiallyMutateOnApplyFailure: true,
            applyEntered: entered,
            applyRelease: neverRelease);
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var execution = fixture.Engine.ExecuteAsync(plan, CancellationToken.None);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Engine.RequestCancellation(plan.TransactionId));
        var result = await execution;

        Assert.Equal(TransactionState.RolledBack, result.FinalState);
        Assert.True(result.RollbackAttempted);
        Assert.Equal(0, action.Value);
    }

    [Fact]
    public async Task OnlyOneMutatingTransactionCanRunAtATime()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestSystemAction(
            "long-running",
            applyEntered: entered,
            applyRelease: release);
        var second = new TestSystemAction("second");
        using var fixture = new ActionEngineTestFixture(first, second);
        var firstPlan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            first.Descriptor.Id);
        var secondPlan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            second.Descriptor.Id);
        var firstExecution = fixture.Engine.ExecuteAsync(firstPlan, CancellationToken.None);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var busy = await fixture.Engine.ExecuteAsync(
            secondPlan,
            TestContext.Current.CancellationToken);
        release.TrySetResult();
        var completed = await firstExecution;

        Assert.False(busy.Succeeded);
        Assert.Contains(busy.Errors, error => error.Code == "MUTATION_TRANSACTION_BUSY");
        Assert.True(completed.Succeeded);
        Assert.DoesNotContain("apply:second", second.Operations);
    }

    [Fact]
    public async Task NonReversibleAppliedActionProducesPartialRollback()
    {
        var first = new TestSystemAction("non-reversible", supportsRollback: false);
        var failing = new TestSystemAction("later-failure", failVerification: true);
        using var fixture = new ActionEngineTestFixture(first, failing);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            first.Descriptor.Id,
            failing.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.PartiallyRolledBack, result.FinalState);
        Assert.Equal(1, first.Value);
        Assert.Contains(result.Errors, error => error.Code == "ROLLBACK_UNAVAILABLE");
    }

    [Fact]
    public async Task CommittedTransactionCanBeExplicitlyRolledBackFromArchive()
    {
        var action = new TestSystemAction("manual-rollback");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var committed = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        var rolledBack = await fixture.Engine.RollbackAsync(
            plan.TransactionId,
            TestContext.Current.CancellationToken);
        var history = await fixture.Engine.ReadHistoryAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionState.Committed, committed.FinalState);
        Assert.Equal(TransactionState.RolledBack, rolledBack.FinalState);
        Assert.Equal(0, action.Value);
        Assert.True(rolledBack.RollbackAttempted);
        Assert.Equal(TransactionState.RolledBack, Assert.Single(history).FinalState);
    }

    [Fact]
    public async Task ProgressEventsFollowLifecycleOrder()
    {
        var action = new TestSystemAction("progress");
        using var fixture = new ActionEngineTestFixture(action);
        var stages = new List<ActionExecutionStage>();
        fixture.Engine.ProgressChanged += (_, update) => stages.Add(update.Stage);

        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        await fixture.Engine.ExecuteAsync(plan, TestContext.Current.CancellationToken);

        AssertOrdered(
            stages,
            ActionExecutionStage.Detection,
            ActionExecutionStage.Planning,
            ActionExecutionStage.Validation,
            ActionExecutionStage.Snapshot,
            ActionExecutionStage.Apply,
            ActionExecutionStage.Verification);
    }

    [Fact]
    public async Task PrivilegeAndRestartMetadataFlowIntoPlanAndResult()
    {
        var action = new TestSystemAction(
            "admin-restart",
            privilege: ActionPrivilegeLevel.Administrator,
            restart: ActionRestartRequirement.SystemRestart);
        using var fixture = new ActionEngineTestFixture(action);

        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ActionPrivilegeLevel.Administrator,
            Assert.Single(plan.Actions).Plan.RequiredPrivilege);
        Assert.Equal(ActionRestartRequirement.SystemRestart, plan.RestartRequirement);
        Assert.Equal(ActionRestartRequirement.SystemRestart, result.RestartRequirement);
    }

    private static void AssertOrdered(
        List<ActionExecutionStage> actual,
        params ActionExecutionStage[] expected)
    {
        var previous = -1;
        foreach (var stage in expected)
        {
            var index = actual.FindIndex(previous + 1, candidate => candidate == stage);
            Assert.True(index > previous, $"Expected {stage} after index {previous}.");
            previous = index;
        }
    }
}
