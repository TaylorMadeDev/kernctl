using System.Text.Json;
using Kernctl.Core.Gaming;

#pragma warning disable xUnit1051 // Temp-file fixtures complete synchronously and are isolated per test.

namespace Kernctl.Core.Tests;

public sealed class GameLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kernctl-game-library-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SteamProviderParsesLocalLibrariesWithoutInventingExecutable()
    {
        var steamRoot = Path.Combine(root, "Steam");
        var extraRoot = Path.Combine(root, "ExtraLibrary");
        var steamApps = Path.Combine(steamRoot, "steamapps");
        var extraApps = Path.Combine(extraRoot, "steamapps");
        Directory.CreateDirectory(Path.Combine(extraApps, "common", "Fixture Game"));
        Directory.CreateDirectory(steamApps);
        await File.WriteAllTextAsync(
            Path.Combine(steamApps, "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "1"
                {
                    "path" "{{extraRoot.Replace(@"\", @"\\", StringComparison.Ordinal)}}"
                }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(extraApps, "appmanifest_123.acf"),
            """
            "AppState"
            {
                "appid" "123"
                "name" "Fixture Game"
                "installdir" "Fixture Game"
            }
            """);

        var result = await new SteamGameDiscoveryProvider([steamRoot])
            .DiscoverAsync();

        var game = Assert.Single(result.Games);
        Assert.Equal("Fixture Game", game.Name);
        Assert.Equal("123", game.ExternalId);
        Assert.Null(game.Installation.ExecutablePath);
        Assert.Equal(GameInstallState.NeedsExecutable, game.Installation.State);
        Assert.Contains(game.Warnings, warning => warning.Contains("safe executable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SteamProviderRejectsInstallDirectoryTraversal()
    {
        var steamRoot = Path.Combine(root, "UnsafeSteam");
        var steamApps = Path.Combine(steamRoot, "steamapps");
        Directory.CreateDirectory(steamApps);
        await File.WriteAllTextAsync(
            Path.Combine(steamApps, "appmanifest_456.acf"),
            """
            "AppState"
            {
                "appid" "456"
                "name" "Unsafe"
                "installdir" "..\..\outside"
            }
            """);

        var result = await new SteamGameDiscoveryProvider([steamRoot]).DiscoverAsync();

        Assert.Empty(result.Games);
        Assert.Contains(result.Errors, error => error.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EpicProviderAcceptsOnlyExecutableInsideInstallRoot()
    {
        var manifests = Path.Combine(root, "EpicManifests");
        var install = Path.Combine(root, "EpicGame");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(Path.Combine(install, "Binaries"));
        var executable = Path.Combine(install, "Binaries", "game.exe");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllTextAsync(
            Path.Combine(manifests, "safe.item"),
            JsonSerializer.Serialize(new
            {
                CatalogItemId = "epic-safe",
                DisplayName = "Epic Fixture",
                InstallLocation = install,
                LaunchExecutable = @"Binaries\game.exe",
            }));
        await File.WriteAllTextAsync(
            Path.Combine(manifests, "unsafe.item"),
            JsonSerializer.Serialize(new
            {
                CatalogItemId = "epic-unsafe",
                DisplayName = "Unsafe Fixture",
                InstallLocation = install,
                LaunchExecutable = @"..\outside.exe",
            }));

        var result = await new EpicGameDiscoveryProvider(manifests).DiscoverAsync();

        var safe = result.Games.Single(game => game.ExternalId == "epic-safe");
        Assert.Equal(Path.GetFullPath(executable), safe.Installation.ExecutablePath);
        Assert.Equal(GameInstallState.Installed, safe.Installation.State);
        var unsafeGame = result.Games.Single(game => game.ExternalId == "epic-unsafe");
        Assert.Null(unsafeGame.Installation.ExecutablePath);
        Assert.Contains(unsafeGame.Warnings, warning => warning.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LaunchValidationRejectsScriptsControlCharactersAndMissingExecutables()
    {
        var script = GameValidation.ValidateLaunch(
            Path.Combine(root, "game.cmd"),
            root,
            ["safe"],
            requireExistingExecutable: false);
        var control = GameValidation.ValidateArguments(["safe", "bad\r\nargument"]);
        var missing = GameValidation.ValidateLaunch(
            Path.Combine(root, "missing.exe"),
            root,
            []);

        Assert.False(script.IsValid);
        Assert.Contains(script.Errors, error => error.Contains(".exe", StringComparison.Ordinal));
        Assert.Contains(control, error => error.Contains("control", StringComparison.OrdinalIgnoreCase));
        Assert.False(missing.IsValid);
        Assert.Contains(missing.Errors, error => error.Contains("no longer exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualAddRequiresSuspiciousLocationConfirmationAndDeduplicatesPath()
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "fixture.exe");
        await File.WriteAllBytesAsync(executable, []);
        var store = new MemoryGameLibraryStore();
        using var service = new GameLibraryService(store, []);
        await service.InitializeAsync();

        await Assert.ThrowsAsync<GameConfirmationRequiredException>(
            () => service.AddManualAsync(executable));
        var first = await service.AddManualAsync(executable, suspiciousLocationConfirmed: true);
        var duplicate = await service.AddManualAsync(executable, suspiciousLocationConfirmed: true);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(service.Games);
    }

    [Fact]
    public async Task RescanDeduplicatesByExecutableAndKeepsManualConfiguration()
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "same.exe");
        await File.WriteAllBytesAsync(executable, []);
        var store = new MemoryGameLibraryStore();
        using var service = new GameLibraryService(
            store,
            [new StaticDiscoveryProvider(new GameDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Discovered name",
                Source = GameSource.Epic,
                ExternalId = "external",
                Installation = new(
                    executable,
                    root,
                    null,
                    null,
                    GameInstallState.Installed),
                AddedAtUtc = DateTimeOffset.UtcNow,
            })]);
        await service.InitializeAsync();
        var discovered = Assert.Single(service.Games);
        await service.SaveGameAsync(discovered with
        {
            Launch = discovered.Launch with { Priority = GameProcessPriority.High },
        });

        await service.RescanAsync();

        var merged = Assert.Single(service.Games);
        Assert.Equal(GameProcessPriority.High, merged.Launch.Priority);
    }

    [Fact]
    public async Task InitializeMarksPersistedMovedExecutableAsMissing()
    {
        var missingPath = Path.Combine(root, "moved.exe");
        var persisted = new GameDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Moved fixture",
            Source = GameSource.Manual,
            Installation = new(
                missingPath,
                root,
                null,
                null,
                GameInstallState.Installed),
            AddedAtUtc = DateTimeOffset.UtcNow,
        };
        var store = new MemoryGameLibraryStore
        {
            Snapshot = new([persisted], [], new(), []),
        };
        using var service = new GameLibraryService(store, []);

        await service.InitializeAsync();

        var game = Assert.Single(service.Games);
        Assert.Equal(GameInstallState.Missing, game.Installation.State);
        Assert.Contains(game.Warnings, warning => warning.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StoreMigratesVersionZeroAndRedactsSessionProcessData()
    {
        Directory.CreateDirectory(root);
        var store = new GameLibraryStore(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "gaming-library.json"),
            """
            {
              "schemaVersion": 0,
              "games": [],
              "sessions": [],
              "preferences": null
            }
            """);
        var migrated = await store.LoadAsync();
        Assert.NotNull(migrated.Preferences);

        var session = new GameSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Fixture",
            444,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            GameSessionOutcome.Completed,
            @"Exited C:\secret\game.exe with TOKEN=value",
            null,
            GameProcessPriority.Normal,
            null,
            null);
        await store.SaveAsync(new([], [session], new(), []));

        var loaded = Assert.Single((await store.LoadAsync()).Sessions);
        Assert.Null(loaded.ProcessId);
        Assert.DoesNotContain("secret", loaded.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOKEN", loaded.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreNormalizesUnknownNumericPriorities()
    {
        var store = new GameLibraryStore(root);
        var game = new GameDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Priority fixture",
            Source = GameSource.Manual,
            Installation = new(null, null, null, null, GameInstallState.Missing),
            Launch = new() { Priority = (GameProcessPriority)999 },
            AddedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(new(
            [game],
            [],
            new() { DefaultPriority = (GameProcessPriority)999 },
            []));
        var loaded = await store.LoadAsync();

        Assert.Equal(GameProcessPriority.Normal, Assert.Single(loaded.Games).Launch.Priority);
        Assert.Equal(GameProcessPriority.Normal, loaded.Preferences.DefaultPriority);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal sealed class MemoryGameLibraryStore : IGameLibraryStore
    {
        public GameLibrarySnapshot Snapshot { get; set; } = GameLibrarySnapshot.Empty;

        public Task<GameLibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task SaveAsync(
            GameLibrarySnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticDiscoveryProvider(GameDefinition game) : IGameDiscoveryProvider
    {
        public GameSource Source => game.Source;

        public Task<GameDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameDiscoveryResult([game], []));
    }
}
