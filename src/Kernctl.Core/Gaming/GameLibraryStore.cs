using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kernctl.Core.Gaming;

public interface IGameLibraryStore
{
    Task<GameLibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(GameLibrarySnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class GameLibraryStore(string applicationDataRoot) : IGameLibraryStore
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumSessions = 100;
    private const int MaximumGames = 5000;
    private const long MaximumLibraryBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string filePath = Path.Combine(applicationDataRoot, "gaming-library.json");

    public async Task<GameLibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return GameLibrarySnapshot.Empty;
        }

        try
        {
            if (new FileInfo(filePath).Length > MaximumLibraryBytes)
            {
                return WithError("The game library exceeds the 8 MB safety limit.");
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<PersistedLibrary>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document is null)
            {
                return WithError("The game library file was empty.");
            }

            if (document.SchemaVersion is < 0 or > CurrentSchemaVersion)
            {
                return WithError(
                    $"Game library schema {document.SchemaVersion} is not supported by this build.");
            }

            var migrated = Migrate(document);
            var games = migrated.Games.IsDefault
                ? []
                : migrated.Games
                    .Where(game => game is not null
                        && game.Installation is not null
                        && game.Launch is not null)
                    .Take(MaximumGames)
                    .Select(Sanitize)
                    .ToImmutableArray();
            var sessions = migrated.Sessions.IsDefault
                ? []
                : migrated.Sessions
                    .Where(session => session is not null)
                    .TakeLast(MaximumSessions)
                    .Select(Sanitize)
                    .ToImmutableArray();
            return new(
                games,
                sessions,
                Sanitize(migrated.Preferences),
                []);
        }
        catch (Exception exception) when (
            exception is JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return WithError($"The game library could not be loaded: {exception.Message}");
        }
    }

    public async Task SaveAsync(
        GameLibrarySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = filePath + ".tmp";
        var persisted = new PersistedLibrary(
            CurrentSchemaVersion,
            [.. snapshot.Games.Take(MaximumGames).Select(Sanitize)],
            [.. snapshot.Sessions.TakeLast(MaximumSessions).Select(Sanitize)],
            Sanitize(snapshot.Preferences));
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static PersistedLibrary Migrate(PersistedLibrary document) =>
        document.SchemaVersion == 0
            ? document with
            {
                SchemaVersion = CurrentSchemaVersion,
                Preferences = document.Preferences ?? new(),
            }
            : document;

    private static GameDefinition Sanitize(GameDefinition game)
    {
        var warnings = game.Warnings.IsDefault ? [] : game.Warnings;
        var arguments = game.Launch.Arguments.IsDefault ? [] : game.Launch.Arguments;
        return game with
        {
            Name = SafeText(game.Name, 160, "Unnamed game"),
            ExternalId = SafeOptionalText(game.ExternalId, 160),
            Warnings = [.. warnings.Select(value => SafeText(value, 300, "Warning"))],
            Launch = game.Launch with
            {
                Priority = GameValidation.IsAllowedPriority(game.Launch.Priority)
                    ? game.Launch.Priority
                    : GameProcessPriority.Normal,
                Arguments =
                [
                    .. arguments
                        .Take(GameValidation.MaximumArguments)
                        .Select(value => SafeText(
                            value,
                            GameValidation.MaximumArgumentLength,
                            string.Empty)),
                ],
            },
        };
    }

    private static GameLibraryPreferences Sanitize(GameLibraryPreferences? preferences)
    {
        var value = preferences ?? new();
        return value with
        {
            DefaultPriority = GameValidation.IsAllowedPriority(value.DefaultPriority)
                ? value.DefaultPriority
                : GameProcessPriority.Normal,
            OverlayPreferences = (value.OverlayPreferences ?? ImmutableDictionary<string, bool>.Empty)
                .Where(pair => pair.Key.Length is > 0 and <= 80
                    && pair.Key.All(character =>
                        char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                .Take(32)
                .ToImmutableDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
        };
    }

    private static GameSession Sanitize(GameSession session) => session with
    {
        GameName = SafeText(session.GameName, 160, "Unknown game"),
        Summary = RedactSummary(session.Summary),
        ProcessId = null,
    };

    private static string RedactSummary(string value)
    {
        var safe = SafeText(value, 300, "Session ended.");
        if (safe.Contains('\\') || safe.Contains('/') || safe.Contains('='))
        {
            return "Session ended. Technical paths and environment data were not retained.";
        }

        return safe;
    }

    private static string SafeText(string? value, int maximumLength, string fallback)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string? SafeOptionalText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return SafeText(value, maximumLength, string.Empty);
    }

    private static GameLibrarySnapshot WithError(string error) =>
        new([], [], new(), [SafeText(error, 300, "The game library could not be loaded.")]);

    private sealed record PersistedLibrary(
        int SchemaVersion,
        ImmutableArray<GameDefinition> Games,
        ImmutableArray<GameSession> Sessions,
        GameLibraryPreferences? Preferences);
}
