using CommunityToolkit.Mvvm.ComponentModel;
using Kernctl.App.Services;
using Kernctl.Core.Profiles;

namespace Kernctl.App.ViewModels.Profiles;

public sealed partial class ProfileCardViewModel(SystemProfile profile, bool isActive)
    : ObservableObject
{
    [ObservableProperty]
    private bool isActive = isActive;

    public SystemProfile Profile { get; } = profile;

    public string Id => Profile.Id;

    public string Name => Profile.Name;

    public string Description => Profile.Description;

    public int ActionCount => Profile.OrderedActions.Length;

    public string ActionCountText => $"{ActionCount} configured action{(ActionCount == 1 ? string.Empty : "s")}";

    public string AutomaticStatus => Profile.TriggerConfiguration.IsEnabled
        ? Profile.TriggerConfiguration.AutomaticBehaviourApproved
            ? "Automatic · approved"
            : "Automatic · approval needed"
        : "Manual";

    public bool IsFullySupported { get; set; } = true;

    public string SupportStatus => IsFullySupported ? "Supported" : "Check support";

    public bool IsBuiltIn => Profile.IsBuiltIn;

    public string Icon => Profile.Icon switch
    {
        ProfileIcon.Battery => IconCatalog.Battery,
        ProfileIcon.Balanced => IconCatalog.Shield,
        ProfileIcon.Gaming => IconCatalog.Gaming,
        ProfileIcon.Competitive => IconCatalog.Competitive,
        _ => IconCatalog.Optimize,
    };
}
