using System.Text.Json;
using Kernctl.Core.Actions;

namespace Kernctl.Core.Tests;

public sealed class ActionJournalAndRecoveryTests
{
    [Fact]
    public async Task JournalSerializationIsVersionedReadableAndAtomic()
    {
        var action = new TestSystemAction("journal");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);

        var journal = await fixture.Store.LoadAsync(
            plan.TransactionId,
            TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(journal, ActionJson.Options);
        var roundTrip = JsonSerializer.Deserialize<TransactionJournal>(
            json,
            ActionJson.Options);

        Assert.NotNull(roundTrip);
        Assert.Equal(TransactionJournal.CurrentSchemaVersion, roundTrip.JournalSchemaVersion);
        Assert.Equal(plan.TransactionId, roundTrip.TransactionId);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories),
            _ => true);
    }

    [Fact]
    public async Task MalformedJournalIsReportedWithoutBeingLoaded()
    {
        var root = CreateRoot();
        try
        {
            var active = Path.Combine(root, "active");
            Directory.CreateDirectory(active);
            await File.WriteAllTextAsync(
                Path.Combine(active, $"{Guid.NewGuid():N}.json"),
                "{bad",
                TestContext.Current.CancellationToken);
            var store = new FileActionJournalStore(new(root));

            var scan = await store.ScanActiveAsync(TestContext.Current.CancellationToken);

            Assert.Empty(scan.Journals);
            Assert.Single(scan.Errors);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IncompatibleJournalSchemaIsRejectedClearly()
    {
        var action = new TestSystemAction("schema");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var activePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Root, "active"),
            "*.json"));
        var json = await File.ReadAllTextAsync(
            activePath,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            activePath,
            json.Replace(
                "\"journalSchemaVersion\": 1",
                "\"journalSchemaVersion\": 999",
                StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ActionEngineException>(
            () => fixture.Store.LoadAsync(
                plan.TransactionId,
                TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteTransactionsAreDiscoveredWithSafeRecoveryDetails()
    {
        var action = new TestSystemAction("incomplete");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);

        var recoveries = await fixture.Engine.InspectIncompleteAsync(
            TestContext.Current.CancellationToken);

        var recovery = Assert.Single(recoveries);
        Assert.Equal(plan.TransactionId, recovery.TransactionId);
        Assert.Equal(TransactionState.AwaitingConfirmation, recovery.State);
        Assert.False(recovery.CanRollback);
        Assert.False(recovery.ManualInterventionMayBeRequired);
    }

    [Fact]
    public async Task InterruptedApplyCanRecoverFromPersistedSnapshot()
    {
        var action = new TestSystemAction("recover");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var journal = await fixture.Store.LoadAsync(
            plan.TransactionId,
            TestContext.Current.CancellationToken);
        var snapshot = ActionSnapshotIntegrity.Create(
            plan.TransactionId,
            action.Descriptor,
            ActionStatePayload.From(1, new { value = 0 }),
            DateTimeOffset.UtcNow);
        action.Value = 1;
        var interrupted = journal with
        {
            State = TransactionState.Applying,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Actions = journal.Actions.SetItem(
                0,
                journal.Actions[0] with
                {
                    State = ActionExecutionState.Applying,
                    Snapshot = snapshot,
                    MayHaveMutated = false,
                }),
        };
        await fixture.Store.SaveAsync(interrupted, TestContext.Current.CancellationToken);

        var recovery = Assert.Single(await fixture.Engine.InspectIncompleteAsync(
            TestContext.Current.CancellationToken));
        var result = await fixture.Engine.RecoverAsync(
            recovery.TransactionId,
            TestContext.Current.CancellationToken);

        Assert.True(recovery.CanRollback);
        Assert.Equal(TransactionState.RolledBack, result.FinalState);
        Assert.Equal(0, action.Value);
        Assert.Contains("rollback:recover", action.Operations);
    }

    [Fact]
    public async Task HistoryRetentionKeepsOnlyConfiguredNumberOfTransactions()
    {
        var root = CreateRoot();
        try
        {
            var action = new TestSystemAction("retention");
            var store = new FileActionJournalStore(new(root, HistoryRetention: 2));
            var registry = new ActionRegistry([action]);
            var history = new ActionHistoryService(store);
            using var engine = new ActionTransactionEngine(registry, store, history);
            for (var index = 0; index < 3; index++)
            {
                await engine.DryRunAsync(
                    new ActionTransactionRequest([action.Descriptor.Id]),
                    TestContext.Current.CancellationToken);
            }

            var entries = await history.ReadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, entries.Count);
            Assert.Equal(
                2,
                Directory.EnumerateFiles(Path.Combine(root, "archive"), "*.json").Count());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SecretLikeAndPolymorphicMetadataFieldsAreRejectedFromSnapshots()
    {
        var descriptor = TestSystemAction.CreateDescriptor("secrets");

        Assert.Throws<ActionEngineException>(() => ActionSnapshotIntegrity.Create(
            Guid.NewGuid(),
            descriptor,
            ActionStatePayload.From(1, new { password = "never-store-this" }),
            DateTimeOffset.UtcNow));
        Assert.Throws<ActionEngineException>(() => ActionSnapshotIntegrity.Create(
            Guid.NewGuid(),
            descriptor,
            new ActionStatePayload(
                1,
                JsonSerializer.SerializeToElement(
                    new Dictionary<string, string> { ["$type"] = "Unsafe.Type" })),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SerializedJournalContainsNoSecretOrUnsafeTypeMetadata()
    {
        var action = new TestSystemAction("sanitized");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        await fixture.Engine.ExecuteAsync(plan, TestContext.Current.CancellationToken);
        var archivePath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(fixture.Root, "archive"),
            "*.json"));
        var json = await File.ReadAllTextAsync(
            archivePath,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineRejectsPlanWhoseActionDefinitionVersionChanged()
    {
        var action = new TestSystemAction("versioned");
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var alteredAction = plan.Actions[0] with
        {
            Descriptor = plan.Actions[0].Descriptor with { SchemaVersion = 2 },
        };
        var alteredPlan = plan with { Actions = plan.Actions.SetItem(0, alteredAction) };

        await Assert.ThrowsAsync<ActionEngineException>(
            () => fixture.Engine.ExecuteAsync(
                alteredPlan,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StructuredFailureSeparatesSafeAndTechnicalMessages()
    {
        var action = new TestSystemAction("structured", failApply: true);
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);

        var result = await fixture.Engine.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Equal("TEST_APPLY_FAILURE", error.Code);
        Assert.Equal("A deterministic test action failed.", error.UserMessage);
        Assert.Equal("Test failure at Apply.", error.TechnicalDiagnostic);
        Assert.Equal(ActionExecutionStage.Apply, error.Stage);
        Assert.True(error.RetryPossible);
    }

    private static string CreateRoot() => Path.Combine(
        Path.GetTempPath(),
        $"kernctl-action-journal-tests-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
