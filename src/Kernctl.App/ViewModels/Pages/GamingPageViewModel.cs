using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.App.Services;
using Kernctl.App.ViewModels.Profiles;
using Kernctl.Core.Models;
using Kernctl.Core.Profiles;
using Kernctl.Core.Services;

namespace Kernctl.App.ViewModels.Pages;

public sealed partial class GamingPageViewModel : ObservableObject
{
    private readonly IProfileService profileService;
    private readonly ISystemMetricsService metricsService;
    private string activeProfileName;
    private string activeProfileDescription;
    private string cpuValue = "—";
    private string memoryValue = "—";
    private string powerValue = "—";
    private string metricsStatus = "Waiting for metrics";
    private bool isProfileDialogOpen;
    private ProfileChoiceViewModel? selectedProfileChoice;

    public GamingPageViewModel(
        IProfileService profileService,
        ISystemMetricsService metricsService,
        ProfileManagerViewModel? profileManager = null)
    {
        this.profileService = profileService;
        this.metricsService = metricsService;
        ProfileManager = profileManager;
        activeProfileName = profileService.ActiveProfile.Name;
        activeProfileDescription = profileService.ActiveProfile.Description;

        Tools =
        [
            new(
                "Performance Mode",
                "Boost system performance for gaming",
                IconCatalog.Optimize,
                hasToggle: true,
                initialToggleState: true),
            new(
                "Game Launcher",
                "Launch and manage your games",
                IconCatalog.Launcher,
                hasToggle: false),
            new(
                "Process Priority",
                "Set priority for game processes",
                IconCatalog.Processor,
                hasToggle: false),
            new(
                "Overlay Manager",
                "Manage in-game overlays",
                IconCatalog.Display,
                hasToggle: false),
            new(
                "Auto Profile",
                "Automatically switch profiles",
                IconCatalog.User,
                hasToggle: false),
            new(
                "FPS Monitoring",
                "Monitor FPS in real time",
                IconCatalog.Chart,
                hasToggle: true,
                initialToggleState: false),
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
    }

    public IReadOnlyList<ToolCardViewModel> Tools { get; }

    public IReadOnlyList<ProfileChoiceViewModel> ProfileChoices { get; }

    public ProfileManagerViewModel? ProfileManager { get; }

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

    public bool IsProfileDialogOpen
    {
        get => isProfileDialogOpen;
        private set => SetProperty(ref isProfileDialogOpen, value);
    }

    public ProfileChoiceViewModel? SelectedProfileChoice
    {
        get => selectedProfileChoice;
        set => SetProperty(ref selectedProfileChoice, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var metrics = await metricsService.GetSnapshotAsync(cancellationToken);
        CpuValue = $"{metrics.CpuPercent}%";
        MemoryValue = $"{metrics.MemoryPercent}%";
        PowerValue = metrics.PowerState;
        MetricsStatus = metrics.IsSample ? "DEVELOPMENT SAMPLE" : "LIVE METRICS";
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
}
