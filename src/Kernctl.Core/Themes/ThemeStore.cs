using System.Text;
using System.Text.Json;

namespace Kernctl.Core.Themes;

public sealed class ThemeStore
{
    private const int SettingsSchemaVersion = 1;
    private readonly string rootDirectory;

    public ThemeStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string SettingsPath => Path.Combine(rootDirectory, "settings.json");

    public string ThemesDirectory => Path.Combine(rootDirectory, "themes");

    public async Task<ThemeStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ThemesDirectory);
        var themes = new List<ThemeDefinition>();
        var errors = new List<string>();

        foreach (var path in Directory.EnumerateFiles(ThemesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                themes.Add(await ReadThemeFileAsync(path, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ThemeDataException)
            {
                errors.Add($"Could not load '{Path.GetFileName(path)}': {exception.Message}");
            }
        }

        var activeThemeId = BuiltInThemes.DefaultThemeId;
        if (File.Exists(SettingsPath))
        {
            try
            {
                var settingsJson = await File.ReadAllTextAsync(SettingsPath, cancellationToken);
                var settings = JsonSerializer.Deserialize<ThemeSettings>(settingsJson, ThemeJson.Options);
                if (settings?.SchemaVersion == SettingsSchemaVersion
                    && !string.IsNullOrWhiteSpace(settings.ActiveThemeId))
                {
                    activeThemeId = settings.ActiveThemeId;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"Could not load settings: {exception.Message}");
            }
        }

        var themeExists = BuiltInThemes.All.Any(theme => theme.Id == activeThemeId)
            || themes.Any(theme => theme.Id == activeThemeId);
        return new ThemeStoreSnapshot(
            themes,
            themeExists ? activeThemeId : BuiltInThemes.DefaultThemeId,
            errors);
    }

    public async Task SaveThemeAsync(ThemeDefinition theme, CancellationToken cancellationToken = default)
    {
        if (theme.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in themes cannot be persisted as custom themes.");
        }

        var json = ThemeJson.Serialize(theme);
        Directory.CreateDirectory(ThemesDirectory);
        var fileName = ThemeValidation.SanitizeFileName(theme.Id) + ".json";
        await WriteAtomicAsync(Path.Combine(ThemesDirectory, fileName), json, cancellationToken);
    }

    public Task DeleteThemeAsync(ThemeDefinition theme)
    {
        if (theme.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in themes cannot be deleted.");
        }

        var fileName = ThemeValidation.SanitizeFileName(theme.Id) + ".json";
        var path = Path.Combine(ThemesDirectory, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task SaveActiveThemeAsync(string themeId, CancellationToken cancellationToken = default)
    {
        var settings = new ThemeSettings
        {
            ActiveThemeId = themeId,
        };
        var json = JsonSerializer.Serialize(settings, ThemeJson.Options);
        Directory.CreateDirectory(rootDirectory);
        await WriteAtomicAsync(SettingsPath, json, cancellationToken);
    }

    public static async Task<ThemeDefinition> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new ThemeDataException("The selected theme file does not exist.");
        }

        if (information.Length > ThemeJson.MaximumImportBytes)
        {
            throw new ThemeDataException("Theme files must be 256 KB or smaller.");
        }

        return await ReadThemeFileAsync(information.FullName, cancellationToken);
    }

    public static async Task ExportAsync(
        ThemeDefinition theme,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!ThemeValidation.IsSafeThemeFileName(Path.GetFileName(path)))
        {
            throw new ThemeDataException("Theme exports must use a safe .json filename.");
        }

        await WriteAtomicAsync(Path.GetFullPath(path), ThemeJson.Serialize(theme), cancellationToken);
    }

    private static async Task<ThemeDefinition> ReadThemeFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var information = new FileInfo(path);
        if (information.Length > ThemeJson.MaximumImportBytes)
        {
            throw new ThemeDataException("Theme file exceeds the 256 KB size limit.");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return ThemeJson.Deserialize(json);
    }

    private static async Task WriteAtomicAsync(
        string destination,
        string contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ThemeDataException("Theme destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                contents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record ThemeSettings
    {
        public int SchemaVersion { get; init; } = SettingsSchemaVersion;

        public required string ActiveThemeId { get; init; }
    }
}

public sealed record ThemeStoreSnapshot(
    IReadOnlyList<ThemeDefinition> CustomThemes,
    string ActiveThemeId,
    IReadOnlyList<string> Errors);
