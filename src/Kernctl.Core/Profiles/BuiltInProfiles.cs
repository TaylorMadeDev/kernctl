using System.Collections.Immutable;

namespace Kernctl.Core.Profiles;

public static class BuiltInProfiles
{
    public const string BatterySaverId = "battery-saver";
    public const string BalancedId = "balanced";
    public const string GamingId = "gaming";
    public const string CompetitiveId = "competitive";
    public const string DefaultProfileId = BalancedId;

    private static readonly DateTimeOffset BuiltInTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<SystemProfile> All { get; } =
    [
        Create(
            BatterySaverId,
            "Battery Saver",
            "Uses the existing Windows power-saver scheme and quiet kernctl monitoring.",
            ProfileIcon.Battery,
            ProfileAccent.Green,
            [
                ProfileActionDefinition.Power(KnownPowerScheme.PowerSaver),
                ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, false),
                ProfileActionDefinition.PreferenceToggle(KernctlPreference.PerformanceMode, false),
            ]),
        Create(
            BalancedId,
            "Balanced",
            "Restores normal Windows behaviour and is the kernctl default.",
            ProfileIcon.Balanced,
            ProfileAccent.Violet,
            [
                ProfileActionDefinition.Power(KnownPowerScheme.Balanced),
                ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, false),
                ProfileActionDefinition.PreferenceToggle(KernctlPreference.PerformanceMode, false),
            ]),
        Create(
            GamingId,
            "Gaming",
            "Uses conservative settings suitable for most games.",
            ProfileIcon.Gaming,
            ProfileAccent.Blue,
            [
                ProfileActionDefinition.Power(KnownPowerScheme.Balanced),
                ProfileActionDefinition.PreferenceToggle(KernctlPreference.PerformanceMode, true),
                ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true),
            ]),
        Create(
            CompetitiveId,
            "Competitive",
            "Uses the existing high-performance scheme when it is available.",
            ProfileIcon.Competitive,
            ProfileAccent.Amber,
            [
                ProfileActionDefinition.Power(KnownPowerScheme.HighPerformance),
                ProfileActionDefinition.PreferenceToggle(KernctlPreference.PerformanceMode, true),
                ProfileActionDefinition.Monitoring(MonitoringFeature.Fps, true),
            ]),
    ];

    public static SystemProfile GetRequired(string id) =>
        All.SingleOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown built-in profile.");

    private static SystemProfile Create(
        string id,
        string name,
        string description,
        ProfileIcon icon,
        ProfileAccent accent,
        ImmutableArray<ProfileActionDefinition> actions) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            Icon = icon,
            Accent = accent,
            IsBuiltIn = true,
            CreatedAtUtc = BuiltInTimestamp,
            UpdatedAtUtc = BuiltInTimestamp,
            OrderedActions = actions,
        };
}
