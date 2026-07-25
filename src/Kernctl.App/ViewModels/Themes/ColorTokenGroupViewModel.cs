namespace Kernctl.App.ViewModels.Themes;

public sealed record ColorTokenGroupViewModel(
    string Name,
    IReadOnlyList<ColorTokenEditorViewModel> Tokens);
