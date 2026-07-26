using CommunityToolkit.Mvvm.ComponentModel;
using Kernctl.Core.Profiles;

namespace Kernctl.App.ViewModels.Profiles;

public sealed partial class ProfileActionEditorViewModel : ObservableObject
{
    private ProfileActionDefinition definition;

    [ObservableProperty]
    private string selectedValue;

    [ObservableProperty]
    private bool isRequired;

    public ProfileActionEditorViewModel(ProfileActionDefinition definition)
    {
        this.definition = definition;
        selectedValue = GetValue(definition);
        isRequired = definition.IsRequired;
    }

    public Guid Id => definition.Id;

    public ProfileActionKind Kind => definition.Kind;

    public string Name => Kind switch
    {
        ProfileActionKind.PowerScheme => "Windows power scheme",
        ProfileActionKind.Monitoring => "FPS monitoring",
        ProfileActionKind.KernctlPreference => "Performance mode",
        _ => "Unsupported action",
    };

    public IReadOnlyList<string> Values => Kind switch
    {
        ProfileActionKind.PowerScheme => ["Power saver", "Balanced", "High performance"],
        _ => ["Off", "On"],
    };

    public ProfileActionDefinition BuildDefinition()
    {
        definition = Kind switch
        {
            ProfileActionKind.PowerScheme => ProfileActionDefinition.Power(
                SelectedValue switch
                {
                    "Power saver" => KnownPowerScheme.PowerSaver,
                    "High performance" => KnownPowerScheme.HighPerformance,
                    _ => KnownPowerScheme.Balanced,
                },
                IsRequired) with
            { Id = Id },
            ProfileActionKind.Monitoring => ProfileActionDefinition.Monitoring(
                MonitoringFeature.Fps,
                SelectedValue == "On",
                IsRequired) with
            { Id = Id },
            ProfileActionKind.KernctlPreference => ProfileActionDefinition.PreferenceToggle(
                KernctlPreference.PerformanceMode,
                SelectedValue == "On",
                IsRequired) with
            { Id = Id },
            _ => definition,
        };
        return definition;
    }

    private static string GetValue(ProfileActionDefinition definition) =>
        definition.Kind switch
        {
            ProfileActionKind.PowerScheme => definition.PowerScheme switch
            {
                KnownPowerScheme.PowerSaver => "Power saver",
                KnownPowerScheme.HighPerformance => "High performance",
                _ => "Balanced",
            },
            _ => definition.Enabled is true ? "On" : "Off",
        };
}
