using Kernctl.App.ViewModels.Pages;
using Kernctl.Core.Models;
using Kernctl.Core.Services;

namespace Kernctl.Core.Tests;

public sealed class GamingPageViewModelTests
{
    [Fact]
    public void ExposesExactlySixFunctionalNavigationTools()
    {
        var viewModel = CreateViewModel(out _);

        Assert.Equal(6, viewModel.Tools.Count);
        Assert.All(viewModel.Tools, tool => Assert.NotNull(tool.Command));
        Assert.All(viewModel.Tools, tool => Assert.True(tool.HasNavigation));
        Assert.Equal(
            "FPS provider unavailable.",
            viewModel.Tools.Single(tool => tool.Title == "FPS Monitoring").Description);
    }

    [Fact]
    public void ConfirmingProfileOnlyUpdatesSessionProfileState()
    {
        var viewModel = CreateViewModel(out var profiles);
        viewModel.OpenProfileDialogCommand.Execute(null);
        viewModel.SelectedProfileChoice = viewModel.ProfileChoices.Single(
            choice => choice.Kind == ProfileKind.Competitive);

        viewModel.ConfirmProfileCommand.Execute(null);

        Assert.Equal(ProfileKind.Competitive, profiles.ActiveProfile.Kind);
        Assert.Equal("Competitive", viewModel.ActiveProfileName);
        Assert.False(viewModel.IsProfileDialogOpen);
    }

    [Fact]
    public async Task SampleMetricsAreNeverPresentedAsLiveData()
    {
        var viewModel = CreateViewModel(out _);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal("METRICS UNAVAILABLE", viewModel.MetricsStatus);
        Assert.Equal("Unavailable", viewModel.CpuValue);
    }

    private static GamingPageViewModel CreateViewModel(out ProfileService profiles)
    {
        profiles = new ProfileService();
        return new GamingPageViewModel(profiles, new SampleMetricsService());
    }

    private sealed class SampleMetricsService : ISystemMetricsService
    {
        public ValueTask<SystemMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new SystemMetricsSnapshot(18, 42, "Balanced", true, DateTimeOffset.UtcNow));
    }
}
