using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.App.Services;
using Kernctl.App.ViewModels.Gaming;
using Kernctl.App.ViewModels.Profiles;
using Kernctl.Core.Gaming;
using Kernctl.Core.Models;
using Kernctl.Core.Profiles;
using Kernctl.Core.Services;

namespace Kernctl.App.ViewModels.Pages;

public enum GamingPageSection
{
    Dashboard,
    Library,
    Details,
    Priority,
    Overlays,
    AutoProfile,
    Fps,
}

public sealed partial class GamingPageViewModel : ObservableObject
{
    private readonly IProfileService profileService;
    private readonly ISystemMetricsService metricsService;
    private readonly IGameLibraryService? gameLibrary;
    private readonly IGameSessionCoordinator? sessionCoordinator;
    private readonly IProfileCatalogService? profileCatalog;
    private readonly IProfileFileDialogService? fileDialogs;
    private readonly IOverlayService? overlayService;
    private readonly IFpsProvider fpsProvider;
    private string activeProfileName;
    private string activeProfileDescription;
    private string cpuValue = "—";
    private string memoryValue = "—";
    private string powerValue = "—";
    private string metricsStatus = "Waiting for metrics";
    private string libraryQuery = string.Empty;
    private string selectedSource = "All";
    private string selectedSort = "Name";
    private string libraryStatus = "Loading local library…";
    private string operationStatus = string.Empty;
    private string detailName = string.Empty;
    private string detailExecutablePath = string.Empty;
    private string detailWorkingDirectory = string.Empty;
    private string detailArguments = string.Empty;
    private string? detailProfileId;
    private GameProcessPriority detailPriority;
    private bool detailAutoProfile;
    private bool detailRestoreProfile = true;
    private bool automaticProfilesEnabled;
    private bool isGridView = true;
    private bool compactView;
    private GameProcessPriority defaultPriority;
    private bool isProfileDialogOpen;
    private bool isLaunchConfirmationOpen;
    private bool isAddConfirmationOpen;
    private bool isDetailConfirmationOpen;
    private bool isSessionActive;
    private string addConfirmationMessage = string.Empty;
    private string detailConfirmationMessage = string.Empty;
    private string? pendingManualPath;
    private GameCardViewModel? pendingLaunch;
    private GameCardViewModel? selectedGame;
    private GameDefinition? pendingDetailUpdate;
    private ProfileChoiceViewModel? selectedProfileChoice;
    private GamingPageSection section;

    public GamingPageViewModel(
        IProfileService profileService,
        ISystemMetricsService metricsService,
        ProfileManagerViewModel? profileManager = null,
        IGameLibraryService? gameLibrary = null,
        IGameSessionCoordinator? sessionCoordinator = null,
        IProfileCatalogService? profileCatalog = null,
        IProfileFileDialogService? fileDialogs = null,
        IOverlayService? overlayService = null,
        IFpsProvider? fpsProvider = null)
    {
        this.profileService = profileService;
        this.metricsService = metricsService;
        this.gameLibrary = gameLibrary;
        this.sessionCoordinator = sessionCoordinator;
        this.profileCatalog = profileCatalog;
        this.fileDialogs = fileDialogs;
        this.overlayService = overlayService;
        this.fpsProvider = fpsProvider ?? new UnavailableFpsProvider();
        ProfileManager = profileManager;
        activeProfileName = profileService.ActiveProfile.Name;
        activeProfileDescription = profileService.ActiveProfile.Description;
        section = GamingPageSection.Dashboard;

        Tools =
        [
            new(
                "Performance Mode",
                "Review and apply a system profile",
                IconCatalog.Optimize,
                hasToggle: false,
                command: new RelayCommand(OpenProfiles)),
            new(
                "Game Launcher",
                "Launch and manage local games",
                IconCatalog.Launcher,
                hasToggle: false,
                command: new RelayCommand(() => Section = GamingPageSection.Library)),
            new(
                "Process Priority",
                "Choose safe per-game priorities",
                IconCatalog.Processor,
                hasToggle: false,
                command: new RelayCommand(() => Section = GamingPageSection.Priority)),
            new(
                "Overlay Manager",
                "Inspect known overlay applications",
                IconCatalog.Display,
                hasToggle: false,
                command: new AsyncRelayCommand(OpenOverlaysAsync)),
            new(
                "Auto Profile",
                "Configure profile switching per game",
                IconCatalog.User,
                hasToggle: false,
                command: new RelayCommand(() => Section = GamingPageSection.AutoProfile)),
            new(
                "FPS Monitoring",
                this.fpsProvider.Status,
                IconCatalog.Chart,
                hasToggle: false,
                command: new RelayCommand(() => Section = GamingPageSection.Fps)),
        ];

        ProfileChoices = profileService.Profiles
            .Select(profile => new ProfileChoiceViewModel(
                profile.Kind,
                profile.Name,
                profile.Description,
                IconForProfile(profile.Kind)))
            .ToArray();
        selectedProfileChoice = ProfileChoices.Single(
            choice => choice.Kind == profileService.ActiveProfile.Kind);

        profileService.ActiveProfileChanged += OnActiveProfileChanged;
        if (ProfileManager is not null)
        {
            activeProfileName = ProfileManager.ActiveProfile.Name;
            activeProfileDescription = ProfileManager.ActiveProfile.Description;
            ProfileManager.ActiveProfileChanged += OnManagedActiveProfileChanged;
        }

        if (gameLibrary is not null)
        {
            gameLibrary.LibraryChanged += OnLibraryChanged;
        }

        if (sessionCoordinator is not null)
        {
            sessionCoordinator.MetricsChanged += OnSessionMetricsChanged;
        }
    }

