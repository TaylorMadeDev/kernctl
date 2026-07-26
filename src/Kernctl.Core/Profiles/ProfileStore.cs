using System.Text;
using System.Text.Json;

namespace Kernctl.Core.Profiles;

public sealed record ProfileStoreSnapshot(
    IReadOnlyList<SystemProfile> CustomProfiles,
    string ActiveProfileId,
    IReadOnlyList<string> Errors);

public interface IProfileStore
{
    Task<ProfileStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SystemProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(SystemProfile profile, CancellationToken cancellationToken = default);

    Task SaveActiveProfileIdAsync(string profileId, CancellationToken cancellationToken = default);
}

public sealed class ProfileStore(string rootDirectory) : IProfileStore
{
    private const int SettingsSchemaVersion = 1;
    private readonly string rootDirectory = Path.GetFullPath(
        string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("A profile storage root is required.", nameof(rootDirectory))
            : rootDirectory);

    public string ProfilesDirectory => Path.Combine(rootDirectory, "profiles");

    public string SettingsPath => Path.Combine(rootDirectory, "profile-settings.json");

    public async Task<ProfileStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ProfilesDirectory);
        var profiles = new List<SystemProfile>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                profiles.Add(await ReadProfileFileAsync(path, cancellationToken));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ProfileDataException)
            {
                errors.Add($"Could not load '{Path.GetFileName(path)}': {exception.Message}");
            }
        }

        var activeProfileId = BuiltInProfiles.DefaultProfileId;
        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(SettingsPath, cancellationToken);
                var settings = JsonSerializer.Deserialize<ProfileSettings>(json, ProfileJson.Options);
                if (settings?.SchemaVersion == SettingsSchemaVersion
                    && !string.IsNullOrWhiteSpace(settings.ActiveProfileId))
                {
                    activeProfileId = settings.ActiveProfileId;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"Could not load profile settings: {exception.Message}");
            }
        }

        var activeExists = BuiltInProfiles.All.Any(profile => profile.Id == activeProfileId)
            || profiles.Any(profile => profile.Id == activeProfileId);
        return new(
            profiles,
            activeExists ? activeProfileId : BuiltInProfiles.DefaultProfileId,
            errors);
    }

    public async Task SaveAsync(
        SystemProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in profiles cannot be overwritten.");
        }

        var validation = ProfileValidation.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ProfileDataException(validation.Issues[0].Message);
        }

        Directory.CreateDirectory(ProfilesDirectory);
        var path = Path.Combine(
            ProfilesDirectory,
            ProfileValidation.SanitizeFileName(profile.Id) + ".json");
        var json = JsonSerializer.Serialize(profile, ProfileJson.Options);
        await WriteAtomicAsync(path, json, cancellationToken);
    }

    public Task DeleteAsync(
        SystemProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in profiles cannot be deleted.");
        }

        var path = Path.Combine(
            ProfilesDirectory,
            ProfileValidation.SanitizeFileName(profile.Id) + ".json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task SaveActiveProfileIdAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(
            new ProfileSettings { ActiveProfileId = profileId },
            ProfileJson.Options);
        await WriteAtomicAsync(SettingsPath, json, cancellationToken);
    }

    public static async Task<SystemProfile> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var imported = await ReadProfileFileAsync(Path.GetFullPath(path), cancellationToken);
        return imported with
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = $"{imported.Name} (imported)",
            IsBuiltIn = false,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            TriggerConfiguration = new(),
        };
    }

    public static async Task ExportAsync(
        SystemProfile profile,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (profile.IsBuiltIn)
        {
            throw new ProfileDataException("Duplicate a built-in profile before exporting it.");
        }

        if (!ProfileValidation.IsSafeExportFileName(Path.GetFileName(path)))
        {
            throw new ProfileDataException("Profile exports require a safe .json filename.");
        }

        var json = JsonSerializer.Serialize(profile, ProfileJson.Options);
        await WriteAtomicAsync(Path.GetFullPath(path), json, cancellationToken);
    }

    private static async Task<SystemProfile> ReadProfileFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new ProfileDataException("The selected profile file does not exist.");
        }

        if (information.Length > ProfileJson.MaximumImportBytes)
        {
            throw new ProfileDataException("Profile files must be 256 KB or smaller.");
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var profile = JsonSerializer.Deserialize<SystemProfile>(json, ProfileJson.Options)
                ?? throw new ProfileDataException("The profile document is empty.");
            if (profile.IsBuiltIn)
            {
                throw new ProfileDataException("Imported and stored profiles must be custom data.");
            }

            var validation = ProfileValidation.Validate(profile);
            if (!validation.IsValid)
            {
                throw new ProfileDataException(validation.Issues[0].Message);
            }

            return profile;
        }
        catch (JsonException exception)
        {
            throw new ProfileDataException("The profile document is malformed.", exception);
        }
    }

    internal static async Task WriteAtomicAsync(
        string destination,
        string contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ProfileDataException("The profile destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
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

    private sealed record ProfileSettings
    {
        public int SchemaVersion { get; init; } = SettingsSchemaVersion;

        public required string ActiveProfileId { get; init; }
    }
}
