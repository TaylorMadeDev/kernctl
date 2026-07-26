using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kernctl.Core.Profiles;

public static class ProfileJson
{
    public const int MaximumImportBytes = 256 * 1024;

    public static JsonSerializerOptions Options { get; } = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public sealed class ProfileDataException(string message, Exception? innerException = null)
    : Exception(message, innerException);
