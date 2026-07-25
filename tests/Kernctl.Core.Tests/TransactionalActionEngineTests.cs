using Kernctl.Core.Actions;

namespace Kernctl.Core.Tests;

public sealed class TransactionalActionEngineTests
{
    [Fact]
    public async Task VerificationFailureRollsBackAppliedActionsInReverseOrder()
    {
        var operations = new List<string>();
        var first = new RecordingAction("first", operations, verificationSucceeds: true);
        var second = new RecordingAction("second", operations, verificationSucceeds: false);
        var engine = new TransactionalActionEngine();

        var result = await engine.ApplyAsync([first, second], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            [
                "detect:first",
                "explain:first",
                "apply:first",
                "verify:first",
                "detect:second",
                "explain:second",
                "apply:second",
                "verify:second",
                "undo:second",
                "undo:first",
            ],
            operations);
    }

    [Fact]
    public async Task InapplicableActionsAreSkipped()
    {
        var operations = new List<string>();
        var action = new RecordingAction(
            "not-applicable",
            operations,
            verificationSucceeds: true,
            isApplicable: false);
        var engine = new TransactionalActionEngine();

        var result = await engine.ApplyAsync([action], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["detect:not-applicable"], operations);
    }

    private sealed class RecordingAction(
        string id,
        List<string> operations,
        bool verificationSucceeds,
        bool isApplicable = true) : ISystemAction
    {
        public string Id => id;

        public PrivilegeRequirement RequiredPrivilege => PrivilegeRequirement.StandardUser;

        public ValueTask<DetectionResult> DetectAsync(CancellationToken cancellationToken)
        {
            operations.Add($"detect:{id}");
            return ValueTask.FromResult(new DetectionResult(isApplicable, "test"));
        }

        public ValueTask<ActionExplanation> ExplainAsync(CancellationToken cancellationToken)
        {
            operations.Add($"explain:{id}");
            return ValueTask.FromResult(new ActionExplanation("test", "none", "undo"));
        }

        public ValueTask<ActionResult> ApplyAsync(CancellationToken cancellationToken)
        {
            operations.Add($"apply:{id}");
            return ValueTask.FromResult(new ActionResult(true, "applied", false));
        }

        public ValueTask<ActionResult> VerifyAsync(CancellationToken cancellationToken)
        {
            operations.Add($"verify:{id}");
            return ValueTask.FromResult(
                new ActionResult(verificationSucceeds, "verification failed", false));
        }

        public ValueTask<ActionResult> UndoAsync(CancellationToken cancellationToken)
        {
            operations.Add($"undo:{id}");
            return ValueTask.FromResult(new ActionResult(true, "undone", false));
        }
    }
}
