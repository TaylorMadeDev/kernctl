using CommunityToolkit.Mvvm.ComponentModel;
using Kernctl.App.Services;

namespace Kernctl.App.ViewModels.Pages;

public enum SettingsSection
{
    Appearance,
    Behaviour,
    About,
}

public sealed class SettingsPageViewModel : ObservableObject
{
    private SettingsSection selectedSection = SettingsSection.Appearance;

    public SettingsPageViewModel(
        IThemeService themeService,
        IThemeFileDialogService fileDialogService)
    {
        Appearance = new AppearancePageViewModel(themeService, fileDialogService);
    }

    public IReadOnlyList<SettingsSection> Sections { get; } = Enum.GetValues<SettingsSection>();

    public AppearancePageViewModel Appearance { get; }

    public SettingsSection SelectedSection
    {
        get => selectedSection;
        set
        {
            if (SetProperty(ref selectedSection, value))
            {
                OnPropertyChanged(nameof(IsAppearanceSelected));
                OnPropertyChanged(nameof(IsBehaviourSelected));
                OnPropertyChanged(nameof(IsAboutSelected));
            }
        }
    }

    public bool IsAppearanceSelected => SelectedSection == SettingsSection.Appearance;

    public bool IsBehaviourSelected => SelectedSection == SettingsSection.Behaviour;

    public bool IsAboutSelected => SelectedSection == SettingsSection.About;
}
