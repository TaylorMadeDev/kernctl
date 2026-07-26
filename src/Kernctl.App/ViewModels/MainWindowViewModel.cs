using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kernctl.App.Services;
using Kernctl.App.ViewModels.Actions;
using Kernctl.App.ViewModels.Pages;
using Kernctl.App.ViewModels.Profiles;

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
    private NavigationItemViewModel? pendingNavigation;
    private bool isUnsavedNavigationDialogOpen;

    public MainWindowViewModel(
        GamingPageViewModel gaming,
        SettingsPageViewModel settings,
        IThemeService themeService,
        ActionRecoveryViewModel? actionRecovery = null,
        ActionProgressViewModel? actionProgress = null)
    {
        Gaming = gaming;
        Settings = settings;
        ThemeService = themeService;
        ActionRecovery = actionRecovery;
        ActionProgress = actionProgress;
        DiscardThemeChangesCommand = new RelayCommand(DiscardThemeChanges);
        KeepEditingThemeCommand = new RelayCommand(KeepEditingTheme);

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
            ["Settings"] = Settings,
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

    public ProfileManagerViewModel? Profiles => Gaming.ProfileManager;

    public SettingsPageViewModel Settings { get; }

    public IThemeService ThemeService { get; }

    public ActionRecoveryViewModel? ActionRecovery { get; }

    public ActionProgressViewModel? ActionProgress { get; }

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    public IRelayCommand DiscardThemeChangesCommand { get; }

    public IRelayCommand KeepEditingThemeCommand { get; }

    public NavigationItemViewModel SelectedNavigation
    {
        get => selectedNavigation;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == selectedNavigation)
            {
                return;
            }

            if (selectedNavigation.Title == "Settings"
                && value.Title != "Settings"
                && Settings.Appearance.IsDirty)
            {
                pendingNavigation = value;
                IsUnsavedNavigationDialogOpen = true;
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref selectedNavigation, value))
            {
                CurrentPage = pages[value.Title];
                if (value.Title == "Settings")
                {
                    Settings.Appearance.BeginPreviewSession();
                }
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

    public bool IsUnsavedNavigationDialogOpen
    {
        get => isUnsavedNavigationDialogOpen;
        private set => SetProperty(ref isUnsavedNavigationDialogOpen, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Gaming.InitializeAsync(cancellationToken);
        if (ActionRecovery is not null)
        {
            await ActionRecovery.InitializeAsync(cancellationToken);
        }
    }

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

    public void RequestAppearanceCancel()
    {
        if (!Settings.Appearance.IsDirty)
        {
            return;
        }

        pendingNavigation = null;
        IsUnsavedNavigationDialogOpen = true;
    }

    public void CancelThemePreviewOnExit() => ThemeService.CancelPreview();

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

    private void DiscardThemeChanges()
    {
        Settings.Appearance.RequestCancel();
        IsUnsavedNavigationDialogOpen = false;
        if (pendingNavigation is not null)
        {
            var destination = pendingNavigation;
            pendingNavigation = null;
            selectedNavigation = destination;
            OnPropertyChanged(nameof(SelectedNavigation));
            CurrentPage = pages[destination.Title];
        }
    }

    private void KeepEditingTheme()
    {
        pendingNavigation = null;
        IsUnsavedNavigationDialogOpen = false;
        OnPropertyChanged(nameof(SelectedNavigation));
    }
}