    public IReadOnlyList<ToolCardViewModel> Tools { get; }

    public IReadOnlyList<ProfileChoiceViewModel> ProfileChoices { get; }

    public IReadOnlyList<GameProcessPriority> PriorityChoices { get; } =
        [GameProcessPriority.Normal, GameProcessPriority.AboveNormal, GameProcessPriority.High];

    public IReadOnlyList<string> SourceChoices { get; } = ["All", "Manual", "Steam", "Epic"];

    public IReadOnlyList<string> SortChoices { get; } = ["Name", "Last played", "Source"];

    public IReadOnlyList<GamingProfileOption> GamingProfiles =>
        profileCatalog?.Profiles
            .Select(profile => new GamingProfileOption(profile.Id, profile.Name))
            .ToArray()
        ?? [];

    public ProfileManagerViewModel? ProfileManager { get; }

    public ObservableCollection<GameCardViewModel> Games { get; } = [];

    public ObservableCollection<GameCardViewModel> FilteredGames { get; } = [];

    public ObservableCollection<OverlayItemViewModel> Overlays { get; } = [];

    public ObservableCollection<string> LibraryErrors { get; } = [];

    public ObservableCollection<GameSessionItemViewModel> RecentSessions { get; } = [];

    public bool IsDashboard => Section == GamingPageSection.Dashboard;

    public bool IsLibrary => Section == GamingPageSection.Library;

    public bool IsDetails => Section == GamingPageSection.Details;

    public bool IsPriority => Section == GamingPageSection.Priority;

    public bool IsOverlays => Section == GamingPageSection.Overlays;

    public bool IsAutoProfile => Section == GamingPageSection.AutoProfile;

    public bool IsFps => Section == GamingPageSection.Fps;

    public GamingPageSection Section
    {
        get => section;
        private set
        {
            if (SetProperty(ref section, value))
            {
                OnPropertyChanged(nameof(IsDashboard));
                OnPropertyChanged(nameof(IsLibrary));
                OnPropertyChanged(nameof(IsDetails));
                OnPropertyChanged(nameof(IsPriority));
                OnPropertyChanged(nameof(IsOverlays));
                OnPropertyChanged(nameof(IsAutoProfile));
                OnPropertyChanged(nameof(IsFps));
            }
        }
    }

    public string ActiveProfileName
    {
        get => activeProfileName;
        private set => SetProperty(ref activeProfileName, value);
    }

    public string ActiveProfileDescription
    {
        get => activeProfileDescription;
        private set => SetProperty(ref activeProfileDescription, value);
    }

    public string CpuValue
    {
        get => cpuValue;
        private set => SetProperty(ref cpuValue, value);
    }

    public string MemoryValue
    {
        get => memoryValue;
        private set => SetProperty(ref memoryValue, value);
    }

    public string PowerValue
    {
        get => powerValue;
        private set => SetProperty(ref powerValue, value);
    }

    public string MetricsStatus
    {
        get => metricsStatus;
        private set => SetProperty(ref metricsStatus, value);
    }

