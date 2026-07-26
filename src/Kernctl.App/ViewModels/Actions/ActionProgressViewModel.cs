using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.Core.Actions;

namespace Kernctl.App.ViewModels.Actions;

public sealed class ActionProgressViewModel : ObservableObject, IDisposable
{
    private readonly IActionTransactionEngine engine;
    private Guid transactionId;
    private string currentAction = "Preparing transaction";
    private string lifecycleStage = ActionExecutionStage.Planning.ToString();
    private string message = string.Empty;
    private int completedActions;
    private int totalActions;
    private bool isRollback;
    private bool isCancellationRequested;
    private bool isAdministratorRequest;
    private bool isActive;
    private bool disposed;

    public ActionProgressViewModel(IActionTransactionEngine engine)
    {
        this.engine = engine;
        engine.ProgressChanged += OnProgressChanged;
        CancelCommand = new RelayCommand(Cancel, () => IsActive && !IsCancellationRequested);
    }

    public IRelayCommand CancelCommand { get; }

    public Guid TransactionId
    {
        get => transactionId;
        private set => SetProperty(ref transactionId, value);
    }

    public string CurrentAction
    {
        get => currentAction;
        private set => SetProperty(ref currentAction, value);
    }

    public string LifecycleStage
    {
        get => lifecycleStage;
        private set => SetProperty(ref lifecycleStage, value);
    }

    public string Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public int CompletedActions
    {
        get => completedActions;
        private set
        {
            if (SetProperty(ref completedActions, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressLabel));
            }
        }
    }

    public int TotalActions
    {
        get => totalActions;
        private set
        {
            if (SetProperty(ref totalActions, value))
            {
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressLabel));
            }
        }
    }

    public bool IsRollback
    {
        get => isRollback;
        private set => SetProperty(ref isRollback, value);
    }

    public bool IsCancellationRequested
    {
        get => isCancellationRequested;
        private set
        {
            if (SetProperty(ref isCancellationRequested, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsAdministratorRequest
    {
        get => isAdministratorRequest;
        private set => SetProperty(ref isAdministratorRequest, value);
    }

    public bool IsActive
    {
        get => isActive;
        private set
        {
            if (SetProperty(ref isActive, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsIndeterminate => TotalActions <= 0;

    public double ProgressPercent => TotalActions <= 0
        ? 0
        : Math.Clamp((double)CompletedActions / TotalActions * 100, 0, 100);

    public string ProgressLabel => TotalActions <= 0
        ? "Working"
        : $"{CompletedActions} of {TotalActions} actions complete";

    public void Begin(Guid id, int actionCount)
    {
        TransactionId = id;
        TotalActions = actionCount;
        CompletedActions = 0;
        IsRollback = false;
        IsAdministratorRequest = false;
        IsCancellationRequested = false;
        IsActive = true;
    }

    public void Complete()
    {
        IsActive = false;
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        engine.ProgressChanged -= OnProgressChanged;
        disposed = true;
    }

    private void Cancel()
    {
        if (engine.RequestCancellation(TransactionId))
        {
            IsCancellationRequested = true;
            Message = "Cancellation requested. kernctl will finish the current safe step and roll back.";
        }
    }

    private void OnProgressChanged(object? sender, ActionProgressUpdate update)
    {
        if (TransactionId != Guid.Empty && update.TransactionId != TransactionId)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TransactionId = update.TransactionId;
            CurrentAction = update.ActionName ?? "Transaction";
            LifecycleStage = update.Stage.ToString();
            IsAdministratorRequest = update.Stage == ActionExecutionStage.Elevation;
            Message = update.Message;
            CompletedActions = update.CompletedActions;
            TotalActions = update.TotalActions;
            IsRollback = update.IsRollback;
            IsCancellationRequested = update.IsCancellationRequested
                || IsCancellationRequested;
            IsActive = true;
        });
    }
}
