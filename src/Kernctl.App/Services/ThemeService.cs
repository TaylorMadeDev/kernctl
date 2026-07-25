using Kernctl.Core.Themes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kernctl.App.Services;

public sealed class ThemeService(
    ThemeStore store,
    IThemeResourceSink resourceSink,
    ILogger<ThemeService>? logger = null) : IThemeService
{
    private static readonly Action<ILogger, string, Exception?> LogThemeLoadFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogThemeLoadFailure)),
            "Theme data could not be loaded: {ThemeLoadError}");

    private readonly ILogger<ThemeService> logger = logger ?? NullLogger<ThemeService>.Instance;
    private readonly List<ThemeDefinition> themes = [.. BuiltInThemes.All];
    private ThemeDefinition activeTheme = BuiltInThemes.Default;
    private ThemeDefinition committedTheme = BuiltInThemes.Default;
    private bool initialized;

    public event EventHandler? ThemeChanged;

    public IReadOnlyList<ThemeDefinition> AvailableThemes => themes;

    public ThemeDefinition ActiveTheme => activeTheme;

    public ThemeDefinition CommittedTheme => committedTheme;

    public bool HasPreview { get; private set; }

    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        var snapshot = await store.LoadAsync(cancellationToken);
        foreach (var theme in snapshot.CustomThemes.Where(theme => !theme.IsBuiltIn))
        {
            AddOrReplace(theme);
        }

        LoadErrors = snapshot.Errors;
        foreach (var error in LoadErrors)
        {
            LogThemeLoadFailure(logger, error, null);
        }

        committedTheme = FindTheme(snapshot.ActiveThemeId) ?? BuiltInThemes.Default;
        activeTheme = committedTheme;
        resourceSink.Apply(activeTheme);
        initialized = true;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BeginPreview()
    {
        HasPreview = true;
    }

    public void ApplyPreview(ThemeDefinition theme)
    {
        EnsureValid(theme);
        HasPreview = true;
        activeTheme = theme;
        resourceSink.Apply(theme);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CommitAsync(
        ThemeDefinition theme,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(theme);
        if (!theme.IsBuiltIn)
        {
            if (themes.Any(candidate =>
                    candidate.Id != theme.Id
                    && string.Equals(candidate.Name, theme.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ThemeDataException($"A theme named '{theme.Name}' already exists.");
            }

            await store.SaveThemeAsync(theme, cancellationToken);
            AddOrReplace(theme);
        }

        await store.SaveActiveThemeAsync(theme.Id, cancellationToken);
        committedTheme = theme;
        activeTheme = theme;
        HasPreview = false;
        resourceSink.Apply(theme);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelPreview()
    {
        activeTheme = committedTheme;
        HasPreview = false;
        resourceSink.Apply(committedTheme);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public ThemeDefinition CreateCustomTheme(string name, ThemeDefinition baseTheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureValid(baseTheme);
        return baseTheme.CreateEditableCopy(CreateId(), CreateUniqueName(name));
    }

    public ThemeDefinition DuplicateTheme(ThemeDefinition source)
    {
        EnsureValid(source);
        return source.CreateEditableCopy(CreateId(), CreateUniqueName($"{source.Name} Copy"));
    }

    public async Task<ThemeDefinition> RenameCustomThemeAsync(
        ThemeDefinition theme,
        string newName,
        CancellationToken cancellationToken = default)
    {
        EnsureEditable(theme);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var normalizedName = newName.Trim();
        if (themes.Any(candidate =>
                candidate.Id != theme.Id
                && string.Equals(candidate.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ThemeDataException($"A theme named '{normalizedName}' already exists.");
        }

        var renamed = theme with { Name = normalizedName };
        EnsureValid(renamed);
        await store.SaveThemeAsync(renamed, cancellationToken);
        AddOrReplace(renamed);
        if (committedTheme.Id == renamed.Id)
        {
            committedTheme = renamed;
            activeTheme = renamed;
            await store.SaveActiveThemeAsync(renamed.Id, cancellationToken);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
        return renamed;
    }

    public async Task DeleteCustomThemeAsync(
        ThemeDefinition theme,
        CancellationToken cancellationToken = default)
    {
        EnsureEditable(theme);
        await store.DeleteThemeAsync(theme);
        themes.RemoveAll(candidate => candidate.Id == theme.Id);
        var shouldRestoreCommittedTheme = activeTheme.Id == theme.Id;
        if (committedTheme.Id == theme.Id)
        {
            committedTheme = BuiltInThemes.Default;
            await store.SaveActiveThemeAsync(committedTheme.Id, cancellationToken);
            shouldRestoreCommittedTheme = true;
        }

        if (shouldRestoreCommittedTheme)
        {
            activeTheme = committedTheme;
            HasPreview = false;
            resourceSink.Apply(committedTheme);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public ThemeDefinition ResetTheme(ThemeDefinition theme)
    {
        EnsureValid(theme);
        var baseTheme = theme.IsBuiltIn
            ? BuiltInThemes.Get(theme.Id)
            : FindTheme(theme.BaseThemeId ?? BuiltInThemes.DefaultThemeId) ?? BuiltInThemes.Default;
        return theme.IsBuiltIn
            ? baseTheme
            : baseTheme.CreateEditableCopy(theme.Id, theme.Name);
    }

    public async Task<ThemeDefinition> ImportThemeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var imported = await ThemeStore.ImportAsync(path, cancellationToken);
        var local = imported.CreateEditableCopy(CreateId(), CreateUniqueName(imported.Name));
        await store.SaveThemeAsync(local, cancellationToken);
        AddOrReplace(local);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        return local;
    }

    public Task ExportThemeAsync(
        ThemeDefinition theme,
        string path,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(theme);
        return ThemeStore.ExportAsync(theme, path, cancellationToken);
    }

    private static void EnsureValid(ThemeDefinition theme)
    {
        var errors = ThemeValidation.Validate(theme);
        if (errors.Count > 0)
        {
            throw new ThemeDataException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void EnsureEditable(ThemeDefinition theme)
    {
        EnsureValid(theme);
        if (theme.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in themes cannot be changed or deleted.");
        }
    }

    private ThemeDefinition? FindTheme(string id) =>
        themes.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));

    private void AddOrReplace(ThemeDefinition theme)
    {
        var index = themes.FindIndex(candidate => candidate.Id == theme.Id);
        if (index >= 0)
        {
            themes[index] = theme;
        }
        else
        {
            themes.Add(theme);
        }
    }

    private string CreateUniqueName(string requestedName)
    {
        var baseName = requestedName.Trim();
        var name = baseName;
        var suffix = 2;
        while (themes.Any(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {suffix++}";
        }

        return name;
    }

    private static string CreateId() => $"custom-{Guid.NewGuid():N}";
}
