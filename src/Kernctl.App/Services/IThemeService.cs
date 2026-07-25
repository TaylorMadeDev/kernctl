using Kernctl.Core.Themes;

namespace Kernctl.App.Services;

public interface IThemeService
{
    event EventHandler? ThemeChanged;

    IReadOnlyList<ThemeDefinition> AvailableThemes { get; }

    ThemeDefinition ActiveTheme { get; }

    ThemeDefinition CommittedTheme { get; }

    bool HasPreview { get; }

    IReadOnlyList<string> LoadErrors { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    void BeginPreview();

    void ApplyPreview(ThemeDefinition theme);

    Task CommitAsync(ThemeDefinition theme, CancellationToken cancellationToken = default);

    void CancelPreview();

    ThemeDefinition CreateCustomTheme(string name, ThemeDefinition baseTheme);

    ThemeDefinition DuplicateTheme(ThemeDefinition source);

    Task<ThemeDefinition> RenameCustomThemeAsync(
        ThemeDefinition theme,
        string newName,
        CancellationToken cancellationToken = default);

    Task DeleteCustomThemeAsync(
        ThemeDefinition theme,
        CancellationToken cancellationToken = default);

    ThemeDefinition ResetTheme(ThemeDefinition theme);

    Task<ThemeDefinition> ImportThemeAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task ExportThemeAsync(
        ThemeDefinition theme,
        string path,
        CancellationToken cancellationToken = default);
}

public interface IThemeResourceSink
{
    void Apply(ThemeDefinition theme);
}
