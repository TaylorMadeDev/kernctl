using CommunityToolkit.Mvvm.ComponentModel;

namespace Kernctl.App.ViewModels;

public sealed class ToolCardViewModel : ObservableObject
{
    private bool isToggled;

    public ToolCardViewModel(
        string title,
        string description,
        string icon,
        bool hasToggle,
        bool initialToggleState = false)
    {
        Title = title;
        Description = description;
        Icon = icon;
        HasToggle = hasToggle;
        IsToggled = initialToggleState;
    }

    public string Title { get; }

    public string Description { get; }

    public string Icon { get; }

    public bool HasToggle { get; }

    public bool HasNavigation => !HasToggle;

    public string ToggleAccessibleLabel => $"{Title} {(IsToggled ? "enabled" : "disabled")}";

    public bool IsToggled
    {
        get => isToggled;
        set
        {
            if (SetProperty(ref isToggled, value))
            {
                OnPropertyChanged(nameof(ToggleAccessibleLabel));
            }
        }
    }
}
