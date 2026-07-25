using Kernctl.Core.Models;
using Kernctl.Core.Services;

namespace Kernctl.Core.Tests;

public sealed class ProfileServiceTests
{
    [Fact]
    public void StartsBalanced()
    {
        var service = new ProfileService();

        Assert.Equal(ProfileKind.Balanced, service.ActiveProfile.Kind);
    }

    [Fact]
    public void SelectingProfileRaisesSingleChange()
    {
        var service = new ProfileService();
        ProfileDefinition? changed = null;
        service.ActiveProfileChanged += (_, profile) => changed = profile;

        service.SelectProfile(ProfileKind.Gaming);

        Assert.Equal(ProfileKind.Gaming, service.ActiveProfile.Kind);
        Assert.Same(service.ActiveProfile, changed);
    }
}
