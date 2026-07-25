namespace Kernctl.App.ViewModels;

public sealed record SearchResultViewModel(
    string Title,
    string Category,
    string DestinationTitle,
    string SearchText);
