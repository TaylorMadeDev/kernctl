using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kernctl.App.Services;
using Kernctl.App.ViewModels.Pages;

namespace Kernctl.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly Dictionary<string, object> pages;
    private readonly IReadOnlyList<SearchResultViewModel> searchIndex;
    private NavigationItemViewModel selectedNavigation;
    private object currentPage;
    private string searchQuery = string.Empty;
    private bool isSearchEngaged;
    private SearchResultViewModel? selectedSearchResult;

    public MainWindowViewModel(GamingPageViewModel gaming)
    {
        Gaming = gaming;

        NavigationItems =
        [
            new("Overview", IconCatalog.Overview, "System summary"),
            new("Storage", IconCatalog.Storage, "Storage tools"),
            new("Gaming", IconCatalog.Gaming, "Gaming controls"),
            new("Optimize", IconCatalog.Optimize, "Safe optimization"),
            new("Internet", IconCatalog.Internet, "Network diagnostics"),
            new("Apps", IconCatalog.Apps, "Application management"),
            new("Settings", IconCatalog.Settings, "Application settings"),
        ];

        pages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Overview"] = new PlaceholderPageViewModel(
                "Overview",
                "A trustworthy system summary will appear here in a future milestone.",
                IconCatalog.Overview),
            ["Storage"] = new PlaceholderPageViewModel(
                "Storage",
                "Storage inspection and safe cleanup tools are not implemented yet.",
                IconCatalog.Storage),
            ["Gaming"] = Gaming,
            ["Optimize"] = new PlaceholderPageViewModel(
                "Optimize",
                "No optimizations are available yet. Future actions will be detectable, explainable, verifiable, and reversible.",
                IconCatalog.Optimize),
            ["Internet"] = new PlaceholderPageViewModel(
                "Internet",
                "Read-only network diagnostics are planned for a future milestone.",
                IconCatalog.Internet),
            ["Apps"] = new PlaceholderPageViewModel(
                "Apps",
                "Application inventory and management are not implemented yet.",
                IconCatalog.Apps),
            ["Settings"] = new PlaceholderPageViewModel(
                "Settings",
                "kernctl settings are not implemented yet.",
                IconCatalog.Settings),
        };

        selectedNavigation = NavigationItems.Single(item => item.Title == "Gaming");
        currentPage = pages[selectedNavigation.Title];

        searchIndex =
        [
            .. NavigationItems.Select(item => new SearchResultViewModel(
                item.Title,
                "Destination",
                item.Title,
                $"{item.Title} {item.Description}")),
            .. Gaming.Tools.Select(tool => new SearchResultViewModel(
                tool.Title,
                "Gaming tool",
                "Gaming",
                $"{tool.Title} {tool.Description} Gaming")),
        ];
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public GamingPageViewModel Gaming { get; }

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    public NavigationItemViewModel SelectedNavigation
    {
        get => selectedNavigation;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref selectedNavigation, value))
            {
                CurrentPage = pages[value.Title];
            }
        }
    }

    public object CurrentPage
    {
        get => currentPage;
        private set => SetProperty(ref currentPage, value);
    }

    public string SearchQuery
    {
        get => searchQuery;
        set
        {
            if (SetProperty(ref searchQuery, value ?? string.Empty))
            {
                RefreshSearchResults();
            }
        }
    }

    public SearchResultViewModel? SelectedSearchResult
    {
        get => selectedSearchResult;
        set => SetProperty(ref selectedSearchResult, value);
    }

    public bool IsSearchOpen => isSearchEngaged && SearchResults.Count > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken) =>
        await Gaming.InitializeAsync(cancellationToken);

    public void BeginSearch()
    {
        isSearchEngaged = true;
        RefreshSearchResults();
    }

    public void CloseSearch()
    {
        isSearchEngaged = false;
        OnPropertyChanged(nameof(IsSearchOpen));
    }

    public void MoveSearchSelection(int delta)
    {
        if (SearchResults.Count == 0)
        {
            return;
        }

        var currentIndex = SelectedSearchResult is null
            ? -1
            : SearchResults.IndexOf(SelectedSearchResult);
        var nextIndex = Math.Clamp(currentIndex + delta, 0, SearchResults.Count - 1);
        SelectedSearchResult = SearchResults[nextIndex];
    }

    public void ActivateSelectedSearchResult()
    {
        var result = SelectedSearchResult ?? SearchResults.FirstOrDefault();
        if (result is null)
        {
            return;
        }

        NavigateTo(result.DestinationTitle);
        SearchQuery = string.Empty;
        CloseSearch();
    }

    public void NavigateTo(string title)
    {
        var destination = NavigationItems.SingleOrDefault(
            item => string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown navigation destination '{title}'.", nameof(title));
        SelectedNavigation = destination;
    }

    private void RefreshSearchResults()
    {
        SearchResults.Clear();
        var terms = SearchQuery.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var result in searchIndex.Where(result =>
                     terms.Length == 0
                     || terms.All(term => result.SearchText.Contains(
                         term,
                         StringComparison.OrdinalIgnoreCase))))
        {
            SearchResults.Add(result);
        }

        SelectedSearchResult = SearchResults.FirstOrDefault();
        OnPropertyChanged(nameof(IsSearchOpen));
    }
}
