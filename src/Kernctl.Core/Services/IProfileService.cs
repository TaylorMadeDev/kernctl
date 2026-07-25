using Kernctl.Core.Models;

namespace Kernctl.Core.Services;

/// <summary>Maintains session-local profile selection.</summary>
public interface IProfileService
{
    ProfileDefinition ActiveProfile { get; }

    IReadOnlyList<ProfileDefinition> Profiles { get; }

    event EventHandler<ProfileDefinition>? ActiveProfileChanged;

    void SelectProfile(ProfileKind kind);
}
