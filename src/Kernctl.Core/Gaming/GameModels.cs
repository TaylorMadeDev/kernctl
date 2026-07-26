using System.Collections.Immutable;

namespace Kernctl.Core.Gaming;

public enum GameSource
{
    Manual,
    Steam,
    Epic,
}

public enum GameInstallState
{
    Installed,
    Missing,
    NeedsExecutable,
    Invalid,
}

public enum GameProcessPriority
{
    Normal,
    AboveNormal,
    High,
}

public enum GameSessionOutcome
{
    Completed,
    LaunchFailed,
    ProfileApplyFailed,
    MonitoringFailed,
    RestoreFailed,
    Cancelled,
}

public sealed record GameInstallation(
    string? ExecutablePath,
    string? InstallDirectory,
    string? MetadataPath,
    string? LocalArtworkPath,
    GameInstallState State);

public sealed record GameLaunchConfiguration
{
    public ImmutableArray<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public string? ProfileId { get; init; }

    public GameProcessPriority Priority { get; init; } = GameProcessPriority.Normal;

    public bool AutoApplyProfile { get; init; }

    public bool RestorePreviousProfileOnExit { get; init; } = true;
}

public sealed record GameDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required GameSource Source { get; init; }

    public string? ExternalId { get; init; }

    public required GameInstallation Installation { get; init; }

    public GameLaunchConfiguration Launch { get; init; } = new();

    public DateTimeOffset AddedAtUtc { get; init; }

    public DateTimeOffset? LastDiscoveredAtUtc { get; init; }

    public DateTimeOffset? LastPlayedAtUtc { get; init; }

    public ImmutableArray<string> Warnings { get; init; } = [];
}

public sealed record GameSession(
    Guid Id,
    Guid GameId,
    string GameName,
    int? ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    TimeSpan Duration,
    GameSessionOutcome Outcome,
    string Summary,
    string? ProfileId,
    GameProcessPriority Priority,
    long? PeakWorkingSetBytes,
    double? AverageCpuPercent);

public sealed record GameLibraryPreferences
{
    public GameProcessPriority DefaultPriority { get; init; } = GameProcessPriority.Normal;

    public bool AutomaticProfilesEnabled { get; init; }

    public bool CompactView { get; init; }

    public ImmutableDictionary<string, bool> OverlayPreferences { get; init; } =
        ImmutableDictionary<string, bool>.Empty;
}

public sealed record GameLibrarySnapshot(
    ImmutableArray<GameDefinition> Games,
    ImmutableArray<GameSession> Sessions,
    GameLibraryPreferences Preferences,
    ImmutableArray<string> Errors)
{
    public static GameLibrarySnapshot Empty { get; } = new([], [], new(), []);
}

public sealed record GameValidationResult(
    bool IsValid,
    string? NormalizedExecutablePath,
    string? NormalizedWorkingDirectory,
    ImmutableArray<string> Errors,
    ImmutableArray<string> Warnings);

public sealed record GameDiscoveryResult(
    ImmutableArray<GameDefinition> Games,
    ImmutableArray<string> Errors);