    public string InstalledGameCount =>
        Games.Count(game => game.Game.Installation.State == GameInstallState.Installed)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string OverlayCount => Overlays.Count(overlay => overlay.IsRunning && overlay.IsTracked)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string AutoProfileStatus => AutomaticProfilesEnabled ? "Enabled" : "Disabled";

    public string FpsStatus => fpsProvider.Status;

    public string RecentAutoActivation =>
        gameLibrary?.Sessions
            .LastOrDefault(session => !string.IsNullOrWhiteSpace(session.ProfileId)) is { } session
            ? $"{session.GameName}: {session.Outcome} at {session.StartedAtUtc.LocalDateTime:g}"
            : "No automatic profile activations recorded.";

    public string LibraryQuery
    {
        get => libraryQuery;
        set
        {
            if (SetProperty(ref libraryQuery, value ?? string.Empty))
            {
                RefreshFilter();
            }
        }
    }

    public string SelectedSource
    {
        get => selectedSource;
        set
        {
            if (SetProperty(ref selectedSource, value ?? "All"))
            {
                RefreshFilter();
            }
        }
    }

    public string SelectedSort
    {
        get => selectedSort;
        set
        {
            if (SetProperty(ref selectedSort, value ?? "Name"))
            {
                RefreshFilter();
            }
        }
    }

    public bool IsGridView
    {
        get => isGridView;
        private set
        {
            if (SetProperty(ref isGridView, value))
            {
                OnPropertyChanged(nameof(IsListView));
            }
        }
    }

    public bool IsListView => !IsGridView;

    public bool CompactView
    {
        get => compactView;
        private set
        {
            if (SetProperty(ref compactView, value))
            {
                OnPropertyChanged(nameof(DensityLabel));
            }
        }
    }

    public string DensityLabel => CompactView ? "Compact" : "Comfortable";

    public string LibraryStatus
    {
        get => libraryStatus;
        private set => SetProperty(ref libraryStatus, value);
    }

    public string OperationStatus
    {
        get => operationStatus;
        private set => SetProperty(ref operationStatus, value);
    }

    public string DetailName
    {
        get => detailName;
        set => SetProperty(ref detailName, value ?? string.Empty);
    }

    public string DetailExecutablePath
    {
        get => detailExecutablePath;
        set => SetProperty(ref detailExecutablePath, value ?? string.Empty);
    }

    public string DetailWorkingDirectory
    {
        get => detailWorkingDirectory;
        set => SetProperty(ref detailWorkingDirectory, value ?? string.Empty);
    }

    public string DetailArguments
    {
        get => detailArguments;
        set => SetProperty(ref detailArguments, value ?? string.Empty);
    }

    public string? DetailProfileId
    {
        get => detailProfileId;
        set => SetProperty(ref detailProfileId, value);
    }

    public GameProcessPriority DetailPriority
    {
        get => detailPriority;
        set => SetProperty(ref detailPriority, value);
    }

    public bool DetailAutoProfile
    {
        get => detailAutoProfile;
        set => SetProperty(ref detailAutoProfile, value);
    }

    public bool DetailRestoreProfile
    {
        get => detailRestoreProfile;
        set => SetProperty(ref detailRestoreProfile, value);
    }

    public bool AutomaticProfilesEnabled
    {
        get => automaticProfilesEnabled;
        set
        {
            if (SetProperty(ref automaticProfilesEnabled, value))
            {
                OnPropertyChanged(nameof(AutoProfileStatus));
            }
        }
    }

    public GameProcessPriority DefaultPriority
    {
        get => defaultPriority;
        set => SetProperty(ref defaultPriority, value);
    }

    public GameCardViewModel? SelectedGame
    {
        get => selectedGame;
        private set => SetProperty(ref selectedGame, value);
    }

    public bool IsProfileDialogOpen
    {
        get => isProfileDialogOpen;
        private set => SetProperty(ref isProfileDialogOpen, value);
    }

    public bool IsLaunchConfirmationOpen
    {
        get => isLaunchConfirmationOpen;
        private set => SetProperty(ref isLaunchConfirmationOpen, value);
    }

    public bool IsAddConfirmationOpen
    {
        get => isAddConfirmationOpen;
        private set => SetProperty(ref isAddConfirmationOpen, value);
    }

    public bool IsDetailConfirmationOpen
    {
        get => isDetailConfirmationOpen;
        private set => SetProperty(ref isDetailConfirmationOpen, value);
    }

