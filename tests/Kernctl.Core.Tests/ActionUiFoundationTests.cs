using Kernctl.App.ViewModels.Actions;
using Kernctl.Core.Actions;

namespace Kernctl.Core.Tests;

public sealed class ActionUiFoundationTests
{
    [Fact]
    public async Task ReviewSurfaceExposesSafetyMetadataAndCanConvertPlanToDryRun()
    {
        var action = new TestSystemAction(
            "review",
            risk: ActionRiskLevel.High,
            privilege: ActionPrivilegeLevel.Administrator,
            restart: ActionRestartRequirement.SignOut);
        using var fixture = new ActionEngineTestFixture(action);
        var plan = await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var viewModel = new ActionReviewDialogViewModel(fixture.Engine);

        viewModel.Open(plan);

        var item = Assert.Single(viewModel.Actions);
        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.HasHighRiskActions);
        Assert.True(viewModel.RequiresAdministrator);
        Assert.True(item.IsHighRisk);
        Assert.Equal("SignOut", item.Restart);

        viewModel.RunAsDryRun = true;
        await viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.Result);
        Assert.True(viewModel.Result.IsDryRun);
        Assert.True(viewModel.Result.Succeeded);
        Assert.Equal(0, action.Value);
        Assert.DoesNotContain("apply:review", action.Operations);
    }

    [Fact]
    public async Task StartupRecoveryViewModelShowsIncompleteJournalWithoutAutoContinuing()
    {
        var action = new TestSystemAction("recovery-ui");
        using var fixture = new ActionEngineTestFixture(action);
        await fixture.PlanAsync(
            TestContext.Current.CancellationToken,
            action.Descriptor.Id);
        var viewModel = new ActionRecoveryViewModel(fixture.Engine);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        var item = Assert.Single(viewModel.Recoveries);
        Assert.True(viewModel.IsOpen);
        Assert.False(item.CanRecover);
        Assert.Contains("stopped before", item.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, action.Value);

        viewModel.DismissCommand.Execute(null);
        Assert.False(viewModel.IsOpen);
    }
}
