using Kernctl.App.Services;
using Kernctl.App.ViewModels;
using Kernctl.App.ViewModels.Pages;
using Kernctl.Core.Models;
using Kernctl.Core.Services;
using Kernctl.Core.Themes;

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
        var themeService = new ThemeService(
            new ThemeStore(Path.Combine(Path.GetTempPath(), $"kernctl-tests-{Guid.NewGuid():N}")),
            new TestThemeResourceSink());
        themeService.InitializeAsync().GetAwaiter().GetResult();
        var settings = new SettingsPageViewModel(themeService, new TestThemeFileDialogService());
        return new MainWindowViewModel(gaming, settings, themeService);
    }

    private sealed class TestMetricsService : ISystemMetricsService
    {
        public ValueTask<SystemMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SystemMetricsSnapshot(1, 2, "Test", true, DateTimeOffset.UtcNow));
    }

    private sealed class TestThemeResourceSink : IThemeResourceSink
    {
        public void Apply(ThemeDefinition theme)
        {
        }
    }

    private sealed class TestThemeFileDialogService : IThemeFileDialogService
    {
        public Task<string?> PickImportPathAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickExportPathAsync(string suggestedFileName) =>
            Task.FromResult<string?>(null);
    }
}
