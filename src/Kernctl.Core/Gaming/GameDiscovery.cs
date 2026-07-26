using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kernctl.Core.Gaming;

public interface IGameDiscoveryProvider
{
    GameSource Source { get; }

    Task<GameDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
}

public sealed partial class SteamGameDiscoveryProvider(
    IEnumerable<string>? steamRoots = null) : IGameDiscoveryProvider
{
    private const long MaximumMetadataBytes = 2 * 1024 * 1024;
    private readonly ImmutableArray<string> configuredRoots =
        steamRoots?.Where(path => !string.IsNullOrWhiteSpace(path)).ToImmutableArray() ?? [];

    public GameSource Source => GameSource.Steam;

    public async Task<GameDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var games = ImmutableArray.CreateBuilder<GameDefinition>();
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (var steamRoot in GetSteamRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(steamRoot, "steamapps");
            foreach (var library in await ReadLibrariesAsync(steamApps, errors, cancellationToken))
            {
                foreach (var manifestPath in EnumerateFilesSafely(
                             library,
                             "appmanifest_*.acf",
                             errors,
                             "Steam"))
                {
                    try
                    {
                        var text = await ReadMetadataAsync(manifestPath, cancellationToken);
                        var appId = FindValue(text, "appid");
                        var name = FindValue(text, "name");
                        var installDirectoryName = FindValue(text, "installdir");
                        if (string.IsNullOrWhiteSpace(appId)
                            || string.IsNullOrWhiteSpace(name)
                            || string.IsNullOrWhiteSpace(installDirectoryName)
                            || !ulong.TryParse(appId, out _))
                        {
                            errors.Add($"Steam manifest '{Path.GetFileName(manifestPath)}' is incomplete.");
                            continue;
                        }

                        var commonDirectory = Path.GetFullPath(Path.Combine(library, "common"));
                        var installDirectory = Path.GetFullPath(
                            Path.Combine(commonDirectory, installDirectoryName));
                        if (!IsWithin(installDirectory, commonDirectory))
                        {
                            errors.Add(
                                $"Steam manifest '{Path.GetFileName(manifestPath)}' contained an unsafe install directory.");
                            continue;
                        }

                        var exists = Directory.Exists(installDirectory);
                        games.Add(new GameDefinition
                        {
                            Id = StableId(GameSource.Steam, appId),
                            Name = SafeMetadataText(name, 160, "Unnamed Steam game"),
                            Source = GameSource.Steam,
                            ExternalId = appId,
                            Installation = new(
                                ExecutablePath: null,
                                InstallDirectory: installDirectory,
                                MetadataPath: manifestPath,
                                LocalArtworkPath: null,
                                exists
                                    ? GameInstallState.NeedsExecutable
                                    : GameInstallState.Missing),
                            AddedAtUtc = DateTimeOffset.UtcNow,
                            LastDiscoveredAtUtc = DateTimeOffset.UtcNow,
                            Warnings = exists
                                ? ["Steam metadata does not provide a safe executable path. Choose the game executable before launching."]
                                : ["The Steam installation directory is missing."],
                        });
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException
                            or IOException
                            or UnauthorizedAccessException
                            or ArgumentException
                            or NotSupportedException)
                    {
                        errors.Add(
                            $"Steam manifest '{Path.GetFileName(manifestPath)}' could not be read: {exception.Message}");
                    }
                }
            }
        }

        return new(games.ToImmutable(), errors.ToImmutable());
    }

    private ImmutableArray<string> GetSteamRoots()
    {
        if (!configuredRoots.IsEmpty)
        {
            return configuredRoots;
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return string.IsNullOrWhiteSpace(programFilesX86)
            ? []
            : [Path.Combine(programFilesX86, "Steam")];
    }

    private static async Task<IReadOnlyList<string>> ReadLibrariesAsync(
        string steamApps,
        ImmutableArray<string>.Builder errors,
        CancellationToken cancellationToken)
    {
        var libraries = new List<string>();
        if (Directory.Exists(steamApps))
        {
            libraries.Add(steamApps);
        }

        var libraryFile = Path.Combine(steamApps, "libraryfolders.vdf");
        if (!File.Exists(libraryFile))
        {
            return libraries;
        }

        try
        {
            var text = await ReadMetadataAsync(libraryFile, cancellationToken);
            foreach (Match match in VdfPairRegex().Matches(text))
            {
                if (!string.Equals(match.Groups["key"].Value, "path", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = UnescapeVdf(match.Groups["value"].Value);
                if (!Path.IsPathFullyQualified(value))
                {
                    continue;
                }

                var library = Path.Combine(Path.GetFullPath(value), "steamapps");
                if (Directory.Exists(library))
                {
                    libraries.Add(library);
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            errors.Add($"Steam library metadata could not be read: {exception.Message}");
        }

        return libraries;
    }

    private static string? FindValue(string text, string key)
    {
        foreach (Match match in VdfPairRegex().Matches(text))
        {
            if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return UnescapeVdf(match.Groups["value"].Value);
            }
        }

        return null;
    }

    private static string UnescapeVdf(string value) =>
        value.Replace(@"\\", @"\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);

    internal static async Task<string> ReadMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaximumMetadataBytes)
        {
            throw new InvalidDataException("Launcher metadata exceeds the 2 MB safety limit.");
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    internal static string SafeMetadataText(string value, int maximumLength, string fallback)
    {
        var safe = new string(value
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray())
            .Trim();
        return safe.Length == 0 ? fallback : safe;
    }

    private static bool IsWithin(string path, string directory)
    {
        var normalizedDirectory =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        "\"(?<key>(?:\\\\.|[^\"])*)\"\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex VdfPairRegex();

    internal static Guid StableId(GameSource source, string externalId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{source}:{externalId}".ToUpperInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static IEnumerable<string> EnumerateFilesSafely(
        string directory,
        string pattern,
        ImmutableArray<string>.Builder errors,
        string source)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray()
                : [];
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            errors.Add($"{source} metadata directory could not be scanned: {exception.Message}");
            return [];
        }
    }
}

public sealed class EpicGameDiscoveryProvider(
    string? manifestDirectory = null) : IGameDiscoveryProvider
{
    private readonly string configuredManifestDirectory = manifestDirectory
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "EpicGamesLauncher",
            "Data",
            "Manifests");

    public GameSource Source => GameSource.Epic;

    public async Task<GameDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var games = ImmutableArray.CreateBuilder<GameDefinition>();
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (var manifestPath in SteamGameDiscoveryProvider.EnumerateFilesSafely(
                     configuredManifestDirectory,
                     "*.item",
                     errors,
                     "Epic"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(manifestPath).Length > 2 * 1024 * 1024)
                {
                    errors.Add(
                        $"Epic manifest '{Path.GetFileName(manifestPath)}' exceeds the 2 MB safety limit.");
                    continue;
                }

                await using var stream = File.OpenRead(manifestPath);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken);
                var root = document.RootElement;
                var externalId = ReadString(root, "CatalogItemId")
                    ?? ReadString(root, "AppName");
                var name = ReadString(root, "DisplayName") ?? ReadString(root, "AppName");
                var installLocation = ReadString(root, "InstallLocation");
                var launchExecutable = ReadString(root, "LaunchExecutable");
                if (string.IsNullOrWhiteSpace(externalId)
                    || string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(installLocation))
                {
                    errors.Add($"Epic manifest '{Path.GetFileName(manifestPath)}' is incomplete.");
                    continue;
                }

                var installDirectory = Path.GetFullPath(installLocation);
                var warnings = ImmutableArray.CreateBuilder<string>();
                string? executablePath = null;
                if (!string.IsNullOrWhiteSpace(launchExecutable))
                {
                    var candidate = Path.GetFullPath(Path.Combine(installDirectory, launchExecutable));
                    if (IsWithin(candidate, installDirectory)
                        && string.Equals(
                            Path.GetExtension(candidate),
                            ".exe",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        executablePath = candidate;
                    }
                    else
                    {
                        warnings.Add("Epic metadata contained an unsafe launch path and it was ignored.");
                    }
                }

                var state = !Directory.Exists(installDirectory)
                    ? GameInstallState.Missing
                    : executablePath is null
                        ? GameInstallState.NeedsExecutable
                        : File.Exists(executablePath)
                            ? GameInstallState.Installed
                            : GameInstallState.Missing;
                if (state == GameInstallState.Missing)
                {
                    warnings.Add("The Epic installation or executable is missing.");
                }

                if (executablePath is not null)
                {
                    var launchValidation = GameValidation.ValidateLaunch(
                        executablePath,
                        installDirectory,
                        [],
                        requireExistingExecutable: false);
                    warnings.AddRange(launchValidation.Warnings);
                    if (!launchValidation.IsValid)
                    {
                        warnings.AddRange(launchValidation.Errors);
                        state = GameInstallState.Invalid;
                        executablePath = null;
                    }
                }

                games.Add(new GameDefinition
                {
                    Id = SteamGameDiscoveryProvider.StableId(GameSource.Epic, externalId),
                    Name = SteamGameDiscoveryProvider.SafeMetadataText(
                        name,
                        160,
                        "Unnamed Epic game"),
                    Source = GameSource.Epic,
                    ExternalId = SteamGameDiscoveryProvider.SafeMetadataText(
                        externalId,
                        160,
                        "unknown"),
                    Installation = new(
                        executablePath,
                        installDirectory,
                        manifestPath,
                        LocalArtworkPath: null,
                        state),
                    Launch = new()
                    {
                        WorkingDirectory = installDirectory,
                    },
                    AddedAtUtc = DateTimeOffset.UtcNow,
                    LastDiscoveredAtUtc = DateTimeOffset.UtcNow,
                    Warnings = warnings.ToImmutable(),
                });
            }
            catch (Exception exception) when (
                exception is JsonException
                    or IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                errors.Add(
                    $"Epic manifest '{Path.GetFileName(manifestPath)}' could not be read: {exception.Message}");
            }
        }

        return new(games.ToImmutable(), errors.ToImmutable());
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsWithin(string path, string directory)
    {
        var normalizedDirectory =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
