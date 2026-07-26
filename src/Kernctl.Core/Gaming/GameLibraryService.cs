using System.Collections.Immutable;

namespace Kernctl.Core.Gaming;

public interface IGameLibraryService
{
    IReadOnlyList<GameDefinition> Games { get; }

    IReadOnlyList<GameSession> Sessions { get; }

    GameLibraryPreferences Preferences { get; }

    IReadOnlyList<string> Errors { get; }

    event EventHandler? LibraryChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task RescanAsync(CancellationToken cancellationToken = default);

    Task<GameDefinition> AddManualAsync(
        string executablePath,
        bool suspiciousLocationConfirmed = false,
        CancellationToken cancellationToken = default);

    Task SaveGameAsync(GameDefinition game, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid gameId, CancellationToken cancellationToken = default);

    Task SavePreferencesAsync(
        GameLibraryPreferences preferences,
        CancellationToken cancellationToken = default);

    Task RecordSessionAsync(GameSession session, CancellationToken cancellationToken = default);

    GameDefinition GetRequired(Guid gameId);
}

public sealed class GameLibraryService(
    IGameLibraryStore store,
    IEnumerable<IGameDiscoveryProvider> discoveryProviders) : IGameLibraryService, IDisposable
{
    private readonly List<GameDefinition> games = [];
    private readonly List<GameSession> sessions = [];
    private readonly List<string> errors = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;

    public IReadOnlyList<GameDefinition> Games => games;

    public IReadOnlyList<GameSession> Sessions => sessions;

    public GameLibraryPreferences Preferences { get; private set; } = new();

    public IReadOnlyList<string> Errors => errors;

    public event EventHandler? LibraryChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            var snapshot = await store.LoadAsync(cancellationToken);
            games.AddRange(snapshot.Games);
            sessions.AddRange(snapshot.Sessions);
            Preferences = snapshot.Preferences;
            errors.AddRange(snapshot.Errors);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }

        await RescanAsync(cancellationToken);
    }

    public async Task RescanAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var discovered = new List<GameDefinition>();
            errors.Clear();
            foreach (var provider in discoveryProviders)
            {
                try
                {
                    var result = await provider.DiscoverAsync(cancellationToken);
                    discovered.AddRange(result.Games);
                    errors.AddRange(result.Errors);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException)
                {
                    errors.Add($"{provider.Source} discovery failed: {exception.Message}");
                }
            }

            var refreshedExisting = games.Select(RefreshInstallState).ToArray();
            var merged = Merge(refreshedExisting, discovered);
            games.Clear();
            games.AddRange(merged.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase));
            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<GameDefinition> AddManualAsync(
        string executablePath,
        bool suspiciousLocationConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        var validation = GameValidation.ValidateLaunch(
            executablePath,
            workingDirectory: null,
            arguments: []);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Errors[0]);
        }

        if (!validation.Warnings.IsEmpty && !suspiciousLocationConfirmed)
        {
            throw new GameConfirmationRequiredException(validation.Warnings);
        }

        var normalizedPath = validation.NormalizedExecutablePath!;
        var existing = games.FirstOrDefault(game =>
            game.Installation.ExecutablePath is not null
            && string.Equals(
                GameValidation.NormalizeIdentityPath(game.Installation.ExecutablePath),
                GameValidation.NormalizeIdentityPath(normalizedPath),
                StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var game = new GameDefinition
        {
            Id = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(normalizedPath),
            Source = GameSource.Manual,
            Installation = new(
                normalizedPath,
                validation.NormalizedWorkingDirectory,
                MetadataPath: null,
                LocalArtworkPath: null,
                GameInstallState.Installed),
            Launch = new()
            {
                WorkingDirectory = validation.NormalizedWorkingDirectory,
                Priority = Preferences.DefaultPriority,
            },
            AddedAtUtc = now,
            LastDiscoveredAtUtc = now,
            Warnings = validation.Warnings,
        };
        await gate.WaitAsync(cancellationToken);
        try
        {
            games.Add(game);
            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return game;
    }

    public async Task SaveGameAsync(
        GameDefinition game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!GameValidation.IsAllowedPriority(game.Launch.Priority))
        {
            throw new InvalidDataException("Realtime and unknown process priorities are not allowed.");
        }

        if (game.Installation.ExecutablePath is not null)
        {
            var validation = GameValidation.ValidateLaunch(
                game.Installation.ExecutablePath,
                game.Launch.WorkingDirectory,
                game.Launch.Arguments,
                requireExistingExecutable: false);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(validation.Errors[0]);
            }
        }
        else
        {
            var argumentErrors = GameValidation.ValidateArguments(game.Launch.Arguments);
            if (!argumentErrors.IsEmpty)
            {
                throw new InvalidDataException(argumentErrors[0]);
            }
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var index = games.FindIndex(item => item.Id == game.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Game '{game.Id}' is not in the library.");
            }

            games[index] = game;
            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            games.RemoveAll(game => game.Id == gameId);
            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SavePreferencesAsync(
        GameLibraryPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!GameValidation.IsAllowedPriority(preferences.DefaultPriority))
        {
            throw new InvalidDataException("Realtime and unknown process priorities are not allowed.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            Preferences = preferences;
            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RecordSessionAsync(
        GameSession session,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            sessions.Add(session);
            if (sessions.Count > 100)
            {
                sessions.RemoveRange(0, sessions.Count - 100);
            }

            var index = games.FindIndex(game => game.Id == session.GameId);
            if (index >= 0)
            {
                games[index] = games[index] with { LastPlayedAtUtc = session.StartedAtUtc };
            }

            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public GameDefinition GetRequired(Guid gameId) =>
        games.SingleOrDefault(game => game.Id == gameId)
        ?? throw new KeyNotFoundException($"Game '{gameId}' is not in the library.");

    public void Dispose() => gate.Dispose();

    internal static IReadOnlyList<GameDefinition> Merge(
        IEnumerable<GameDefinition> existing,
        IEnumerable<GameDefinition> discovered)
    {
        var result = existing.ToList();
        foreach (var candidate in discovered)
        {
            var index = result.FindIndex(current => IsSameGame(current, candidate));
            if (index < 0)
            {
                result.Add(candidate);
                continue;
            }

            var saved = result[index];
            var executable = saved.Installation.ExecutablePath
                ?? candidate.Installation.ExecutablePath;
            var state = executable is not null && File.Exists(executable)
                ? GameInstallState.Installed
                : candidate.Installation.State;
            result[index] = candidate with
            {
                Id = saved.Id,
                AddedAtUtc = saved.AddedAtUtc,
                LastPlayedAtUtc = saved.LastPlayedAtUtc,
                Launch = saved.Launch,
                Installation = candidate.Installation with
                {
                    ExecutablePath = executable,
                    State = state,
                },
                Warnings = [.. saved.Warnings.Concat(candidate.Warnings).Distinct()],
            };
        }

        return result;
    }

    private static bool IsSameGame(GameDefinition left, GameDefinition right)
    {
        if (left.Installation.ExecutablePath is not null
            && right.Installation.ExecutablePath is not null
            && string.Equals(
                GameValidation.NormalizeIdentityPath(left.Installation.ExecutablePath),
                GameValidation.NormalizeIdentityPath(right.Installation.ExecutablePath),
                StringComparison.Ordinal))
        {
            return true;
        }

        return left.Source == right.Source
            && !string.IsNullOrWhiteSpace(left.ExternalId)
            && string.Equals(left.ExternalId, right.ExternalId, StringComparison.OrdinalIgnoreCase);
    }

    private static GameDefinition RefreshInstallState(GameDefinition game)
    {
        var executable = game.Installation.ExecutablePath;
        if (executable is not null)
        {
            var validation = GameValidation.ValidateLaunch(
                executable,
                game.Launch.WorkingDirectory,
                game.Launch.Arguments,
                requireExistingExecutable: false);
            if (!validation.IsValid
                || !GameValidation.IsAllowedPriority(game.Launch.Priority))
            {
                return game with
                {
                    Installation = game.Installation with { State = GameInstallState.Invalid },
                    Launch = game.Launch with { Priority = GameProcessPriority.Normal },
                    Warnings =
                    [
                        .. game.Warnings
                            .Concat(validation.Errors)
                            .Append("The saved game configuration is invalid and cannot be launched.")
                            .Distinct(),
                    ],
                };
            }

            var exists = File.Exists(executable);
            return game with
            {
                Installation = game.Installation with
                {
                    State = exists ? GameInstallState.Installed : GameInstallState.Missing,
                },
                Warnings = exists
                    ? [.. game.Warnings.Where(warning =>
                        !warning.Contains("missing", StringComparison.OrdinalIgnoreCase))]
                    : [.. game.Warnings.Append("The configured executable is missing.").Distinct()],
            };
        }

        var installExists = game.Installation.InstallDirectory is not null
            && Directory.Exists(game.Installation.InstallDirectory);
        return game with
        {
            Installation = game.Installation with
            {
                State = installExists
                    ? GameInstallState.NeedsExecutable
                    : GameInstallState.Missing,
            },
        };
    }

    private Task PersistAsync(CancellationToken cancellationToken) =>
        store.SaveAsync(
            new([.. games], [.. sessions], Preferences, [.. errors]),
            cancellationToken);
}

public sealed class GameConfirmationRequiredException(ImmutableArray<string> warnings)
    : InvalidOperationException(warnings.FirstOrDefault() ?? "Confirmation is required.")
{
    public ImmutableArray<string> Warnings { get; } = warnings;
}
