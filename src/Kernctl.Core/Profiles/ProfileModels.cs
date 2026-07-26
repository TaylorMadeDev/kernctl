using System.Collections.Immutable;

namespace Kernctl.Core.Profiles;

public enum ProfileIcon
{
    Battery,
    Balanced,
    Gaming,
    Competitive,
    Custom,
}

public enum ProfileAccent
{
    Green,
    Violet,
    Blue,
    Amber,
    Red,
}

public enum ProfileActionKind
{
    PowerScheme,
    Monitoring,
    KernctlPreference,
}

public enum KnownPowerScheme
{
    PowerSaver,
    Balanced,
    HighPerformance,
}

public enum MonitoringFeature
{
    Fps,
}

public enum KernctlPreference
{
    PerformanceMode,
}

public enum ProfileTriggerKind
{
    GameStarted,
    GameExited,
    BatteryPower,
    AcPower,
    KernctlStarted,
}

public enum ProfilePlanDisposition
{
    WillChange,
    AlreadyConfigured,
    Unsupported,
    RequiresConfirmation,
    RequiresRestart,
}

public enum ProfileOutcome
{
    Succeeded,
    PartialSuccess,
    Failed,
    RolledBack,
    RollbackFailed,
}

public sealed record ProfileActionDefinition
{
    public required Guid Id { get; init; }

    public required ProfileActionKind Kind { get; init; }

    public required string TargetKey { get; init; }

    public bool IsRequired { get; init; } = true;

    public bool RequiresConfirmation { get; init; }

    public KnownPowerScheme? PowerScheme { get; init; }

    public MonitoringFeature? MonitoringFeature { get; init; }

    public KernctlPreference? Preference { get; init; }

    public bool? Enabled { get; init; }

    public static ProfileActionDefinition Power(
        KnownPowerScheme scheme,
        bool isRequired = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = ProfileActionKind.PowerScheme,
            TargetKey = "windows.power-scheme",
            PowerScheme = scheme,
            IsRequired = isRequired,
        };

    public static ProfileActionDefinition Monitoring(
        Profiles.MonitoringFeature feature,
        bool enabled,
        bool isRequired = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = ProfileActionKind.Monitoring,
            TargetKey = $"kernctl.monitoring.{feature.ToString().ToLowerInvariant()}",
            MonitoringFeature = feature,
            Enabled = enabled,
            IsRequired = isRequired,
        };

    public static ProfileActionDefinition PreferenceToggle(
        Profiles.KernctlPreference preference,
        bool enabled,
        bool isRequired = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = ProfileActionKind.KernctlPreference,
            TargetKey = $"kernctl.preference.{preference.ToString().ToLowerInvariant()}",
            Preference = preference,
            Enabled = enabled,
            IsRequired = isRequired,
        };
}

public sealed record ProfileTriggerDefinition
{
    public required Guid Id { get; init; }

    public required ProfileTriggerKind Kind { get; init; }

    public string? SelectedExecutablePath { get; init; }

    public int Priority { get; init; }

    public bool RestorePreviousProfileOnExit { get; init; }
}

public sealed record ProfileTriggerConfiguration
{
    public bool IsEnabled { get; init; }

    public bool AutomaticBehaviourApproved { get; init; }

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);

    public ImmutableArray<ProfileTriggerDefinition> Triggers { get; init; } = [];
}

public sealed record SystemProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public ProfileIcon Icon { get; init; }

    public ProfileAccent Accent { get; init; }

    public bool IsBuiltIn { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public ProfileTriggerConfiguration TriggerConfiguration { get; init; } = new();

    public ImmutableArray<ProfileActionDefinition> OrderedActions { get; init; } = [];
}

public sealed record ProfileValidationIssue(string Code, string Message, Guid? ActionId = null);

public sealed record ProfileValidationResult(
    bool IsValid,
    ImmutableArray<ProfileValidationIssue> Issues);

public sealed record ProfileActionPlanItem(
    Guid DefinitionId,
    string ActionId,
    string FriendlyName,
    string CurrentValue,
    string ProposedValue,
    string Explanation,
    bool IsRequired,
    bool IsReversible,
    string Privilege,
    ProfilePlanDisposition Disposition,
    ImmutableArray<string> Messages);

public sealed record ProfileApplicationPlan(
    Guid PlanId,
    SystemProfile Profile,
    DateTimeOffset CreatedAtUtc,
    ImmutableArray<ProfileActionPlanItem> Actions,
    Kernctl.Core.Actions.ActionTransactionPlan? TransactionPlan,
    ProfileValidationResult Validation)
{
    public bool CanApply =>
        Validation.IsValid
        && Actions.Length > 0
        && Actions.All(action =>
            !action.IsRequired || action.Disposition != ProfilePlanDisposition.Unsupported)
        && TransactionPlan is { CanExecute: true };
}

public sealed record ProfileApplicationResult(
    Guid ActivationId,
    string ProfileId,
    string ProfileName,
    Guid? TransactionId,
    ProfileOutcome Outcome,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ImmutableArray<ProfileActionResult> Actions,
    string Summary);

public sealed record ProfileActionResult(
    Guid DefinitionId,
    string FriendlyName,
    bool Succeeded,
    string Summary);

public sealed record ProfileActivationHistoryEntry(
    Guid ActivationId,
    string ProfileId,
    string ProfileName,
    string Trigger,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    ProfileOutcome Outcome,
    int ActionsApplied,
    int FailedActions,
    string RollbackStatus,
    Guid? TransactionId);

public sealed record ProfileTriggerEvent(
    ProfileTriggerKind Kind,
    DateTimeOffset OccurredAtUtc,
    string? ExecutablePath = null);

public sealed record ProfileTriggerDecision(
    bool ShouldActivate,
    string? ProfileId,
    string Reason,
    bool ShouldRestorePreviousProfile = false);
