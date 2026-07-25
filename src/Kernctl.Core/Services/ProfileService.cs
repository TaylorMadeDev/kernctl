using Kernctl.Core.Models;

namespace Kernctl.Core.Services;

/// <summary>Default session-only implementation of profile selection.</summary>
public sealed class ProfileService : IProfileService
{
    private static readonly IReadOnlyList<ProfileDefinition> AvailableProfiles =
    [
        new(ProfileKind.BatterySaver, "Battery Saver", "Prioritizes lower power use"),
        new(ProfileKind.Balanced, "Balanced", "Default system profile"),
        new(ProfileKind.Gaming, "Gaming", "Prioritizes smooth gameplay"),
        new(ProfileKind.Competitive, "Competitive", "Prioritizes latency-sensitive play"),
    ];

    public ProfileService()
    {
        ActiveProfile = AvailableProfiles.Single(profile => profile.Kind == ProfileKind.Balanced);
    }

    public ProfileDefinition ActiveProfile { get; private set; }

    public IReadOnlyList<ProfileDefinition> Profiles => AvailableProfiles;

    public event EventHandler<ProfileDefinition>? ActiveProfileChanged;

    public void SelectProfile(ProfileKind kind)
    {
        var selected = AvailableProfiles.SingleOrDefault(profile => profile.Kind == kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown profile.");

        if (selected == ActiveProfile)
        {
            return;
        }

        ActiveProfile = selected;
        ActiveProfileChanged?.Invoke(this, selected);
    }
}
