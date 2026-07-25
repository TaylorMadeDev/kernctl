using System.Text.RegularExpressions;

namespace Kernctl.Core.Themes;

public static partial class ThemeValidation
{
    public static IReadOnlyList<string> Validate(ThemeDefinition? theme)
    {
        if (theme is null)
        {
            return ["Theme data is missing."];
        }

        var errors = new List<string>();
        if (theme.SchemaVersion != ThemeDefinition.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported theme schema version {theme.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(theme.Id) || !ThemeIdPattern().IsMatch(theme.Id))
        {
            errors.Add("Theme ID must contain only lowercase letters, numbers, and single hyphens.");
        }

        if (string.IsNullOrWhiteSpace(theme.Name) || theme.Name.Trim().Length is < 1 or > 80)
        {
            errors.Add("Theme name must contain between 1 and 80 characters.");
        }

        if (theme.Typography.Scale is < 0.9 or > 1.2)
        {
            errors.Add("Font scale must be between 90% and 120%.");
        }

        ValidateColors(theme.Colors, errors);
        ValidateSpacing(theme.Spacing, errors);
        return errors;
    }

    public static string SanitizeFileName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim().ToLowerInvariant();
        var characters = normalized
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        var sanitized = ConsecutiveHyphens().Replace(new string(characters), "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "theme" : sanitized[..Math.Min(64, sanitized.Length)];
    }

    public static bool IsSafeThemeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName == Path.GetFileName(fileName)
        && !fileName.Contains("..", StringComparison.Ordinal)
        && string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase);

    private static void ValidateColors(ThemeColors? colors, List<string> errors)
    {
        if (colors is null)
        {
            errors.Add("Theme colours are missing.");
            return;
        }

        var values = new Dictionary<string, string?>
        {
            [nameof(colors.WindowBackground)] = colors.WindowBackground,
            [nameof(colors.SidebarBackground)] = colors.SidebarBackground,
            [nameof(colors.SurfacePrimary)] = colors.SurfacePrimary,
            [nameof(colors.SurfaceSecondary)] = colors.SurfaceSecondary,
            [nameof(colors.SurfaceElevated)] = colors.SurfaceElevated,
            [nameof(colors.BorderSubtle)] = colors.BorderSubtle,
            [nameof(colors.BorderStrong)] = colors.BorderStrong,
            [nameof(colors.TextPrimary)] = colors.TextPrimary,
            [nameof(colors.TextSecondary)] = colors.TextSecondary,
            [nameof(colors.TextMuted)] = colors.TextMuted,
            [nameof(colors.AccentPrimary)] = colors.AccentPrimary,
            [nameof(colors.AccentHover)] = colors.AccentHover,
            [nameof(colors.AccentPressed)] = colors.AccentPressed,
            [nameof(colors.Success)] = colors.Success,
            [nameof(colors.Warning)] = colors.Warning,
            [nameof(colors.Danger)] = colors.Danger,
            [nameof(colors.FocusRing)] = colors.FocusRing,
            [nameof(colors.SelectionBackground)] = colors.SelectionBackground,
        };

        foreach (var (name, value) in values)
        {
            if (!ThemeColor.TryParse(value, out _))
            {
                errors.Add($"{name} must use #RRGGBB or #AARRGGBB format.");
            }
        }
    }

    private static void ValidateSpacing(ThemeSpacing? spacing, List<string> errors)
    {
        if (spacing is null)
        {
            errors.Add("Theme spacing is missing.");
            return;
        }

        if (spacing.ControlHeight is < 28 or > 56
            || spacing.CardPadding is < 8 or > 32
            || spacing.NavigationItemHeight is < 38 or > 72
            || spacing.PageSpacing is < 12 or > 40
            || spacing.GridGap is < 6 or > 28)
        {
            errors.Add("One or more spacing values are outside the supported range.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeIdPattern();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ConsecutiveHyphens();
}
