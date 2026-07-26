using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.Core.Gaming;

namespace Kernctl.App.ViewModels.Gaming;

public sealed class GameCardViewModel : ObservableObject
{
    private double gridCardMinimumHeight = 174;
    private double listCardMinimumHeight = 76;

    public GameCardViewModel(
        GameDefinition game,
        Action<GameCardViewModel> openDetails,
        Action<GameCardViewModel> requestLaunch)
    {
        Game = game;
        OpenDetailsCommand = new RelayCommand(() => openDetails(this));
        RequestLaunchCommand = new RelayCommand(
            () => requestLaunch(this),
            () => CanLaunch);
    }

    public GameDefinition Game { get; private set; }

    public Guid Id => Game.Id;

    public string Name => Game.Name;

    public string Source => Game.Source.ToString();

    public string InstallState => Game.Installation.State switch
    {
        GameInstallState.Installed => "Installed",
        GameInstallState.Missing => "Missing",
        GameInstallState.NeedsExecutable => "Choose executable",
        _ => "Invalid",
    };

    public string ProfileName { get; set; } = "No profile";

    public string LastPlayed => Game.LastPlayedAtUtc is null
        ? "Never played"
        : $"Played {Game.LastPlayedAtUtc.Value.LocalDateTime:g}";

    public bool CanLaunch =>
        Game.Installation.State == GameInstallState.Installed
        && Game.Installation.ExecutablePath is not null
        && File.Exists(Game.Installation.ExecutablePath);

    public bool IsMissing => !CanLaunch;

    public IRelayCommand OpenDetailsCommand { get; }

    public IRelayCommand RequestLaunchCommand { get; }

    public double GridCardMinimumHeight
    {
        get => gridCardMinimumHeight;
        private set => SetProperty(ref gridCardMinimumHeight, value);
    }

    public double ListCardMinimumHeight
    {
        get => listCardMinimumHeight;
        private set => SetProperty(ref listCardMinimumHeight, value);
    }

    public void Update(GameDefinition game, string profileName)
    {
        Game = game;
        ProfileName = profileName;
        OnPropertyChanged(string.Empty);
        RequestLaunchCommand.NotifyCanExecuteChanged();
    }

    public void SetCompact(bool compact)
    {
        GridCardMinimumHeight = compact ? 142 : 174;
        ListCardMinimumHeight = compact ? 58 : 76;
    }
}
