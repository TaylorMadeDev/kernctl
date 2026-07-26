using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.Core.Actions;

namespace Kernctl.App.ViewModels.Actions;

public sealed class ActionRecoveryViewModel : ObservableObject
{
    private readonly IActionTransactionEngine engine;
    private RecoveryItemViewModel? selectedRecovery;
    private bool isDismissed;
    private bool isRecovering;
    private string statusMessage = string.Empty;

    public ActionRecoveryViewModel(IActionTransactionEngine engine)
    {
        this.engine = engine;
        RecoverCommand = new AsyncRelayCommand(
            RecoverAsync,
            () => SelectedRecovery?.CanRecover == true && !IsRecovering);
        DismissCommand = new RelayCommand(Dismiss);
    }

    public ObservableCollection<RecoveryItemViewModel> Recoveries { get; } = [];

    public IAsyncRelayCommand RecoverCommand { get; }

    public IRelayCommand DismissCommand { get; }

    public RecoveryItemViewModel? SelectedRecovery
    {
        get => selectedRecovery;
        set
        {
            if (SetProperty(ref selectedRecovery, value))
            {
                RecoverCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRecovering
    {
        get => isRecovering;
        private set
        {
            if (SetProperty(ref isRecovering, value))
            {
                RecoverCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsOpen => !isDismissed && Recoveries.Count > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var recoveries = await engine.InspectIncompleteAsync(cancellationToken);
        Recoveries.Clear();
        foreach (var recovery in recoveries)
        {
            Recoveries.Add(new(recovery));
        }

        SelectedRecovery = Recoveries.FirstOrDefault();
        isDismissed = false;
        OnPropertyChanged(nameof(IsOpen));
    }

    private async Task RecoverAsync()
    {
        if (SelectedRecovery is not { CanRecover: true } selected)
        {
            return;
        }

        IsRecovering = true;
        try
        {
            var result = await engine.RecoverAsync(selected.TransactionId);
            StatusMessage = result.Summary;
            Recoveries.Remove(selected);
            SelectedRecovery = Recoveries.FirstOrDefault();
            OnPropertyChanged(nameof(IsOpen));
        }
        finally
        {
            IsRecovering = false;
        }
    }

    private void Dismiss()
    {
        isDismissed = true;
        OnPropertyChanged(nameof(IsOpen));
    }
}

public sealed record RecoveryItemViewModel(TransactionRecoveryInfo Recovery)
{
    public Guid TransactionId => Recovery.TransactionId;

    public string Title => Recovery.TransactionId == Guid.Empty
        ? "Unreadable transaction journal"
        : $"Interrupted transaction {Recovery.TransactionId:N}";

    public string State => $"Recorded state: {Recovery.State}";

    public string Explanation => Recovery.Explanation;

    public string Actions => Recovery.ActionNames.IsEmpty
        ? "Actions: no metadata is available"
        : $"Actions: {string.Join(", ", Recovery.ActionNames)}";

    public string AppliedSummary =>
        $"{Recovery.AppliedActions.Length} applied · {Recovery.VerifiedActions.Length} verified · {Recovery.SnapshotsAvailable.Length} snapshots";

    public string PrivilegeSummary => Recovery.RequiresAdministrator
        ? "Recovery may eventually require administrator approval"
        : "Standard-user recovery metadata";

    public bool ManualInterventionMayBeRequired => Recovery.ManualInterventionMayBeRequired;

    public bool CanRecover => Recovery.TransactionId != Guid.Empty && Recovery.CanRollback;
}
