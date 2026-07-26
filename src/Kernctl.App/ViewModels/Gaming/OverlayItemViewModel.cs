using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.Core.Gaming;

namespace Kernctl.App.ViewModels.Gaming;

public sealed class OverlayItemViewModel : ObservableObject
{
    private bool isExitConfirmationVisible;
    private bool isTracked;
    private string status;

    public OverlayItemViewModel(
        OverlayApplication overlay,
        Func<OverlayItemViewModel, Task> open,
        Func<OverlayItemViewModel, Task> close,
        bool isTracked,
        Func<OverlayItemViewModel, Task> toggleTracked)
    {
        Overlay = overlay;
        status = overlay.Status;
        this.isTracked = isTracked;
        OpenCommand = new AsyncRelayCommand(() => open(this), () => overlay.ExecutablePath is not null);
        RequestExitCommand = new RelayCommand(
            () => IsExitConfirmationVisible = true,
            () => overlay.IsRunning);
        CancelExitCommand = new RelayCommand(() => IsExitConfirmationVisible = false);
        ConfirmExitCommand = new AsyncRelayCommand(async () =>
        {
            IsExitConfirmationVisible = false;
            await close(this);
        });
        ToggleTrackedCommand = new AsyncRelayCommand(async () =>
        {
            IsTracked = !IsTracked;
            await toggleTracked(this);
        });
    }

    public OverlayApplication Overlay { get; }

    public string Id => Overlay.Id;

    public string Name => Overlay.Name;

    public bool IsRunning => Overlay.IsRunning;

    public string Capabilities => string.Join(", ", Overlay.Capabilities);

    public string Publisher => Overlay.PublisherMetadata ?? "Publisher metadata unavailable";

    public string Path => Overlay.ExecutablePath ?? "Path unavailable";

    public string Status
    {
        get => status;
        set => SetProperty(ref status, value);
    }

    public bool IsTracked
    {
        get => isTracked;
        private set
        {
            if (SetProperty(ref isTracked, value))
            {
                OnPropertyChanged(nameof(TrackingLabel));
            }
        }
    }

    public string TrackingLabel => IsTracked ? "Included" : "Ignored";

    public bool IsExitConfirmationVisible
    {
        get => isExitConfirmationVisible;
        set => SetProperty(ref isExitConfirmationVisible, value);
    }

    public IAsyncRelayCommand OpenCommand { get; }

    public IRelayCommand RequestExitCommand { get; }

    public IRelayCommand CancelExitCommand { get; }

    public IAsyncRelayCommand ConfirmExitCommand { get; }

    public IAsyncRelayCommand ToggleTrackedCommand { get; }
}