    public bool IsSessionActive
    {
        get => isSessionActive;
        private set => SetProperty(ref isSessionActive, value);
    }

    public string AddConfirmationMessage
    {
        get => addConfirmationMessage;
        private set => SetProperty(ref addConfirmationMessage, value);
    }

    public string DetailConfirmationMessage
    {
        get => detailConfirmationMessage;
        private set => SetProperty(ref detailConfirmationMessage, value);
    }

    public string LaunchConfirmationMessage => pendingLaunch is null
        ? "Review this launch."
        : $"Launch {pendingLaunch.Name} directly from its configured executable? Temporary profile and priority changes will be restored when its process tree exits. {string.Join(" ", pendingLaunch.Game.Warnings)}";

    public ProfileChoiceViewModel? SelectedProfileChoice
    {
        get => selectedProfileChoice;
        set => SetProperty(ref selectedProfileChoice, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (gameLibrary is not null)
        {
            await gameLibrary.InitializeAsync(cancellationToken);
            automaticProfilesEnabled = gameLibrary.Preferences.AutomaticProfilesEnabled;
            defaultPriority = gameLibrary.Preferences.DefaultPriority;
            compactView = gameLibrary.Preferences.CompactView;
            RefreshLibrary();
        }

        try
        {
            var metrics = await metricsService.GetSnapshotAsync(cancellationToken);
            CpuValue = metrics.IsSample ? "Unavailable" : $"{metrics.CpuPercent}%";
            MemoryValue = metrics.IsSample ? "Unavailable" : $"{metrics.MemoryPercent}%";
            PowerValue = ActiveProfileName;
            MetricsStatus = metrics.IsSample ? "METRICS UNAVAILABLE" : "LIVE METRICS";
        }
        catch (InvalidOperationException)
        {
            MetricsStatus = "METRICS UNAVAILABLE";
        }

        if (overlayService is not null)
        {
            await RefreshOverlaysAsync();
        }
    }

    public void OpenGameDetails(Guid gameId)
    {
        var game = Games.Single(item => item.Id == gameId);
        OpenDetails(game);
    }

    public void RequestGameLaunch(Guid gameId)
    {
        var game = Games.Single(item => item.Id == gameId);
        RequestLaunch(game);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        sessionCoordinator?.ShutdownAsync(cancellationToken) ?? Task.CompletedTask;

    [RelayCommand]
    private void BackToDashboard() => Section = GamingPageSection.Dashboard;

    [RelayCommand]
    private void BackToLibrary() => Section = GamingPageSection.Library;

    [RelayCommand]
    private void ShowGrid() => IsGridView = true;

    [RelayCommand]
    private void ShowList() => IsGridView = false;

    [RelayCommand]
    private async Task ToggleDensityAsync()
    {
        CompactView = !CompactView;
        foreach (var game in Games)
        {
            game.SetCompact(CompactView);
        }

        if (gameLibrary is not null)
        {
            await gameLibrary.SavePreferencesAsync(gameLibrary.Preferences with
            {
                CompactView = CompactView,
            });
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (gameLibrary is null)
        {
            return;
        }

        LibraryStatus = "Rescanning Steam and Epic local metadata…";
        await gameLibrary.RescanAsync();
        LibraryStatus = $"Found {Games.Count} local game entries.";
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        if (fileDialogs is null || gameLibrary is null)
        {
            OperationStatus = "A native Windows file picker is unavailable.";
            return;
        }

        var path = await fileDialogs.PickExecutableAsync();
        if (path is null)
        {
            return;
        }

        await TryAddManualAsync(path, suspiciousLocationConfirmed: false);
    }

    [RelayCommand]
    private async Task ConfirmAddAsync()
    {
        if (pendingManualPath is null)
        {
            return;
        }

        var path = pendingManualPath;
        pendingManualPath = null;
        IsAddConfirmationOpen = false;
        await TryAddManualAsync(path, suspiciousLocationConfirmed: true);
    }

    [RelayCommand]
    private void CancelAdd()
    {
        pendingManualPath = null;
        IsAddConfirmationOpen = false;
    }

    [RelayCommand]
    private async Task PickDetailExecutableAsync()
    {
        if (fileDialogs is null)
        {
            return;
        }

        var path = await fileDialogs.PickExecutableAsync();
        if (path is not null)
        {
            DetailExecutablePath = path;
            DetailWorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty;
        }
    }

    [RelayCommand]
    private async Task SaveDetailsAsync()
    {
        if (gameLibrary is null || SelectedGame is null)
        {
            return;
        }

        var arguments = DetailArguments
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0)
            .ToArray();
        var validation = GameValidation.ValidateLaunch(
            DetailExecutablePath,
            DetailWorkingDirectory,
            arguments);
        if (!validation.IsValid)
        {
            OperationStatus = validation.Errors[0];
            return;
        }

        var updated = SelectedGame.Game with
        {
            Name = string.IsNullOrWhiteSpace(DetailName)
                ? SelectedGame.Game.Name
                : DetailName.Trim(),
            Installation = SelectedGame.Game.Installation with
            {
                ExecutablePath = validation.NormalizedExecutablePath,
                InstallDirectory = SelectedGame.Game.Installation.InstallDirectory
                    ?? validation.NormalizedWorkingDirectory,
                State = GameInstallState.Installed,
            },
            Launch = SelectedGame.Game.Launch with
            {
                Arguments = [.. arguments],
                WorkingDirectory = validation.NormalizedWorkingDirectory,
                ProfileId = string.IsNullOrWhiteSpace(DetailProfileId) ? null : DetailProfileId,
                Priority = DetailPriority,
                AutoApplyProfile = DetailAutoProfile,
                RestorePreviousProfileOnExit = DetailRestoreProfile,
            },
            Warnings = validation.Warnings,
        };
        var pathChanged = !string.Equals(
            SelectedGame.Game.Installation.ExecutablePath,
            updated.Installation.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
        if (pathChanged && !validation.Warnings.IsEmpty)
        {
            pendingDetailUpdate = updated;
            DetailConfirmationMessage = string.Join(" ", validation.Warnings);
            IsDetailConfirmationOpen = true;
            return;
        }

        await PersistDetailsAsync(updated);
    }

    [RelayCommand]
    private async Task ConfirmDetailSaveAsync()
    {
        if (pendingDetailUpdate is null)
        {
            return;
        }

        var update = pendingDetailUpdate;
        pendingDetailUpdate = null;
        IsDetailConfirmationOpen = false;
        await PersistDetailsAsync(update);
    }

    [RelayCommand]
    private void CancelDetailSave()
    {
        pendingDetailUpdate = null;
        IsDetailConfirmationOpen = false;
    }

    [RelayCommand]
    private async Task RemoveGameAsync()
    {
        if (gameLibrary is null || SelectedGame is null)
        {
            return;
        }

        await gameLibrary.RemoveAsync(SelectedGame.Id);
        Section = GamingPageSection.Library;
        OperationStatus = "Game removed from kernctl. Launcher files were not changed.";
    }

    [RelayCommand]
    private void RequestSelectedLaunch()
    {
        if (SelectedGame is not null)
        {
            RequestLaunch(SelectedGame);
        }
    }

    [RelayCommand]
    private void CancelLaunch()
    {
        pendingLaunch = null;
        IsLaunchConfirmationOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmLaunchAsync()
    {
        if (pendingLaunch is null || sessionCoordinator is null)
        {
            return;
        }

        var game = pendingLaunch;
        pendingLaunch = null;
        IsLaunchConfirmationOpen = false;
        IsSessionActive = true;
        OperationStatus = $"Monitoring {game.Name}. FPS is not fabricated.";
        try
        {
            var session = await sessionCoordinator.LaunchAndMonitorAsync(game.Id);
            OperationStatus = session.Summary;
        }
        finally
        {
            IsSessionActive = false;
            RefreshLibrary();
        }
    }

    [RelayCommand]
    private async Task SaveGamingPreferencesAsync()
    {
        if (gameLibrary is null)
        {
            return;
        }

        await gameLibrary.SavePreferencesAsync(gameLibrary.Preferences with
        {
            AutomaticProfilesEnabled = AutomaticProfilesEnabled,
            DefaultPriority = DefaultPriority,
            CompactView = CompactView,
        });
        OperationStatus = "Gaming preferences saved.";
    }

    [RelayCommand]
    private void OpenProfileDialog()
    {
        if (ProfileManager is not null)
        {
            ProfileManager.OpenCommand.Execute(null);
            return;
        }

        SelectedProfileChoice = ProfileChoices.Single(
            choice => choice.Kind == profileService.ActiveProfile.Kind);
        IsProfileDialogOpen = true;
    }

    [RelayCommand]
    private void CloseProfileDialog() => IsProfileDialogOpen = false;

    [RelayCommand]
    private void ConfirmProfile()
    {
        if (SelectedProfileChoice is null)
        {
            return;
        }

        profileService.SelectProfile(SelectedProfileChoice.Kind);
        IsProfileDialogOpen = false;
    }

    private async Task TryAddManualAsync(string path, bool suspiciousLocationConfirmed)
    {
        try
        {
            var game = await gameLibrary!.AddManualAsync(path, suspiciousLocationConfirmed);
            OperationStatus = $"{game.Name} was added from a direct executable.";
            Section = GamingPageSection.Library;
        }
        catch (GameConfirmationRequiredException exception)
        {
            pendingManualPath = path;
            AddConfirmationMessage = string.Join(" ", exception.Warnings);
            IsAddConfirmationOpen = true;
        }
        catch (InvalidDataException exception)
        {
            OperationStatus = exception.Message;
        }
    }

    private void OpenDetails(GameCardViewModel game)
    {
        SelectedGame = game;
        DetailName = game.Game.Name;
        DetailExecutablePath = game.Game.Installation.ExecutablePath ?? string.Empty;
        DetailWorkingDirectory = game.Game.Launch.WorkingDirectory
            ?? game.Game.Installation.InstallDirectory
            ?? string.Empty;
        DetailArguments = string.Join(Environment.NewLine, game.Game.Launch.Arguments);
        DetailProfileId = game.Game.Launch.ProfileId;
        DetailPriority = game.Game.Launch.Priority;
        DetailAutoProfile = game.Game.Launch.AutoApplyProfile;
        DetailRestoreProfile = game.Game.Launch.RestorePreviousProfileOnExit;
        OperationStatus = game.Game.Warnings.FirstOrDefault() ?? string.Empty;
        RefreshRecentSessions(game.Id);
        Section = GamingPageSection.Details;
    }

    private void RequestLaunch(GameCardViewModel game)
    {
        if (!game.CanLaunch)
        {
            OpenDetails(game);
            OperationStatus = "Choose a valid executable before launching this game.";
            return;
        }

        pendingLaunch = game;
        OnPropertyChanged(nameof(LaunchConfirmationMessage));
        IsLaunchConfirmationOpen = true;
    }

    private async Task OpenOverlaysAsync()
    {
        Section = GamingPageSection.Overlays;
        await RefreshOverlaysAsync();
    }

    private async Task RefreshOverlaysAsync()
    {
        if (overlayService is null)
        {
            return;
        }

        var applications = await overlayService.InspectAsync();
        Overlays.Clear();
        foreach (var overlay in applications)
        {
            Overlays.Add(new(
                overlay,
                async item =>
                {
                    var result = await overlayService.OpenAsync(item.Id);
                    item.Status = result.Summary;
                },
                async item =>
                {
                    var result = await overlayService.RequestCloseAsync(
                        item.Id,
                        explicitlyConfirmed: true);
                    item.Status = result.Summary;
                    await RefreshOverlaysAsync();
                },
                gameLibrary?.Preferences.OverlayPreferences.GetValueOrDefault(
                    overlay.Id,
                    true) ?? true,
                async item =>
                {
                    if (gameLibrary is null)
                    {
                        return;
                    }

                    await gameLibrary.SavePreferencesAsync(gameLibrary.Preferences with
                    {
                        OverlayPreferences = gameLibrary.Preferences.OverlayPreferences
                            .SetItem(item.Id, item.IsTracked),
                    });
                    OnPropertyChanged(nameof(OverlayCount));
                }));
        }

        OnPropertyChanged(nameof(OverlayCount));
    }

    private void RefreshLibrary()
    {
        if (gameLibrary is null)
        {
            return;
        }

        Games.Clear();
        foreach (var game in gameLibrary.Games)
        {
            var card = new GameCardViewModel(game, OpenDetails, RequestLaunch);
            card.ProfileName = ProfileName(game.Launch.ProfileId);
            card.SetCompact(CompactView);
            Games.Add(card);
        }

        LibraryErrors.Clear();
        foreach (var error in gameLibrary.Errors)
        {
            LibraryErrors.Add(error);
        }

        LibraryStatus = Games.Count == 0
            ? "No games found. Add an executable or rescan local launcher metadata."
            : $"{Games.Count} local entries — {InstalledGameCount} ready to launch";
        RefreshFilter();
        OnPropertyChanged(nameof(InstalledGameCount));
        OnPropertyChanged(nameof(AutoProfileStatus));
        OnPropertyChanged(nameof(RecentAutoActivation));
    }

    private void RefreshFilter()
    {
        FilteredGames.Clear();
        IEnumerable<GameCardViewModel> filtered = Games
            .Where(game =>
                string.IsNullOrWhiteSpace(LibraryQuery)
                || game.Name.Contains(LibraryQuery, StringComparison.OrdinalIgnoreCase))
            .Where(game =>
                SelectedSource == "All"
                || string.Equals(game.Source, SelectedSource, StringComparison.OrdinalIgnoreCase));
        filtered = SelectedSort switch
        {
            "Last played" => filtered
                .OrderByDescending(game => game.Game.LastPlayedAtUtc)
                .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase),
            "Source" => filtered
                .OrderBy(game => game.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase),
        };
        foreach (var game in filtered)
        {
            FilteredGames.Add(game);
        }
    }

    private void RefreshRecentSessions(Guid gameId)
    {
        RecentSessions.Clear();
        if (gameLibrary is null)
        {
            return;
        }

        foreach (var session in gameLibrary.Sessions
                     .Where(session => session.GameId == gameId)
                     .OrderByDescending(session => session.StartedAtUtc)
                     .Take(8))
        {
            RecentSessions.Add(new(
                session.StartedAtUtc.LocalDateTime.ToString(
                    "g",
                    System.Globalization.CultureInfo.CurrentCulture),
                session.Outcome.ToString(),
                FormatDuration(session.Duration),
                session.Priority.ToString(),
                FormatResources(session),
                session.Summary));
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(
                @"h\:mm\:ss",
                System.Globalization.CultureInfo.InvariantCulture)
            : duration.ToString(
                @"m\:ss",
                System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatResources(GameSession session)
    {
        var cpu = session.AverageCpuPercent is null
            ? "CPU unavailable"
            : $"{session.AverageCpuPercent.Value:F0}% CPU";
        var memory = session.PeakWorkingSetBytes is null
            ? "memory unavailable"
            : $"{session.PeakWorkingSetBytes.Value / 1024d / 1024d:F0} MB peak";
        return $"{cpu}, {memory}";
    }

    private async Task PersistDetailsAsync(GameDefinition updated)
    {
        await gameLibrary!.SaveGameAsync(updated);
        OperationStatus = updated.Warnings.IsEmpty
            ? "Game configuration saved."
            : $"Game configuration saved with warning: {string.Join(" ", updated.Warnings)}";
        RefreshLibrary();
        OpenGameDetails(updated.Id);
    }

    private string ProfileName(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileCatalog is null)
        {
            return "No profile";
        }

        return profileCatalog.Profiles.SingleOrDefault(profile => profile.Id == profileId)?.Name
            ?? "Missing profile";
    }

    private void OpenProfiles()
    {
        ProfileManager?.OpenCommand.Execute(null);
    }

    private static string IconForProfile(ProfileKind kind) => kind switch
    {
        ProfileKind.BatterySaver => IconCatalog.Battery,
        ProfileKind.Balanced => IconCatalog.Shield,
        ProfileKind.Gaming => IconCatalog.Gaming,
        ProfileKind.Competitive => IconCatalog.Competitive,
        _ => IconCatalog.Shield,
    };

    private void OnActiveProfileChanged(object? sender, ProfileDefinition profile)
    {
        ActiveProfileName = profile.Name;
        ActiveProfileDescription = profile.Description;
        PowerValue = profile.Name;
    }

    private void OnManagedActiveProfileChanged(object? sender, SystemProfile profile)
    {
        ActiveProfileName = profile.Name;
        ActiveProfileDescription = profile.Description;
        PowerValue = profile.Name;
    }

    private void OnLibraryChanged(object? sender, EventArgs eventArgs) => RefreshLibrary();

    private void OnSessionMetricsChanged(object? sender, GameProcessMetrics metrics)
    {
        CpuValue = $"{metrics.CpuPercent:F0}%";
        MemoryValue = $"{metrics.WorkingSetBytes / 1024d / 1024d:F0} MB";
        MetricsStatus = "LIVE GAME SESSION";
    }
}

public sealed record GamingProfileOption(string Id, string Name);

public sealed record GameSessionItemViewModel(
    string Started,
    string Outcome,
    string Duration,
    string Priority,
    string Resources,
    string Summary);
