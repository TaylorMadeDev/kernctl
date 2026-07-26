using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.Core.Actions;

namespace Kernctl.App.ViewModels.Actions;

public sealed class ActionReviewDialogViewModel : ObservableObject
{
    private readonly IActionTransactionEngine engine;
    private ActionTransactionPlan? plan;
    private bool isOpen;
    private bool runAsDryRun;
    private string statusMessage = string.Empty;

    public ActionReviewDialogViewModel(IActionTransactionEngine engine)
    {
        this.engine = engine;
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => Plan is not null);
        CancelCommand = new RelayCommand(Cancel);
    }

    public ObservableCollection<ActionReviewItemViewModel> Actions { get; } = [];

    public IAsyncRelayCommand ConfirmCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public ActionTransactionPlan? Plan
    {
        get => plan;
        private set
        {
            if (SetProperty(ref plan, value))
            {
                ConfirmCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(RestartRequirement));
                OnPropertyChanged(nameof(RequiresAdministrator));
                OnPropertyChanged(nameof(HasHighRiskActions));
            }
        }
    }

    public bool IsOpen
    {
        get => isOpen;
        private set => SetProperty(ref isOpen, value);
    }

    public bool RunAsDryRun
    {
        get => runAsDryRun;
        set => SetProperty(ref runAsDryRun, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string RestartRequirement => Plan?.RestartRequirement.ToString() ?? "None";

    public bool RequiresAdministrator => Plan?.Actions.Any(action =>
        action.Plan.RequiredPrivilege == ActionPrivilegeLevel.Administrator) == true;

    public bool HasHighRiskActions => Plan?.Actions.Any(action =>
        action.Plan.RiskLevel == ActionRiskLevel.High) == true;

    public TransactionExecutionResult? Result { get; private set; }

    public void Open(ActionTransactionPlan transactionPlan)
    {
        ArgumentNullException.ThrowIfNull(transactionPlan);
        Plan = transactionPlan;
        Actions.Clear();
        foreach (var action in transactionPlan.Actions)
        {
            Actions.Add(new(action));
        }

        RunAsDryRun = transactionPlan.IsDryRun;
        StatusMessage = string.Empty;
        Result = null;
        IsOpen = true;
    }

    private async Task ConfirmAsync()
    {
        if (Plan is null)
        {
            return;
        }

        Result = RunAsDryRun
            ? await engine.DryRunAsync(Plan)
            : await engine.ExecuteAsync(Plan);
        StatusMessage = Result.Summary;
        OnPropertyChanged(nameof(Result));
        IsOpen = false;
    }

    private void Cancel()
    {
        IsOpen = false;
        StatusMessage = "Action review cancelled. No changes were made.";
    }
}

public sealed record ActionReviewItemViewModel(PlannedAction Action)
{
    public string Name => Action.Descriptor.DisplayName;

    public string Description => Action.Descriptor.ShortDescription;

    public string Explanation => Action.Plan.UserExplanation;

    public string Risk => Action.Plan.RiskLevel.ToString();

    public string Privilege => Action.Plan.RequiredPrivilege.ToString();

    public string Restart => Action.Plan.RestartRequirement.ToString();

    public string Rollback => Action.Plan.SupportsRollback ? "Rollback available" : "No automatic rollback";

    public bool IsHighRisk => Action.Plan.RiskLevel == ActionRiskLevel.High;

    public IReadOnlyList<string> AffectedResources => Action.Plan.AffectedResources;

    public IReadOnlyList<string> Warnings => Action.Plan.Warnings;

    public string AffectedResourcesText => AffectedResources.Count == 0
        ? "Resources: none declared"
        : $"Resources: {string.Join(", ", AffectedResources)}";

    public string WarningsText => string.Join(Environment.NewLine, Warnings);

    public bool HasWarnings => Warnings.Count > 0;
}
