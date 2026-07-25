using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kernctl.Core.Themes;

public static class ThemeJson
{
    public const long MaximumImportBytes = 256 * 1024;

    public static JsonSerializerOptions Options { get; } = new()
    {
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(ThemeDefinition theme)
    {
        EnsureValid(theme);
        return JsonSerializer.Serialize(theme, Options);
    }

    public static ThemeDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ThemeDefinition theme;
        try
        {
            theme = JsonSerializer.Deserialize<ThemeDefinition>(json, Options)
                ?? throw new ThemeDataException("Theme file did not contain a theme definition.");
        }
        catch (JsonException exception)
        {
            throw new ThemeDataException("Theme file contains malformed JSON.", exception);
        }

        EnsureValid(theme);
        return theme;
    }

    private static void EnsureValid(ThemeDefinition theme)
    {
        var errors = ThemeValidation.Validate(theme);
        if (errors.Count > 0)
        {
            throw new ThemeDataException(string.Join(Environment.NewLine, errors));
        }
    }
}

public sealed class ThemeDataException : Exception
{
    public ThemeDataException(string message)
        : base(message)
    {
    }

    public ThemeDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
