using Avalonia.Controls;
using Avalonia.Input;
using Kernctl.App.ViewModels;

namespace Kernctl.App.Views;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool disposed;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            await ViewModel.InitializeAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown cancels harmless metric initialization.
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        lifetimeCancellation.Cancel();
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lifetimeCancellation.Dispose();
        disposed = true;
    }

    private void SearchBox_GotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        ViewModel?.BeginSearch();

    private void SearchBox_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (eventArgs.Key)
        {
            case Key.Down:
                ViewModel.MoveSearchSelection(1);
                eventArgs.Handled = true;
                break;
            case Key.Up:
                ViewModel.MoveSearchSelection(-1);
                eventArgs.Handled = true;
                break;
            case Key.Enter:
                ViewModel.ActivateSelectedSearchResult();
                eventArgs.Handled = true;
                break;
            case Key.Escape:
                ViewModel.CloseSearch();
                Focus();
                eventArgs.Handled = true;
                break;
        }
    }

    private void Window_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.K
            && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            ViewModel?.BeginSearch();
            eventArgs.Handled = true;
        }
    }
}
