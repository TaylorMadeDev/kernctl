using Kernctl.App.ViewModels;
using Kernctl.App.ViewModels.Pages;
using Kernctl.Core.Models;
using Kernctl.Core.Services;

namespace Kernctl.Core.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void GamingIsInitiallySelectedAndPageStateIsCached()
    {
        var viewModel = CreateViewModel();
        var gamingPage = viewModel.CurrentPage;

        viewModel.NavigateTo("Overview");
        viewModel.NavigateTo("Gaming");

        Assert.Equal("Gaming", viewModel.SelectedNavigation.Title);
        Assert.Same(gamingPage, viewModel.CurrentPage);
    }

    [Fact]
    public void SearchFiltersToolsAndNavigatesToTheirPage()
    {
        var viewModel = CreateViewModel();
        viewModel.BeginSearch();

        viewModel.SearchQuery = "launcher";

        var result = Assert.Single(viewModel.SearchResults);
        Assert.Equal("Game Launcher", result.Title);
        viewModel.ActivateSelectedSearchResult();
        Assert.Equal("Gaming", viewModel.SelectedNavigation.Title);
        Assert.False(viewModel.IsSearchOpen);
    }

    [Fact]
    public void NavigationRejectsUnknownDestination()
    {
        var viewModel = CreateViewModel();

        Assert.Throws<ArgumentException>(() => viewModel.NavigateTo("Unknown"));
    }

    [Fact]
    public void EveryDeclaredNavigationDestinationCanBeOpened()
    {
        var viewModel = CreateViewModel();

        foreach (var destination in viewModel.NavigationItems)
        {
            viewModel.NavigateTo(destination.Title);
            Assert.Equal(destination, viewModel.SelectedNavigation);
            Assert.NotNull(viewModel.CurrentPage);
        }
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var profiles = new ProfileService();
        var gaming = new GamingPageViewModel(profiles, new TestMetricsService());
        return new MainWindowViewModel(gaming);
    }

    private sealed class TestMetricsService : ISystemMetricsService
    {
        public ValueTask<SystemMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SystemMetricsSnapshot(1, 2, "Test", true, DateTimeOffset.UtcNow));
    }
}
