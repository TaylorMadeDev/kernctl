using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Kernctl.Core.Actions;

namespace Kernctl.Core.Profiles;

public interface IPowerSchemeService
{
    Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken = default);

    Task<bool> IsSchemeAvailableAsync(
        KnownPowerScheme scheme,
        CancellationToken cancellationToken = default);

    Task SetActiveSchemeAsync(Guid schemeId, CancellationToken cancellationToken = default);

    Guid GetSchemeId(KnownPowerScheme scheme);
}

public interface IKernctlFeatureState
{
    Task<bool> GetMonitoringEnabledAsync(
        MonitoringFeature feature,
        CancellationToken cancellationToken = default);

    Task SetMonitoringEnabledAsync(
        MonitoringFeature feature,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<bool> GetPreferenceAsync(
        KernctlPreference preference,
        CancellationToken cancellationToken = default);

    Task SetPreferenceAsync(
        KernctlPreference preference,
        bool enabled,
        CancellationToken cancellationToken = default);
}

public sealed class KernctlFeatureState : IKernctlFeatureState
{
    private readonly ConcurrentDictionary<MonitoringFeature, bool> monitoring = new();
    private readonly ConcurrentDictionary<KernctlPreference, bool> preferences = new();

    public Task<bool> GetMonitoringEnabledAsync(
        MonitoringFeature feature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(monitoring.GetValueOrDefault(feature));
    }

    public Task SetMonitoringEnabledAsync(
        MonitoringFeature feature,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        monitoring[feature] = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetPreferenceAsync(
        KernctlPreference preference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(preferences.GetValueOrDefault(preference));
    }

    public Task SetPreferenceAsync(
        KernctlPreference preference,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        preferences[preference] = enabled;
        return Task.CompletedTask;
    }
}

public static class ProfileActionIds
{
    public static string For(ProfileActionDefinition definition) =>
        definition.Kind switch
        {
            ProfileActionKind.PowerScheme when definition.PowerScheme is { } scheme =>
                $"windows.power-scheme.{Slug(scheme)}",
            ProfileActionKind.Monitoring
                when definition.MonitoringFeature is { } feature
                && definition.Enabled is { } enabled =>
                $"kernctl.monitoring.{Slug(feature)}.{(enabled ? "on" : "off")}",
            ProfileActionKind.KernctlPreference
                when definition.Preference is { } preference
                && definition.Enabled is { } enabled =>
                $"kernctl.preference.{Slug(preference)}.{(enabled ? "on" : "off")}",
            _ => $"unsupported.{definition.Id:N}",
        };

    private static string Slug<T>(T value)
        where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? $"-{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));
}

public sealed class PowerSchemeSystemAction(
    IPowerSchemeService service,
    KnownPowerScheme desiredScheme) : ISystemAction
{
    private readonly Guid desiredSchemeId = service.GetSchemeId(desiredScheme);

    public ActionDescriptor Descriptor { get; } = new(
        $"windows.power-scheme.{ToSlug(desiredScheme)}",
        1,
        $"Use {Friendly(desiredScheme)} power scheme",
        "Selects an existing Windows power scheme.",
        "Uses the supported Windows power-management API and keeps the exact previous scheme for rollback.",
        SystemActionCategory.Performance,
        ActionRiskLevel.Moderate,
        ActionPrivilegeLevel.StandardUser,
        ActionRestartRequirement.None,
        [ActionPlatform.Windows],
        SupportsRollback: true,
        IsAvailable: OperatingSystem.IsWindows(),
        EstimatedDuration: TimeSpan.FromSeconds(1));

    public async Task<ActionDetectionResult> DetectAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ActionDetectionResult.Unavailable(
                "Not available",
                "Windows power schemes are supported only on Windows.",
                "The current platform is not Windows.");
        }

        if (!await service.IsSchemeAvailableAsync(desiredScheme, cancellationToken))
        {
            return ActionDetectionResult.Unavailable(
                "Scheme not installed",
                $"{Friendly(desiredScheme)} is not available on this device.",
                "kernctl never creates or imports missing power schemes.");
        }

        var active = await service.GetActiveSchemeAsync(cancellationToken);
        return ActionDetectionResult.Available(
            Describe(active),
            active == desiredSchemeId
                ? "The requested power scheme is already active."
                : "Windows reports a different active power scheme.");
    }

    public Task<ActionPlan> PlanAsync(
        ActionExecutionContext context,
        ActionDetectionResult detection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = new ActionPlan(
            Descriptor.Id,
            Descriptor.SchemaVersion,
            detection.CurrentState,
            Friendly(desiredScheme),
            [new("Select existing power scheme", $"Ask Windows to activate {Friendly(desiredScheme)}.")],
            ["windows.power-scheme"],
            Descriptor.RiskLevel,
            Descriptor.RequiredPrivilege,
            Descriptor.RestartRequirement,
            Descriptor.SupportsRollback,
            [],
            detection.UnavailableReasons,
            "Only the active scheme changes; kernctl does not edit scheme settings.");
        return Task.FromResult(plan);
    }

    public async Task<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken) =>
        plan.ActionId == Descriptor.Id
        && plan.ActionSchemaVersion == Descriptor.SchemaVersion
        && await service.IsSchemeAvailableAsync(desiredScheme, cancellationToken)
            ? ActionValidationResult.Valid
            : ActionValidationResult.Invalid(
                new ActionValidationIssue(
                    "power-scheme.unavailable",
                    "The selected power scheme is no longer available."));

    public async Task<ActionStatePayload> CaptureStateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        var active = await service.GetActiveSchemeAsync(cancellationToken);
        return ActionStatePayload.From(1, new PowerSchemeSnapshot(active));
    }

    public async Task<ActionApplyResult> ApplyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await service.SetActiveSchemeAsync(desiredSchemeId, CancellationToken.None);
            return ActionApplyResult.Success($"{Friendly(desiredScheme)} was requested.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return ActionApplyResult.Failure(
                "Windows did not accept the power-scheme change.",
                Error(context, "power-scheme.apply", exception.Message, ActionExecutionStage.Apply),
                mayHaveMutated: true);
        }
    }

    public async Task<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        var active = await service.GetActiveSchemeAsync(cancellationToken);
        return active == desiredSchemeId
            ? ActionVerificationResult.Success($"{Friendly(desiredScheme)} is active.")
            : ActionVerificationResult.Failure(
                "Windows reports a different active power scheme.",
                Error(
                    context,
                    "power-scheme.verify",
                    $"Expected {desiredSchemeId:D}; observed {active:D}.",
                    ActionExecutionStage.Verification));
    }

    public async Task<ActionRollbackResult> RollbackAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        ActionStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var original = snapshot.OriginalState.Deserialize<PowerSchemeSnapshot>(ActionJson.Options)
                ?? throw new InvalidOperationException("The power-scheme snapshot is missing.");
            await service.SetActiveSchemeAsync(original.SchemeId, CancellationToken.None);
            var observed = await service.GetActiveSchemeAsync(cancellationToken);
            return observed == original.SchemeId
                ? ActionRollbackResult.Success("The previous power scheme was restored.")
                : ActionRollbackResult.Failure(
                    "Windows did not report the previous power scheme after rollback.",
                    Error(
                        context,
                        "power-scheme.rollback.verify",
                        $"Expected {original.SchemeId:D}; observed {observed:D}.",
                        ActionExecutionStage.Rollback));
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return ActionRollbackResult.Failure(
                "The previous power scheme could not be restored.",
                Error(context, "power-scheme.rollback", exception.Message, ActionExecutionStage.Rollback));
        }
    }

    private ActionError Error(
        ActionExecutionContext context,
        string code,
        string diagnostic,
        ActionExecutionStage stage) =>
        new(
            code,
            "The Windows power scheme operation did not complete safely.",
            diagnostic,
            Descriptor.Id,
            context.TransactionId,
            stage,
            RetryPossible: true,
            RollbackPossible: true);

    private static string Describe(Guid schemeId) =>
        schemeId == PowerSchemeIds.PowerSaver ? "Power saver"
        : schemeId == PowerSchemeIds.Balanced ? "Balanced"
        : schemeId == PowerSchemeIds.HighPerformance ? "High performance"
        : $"Existing scheme ({schemeId:D})";

    private static string Friendly(KnownPowerScheme scheme) => scheme switch
    {
        KnownPowerScheme.PowerSaver => "Power saver",
        KnownPowerScheme.Balanced => "Balanced",
        KnownPowerScheme.HighPerformance => "High performance",
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, null),
    };

    private static string ToSlug(KnownPowerScheme scheme) => scheme switch
    {
        KnownPowerScheme.PowerSaver => "power-saver",
        KnownPowerScheme.Balanced => "balanced",
        KnownPowerScheme.HighPerformance => "high-performance",
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, null),
    };

    private sealed record PowerSchemeSnapshot(Guid SchemeId);
}

public sealed class KernctlFeatureSystemAction : ISystemAction
{
    private readonly IKernctlFeatureState state;
    private readonly MonitoringFeature? monitoringFeature;
    private readonly KernctlPreference? preference;
    private readonly bool desiredValue;

    public KernctlFeatureSystemAction(
        IKernctlFeatureState state,
        MonitoringFeature feature,
        bool desiredValue)
    {
        this.state = state;
        monitoringFeature = feature;
        this.desiredValue = desiredValue;
        Descriptor = CreateDescriptor(
            $"kernctl.monitoring.{ToSlug(feature)}.{StateSlug(desiredValue)}",
            $"{Friendly(feature)} monitoring",
            desiredValue);
    }

    public KernctlFeatureSystemAction(
        IKernctlFeatureState state,
        KernctlPreference preference,
        bool desiredValue)
    {
        this.state = state;
        this.preference = preference;
        this.desiredValue = desiredValue;
        Descriptor = CreateDescriptor(
            $"kernctl.preference.{ToSlug(preference)}.{StateSlug(desiredValue)}",
            Friendly(preference),
            desiredValue);
    }

    public ActionDescriptor Descriptor { get; }

    public async Task<ActionDetectionResult> DetectAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var current = await GetValueAsync(cancellationToken);
        return ActionDetectionResult.Available(
            OnOff(current),
            current == desiredValue
                ? "This kernctl setting is already configured."
                : "This change affects kernctl only.");
    }

    public Task<ActionPlan> PlanAsync(
        ActionExecutionContext context,
        ActionDetectionResult detection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ActionPlan(
            Descriptor.Id,
            Descriptor.SchemaVersion,
            detection.CurrentState,
            OnOff(desiredValue),
            [new("Update kernctl setting", $"Set {Descriptor.DisplayName} to {OnOff(desiredValue)}.")],
            [Descriptor.Id[..Descriptor.Id.LastIndexOf('.')]],
            Descriptor.RiskLevel,
            Descriptor.RequiredPrivilege,
            Descriptor.RestartRequirement,
            Descriptor.SupportsRollback,
            [],
            [],
            "This is an application preference and does not modify Windows."));
    }

    public Task<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            plan.ActionId == Descriptor.Id && plan.ActionSchemaVersion == Descriptor.SchemaVersion
                ? ActionValidationResult.Valid
                : ActionValidationResult.Invalid(
                    new ActionValidationIssue(
                        "kernctl-setting.plan",
                        "The kernctl setting plan is incompatible.")));
    }

    public async Task<ActionStatePayload> CaptureStateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken) =>
        ActionStatePayload.From(1, new FeatureSnapshot(await GetValueAsync(cancellationToken)));

    public async Task<ActionApplyResult> ApplyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SetValueAsync(desiredValue, CancellationToken.None);
        return ActionApplyResult.Success($"{Descriptor.DisplayName} is now {OnOff(desiredValue)}.");
    }

    public async Task<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        var observed = await GetValueAsync(cancellationToken);
        return observed == desiredValue
            ? ActionVerificationResult.Success($"{Descriptor.DisplayName} was verified.")
            : ActionVerificationResult.Failure(
                "The kernctl setting did not retain its requested value.",
                Error(context, "kernctl-setting.verify", ActionExecutionStage.Verification));
    }

    public async Task<ActionRollbackResult> RollbackAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        ActionStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var original = snapshot.OriginalState.Deserialize<FeatureSnapshot>(ActionJson.Options);
        if (original is null)
        {
            return ActionRollbackResult.Failure(
                "The kernctl setting snapshot is invalid.",
                Error(context, "kernctl-setting.snapshot", ActionExecutionStage.Rollback));
        }

        await SetValueAsync(original.Enabled, CancellationToken.None);
        return await GetValueAsync(cancellationToken) == original.Enabled
            ? ActionRollbackResult.Success("The previous kernctl setting was restored.")
            : ActionRollbackResult.Failure(
                "The previous kernctl setting could not be verified.",
                Error(context, "kernctl-setting.rollback", ActionExecutionStage.Rollback));
    }

    private static ActionDescriptor CreateDescriptor(
        string id,
        string name,
        bool desiredValue) =>
        new(
            id,
            1,
            $"{name}: {OnOff(desiredValue)}",
            "Updates a kernctl-only preference.",
            "This action changes only kernctl's own runtime state and can be restored.",
            SystemActionCategory.Applications,
            ActionRiskLevel.Low,
            ActionPrivilegeLevel.StandardUser,
            ActionRestartRequirement.None,
            [ActionPlatform.Windows],
            SupportsRollback: true,
            IsAvailable: true,
            EstimatedDuration: TimeSpan.FromMilliseconds(100));

    private Task<bool> GetValueAsync(CancellationToken cancellationToken) =>
        monitoringFeature is { } monitoring
            ? state.GetMonitoringEnabledAsync(monitoring, cancellationToken)
            : state.GetPreferenceAsync(preference!.Value, cancellationToken);

    private Task SetValueAsync(bool value, CancellationToken cancellationToken) =>
        monitoringFeature is { } monitoring
            ? state.SetMonitoringEnabledAsync(monitoring, value, cancellationToken)
            : state.SetPreferenceAsync(preference!.Value, value, cancellationToken);

    private ActionError Error(
        ActionExecutionContext context,
        string code,
        ActionExecutionStage stage) =>
        new(
            code,
            "A kernctl preference could not be verified.",
            "The in-process preference state did not match the requested value.",
            Descriptor.Id,
            context.TransactionId,
            stage,
            RetryPossible: true,
            RollbackPossible: true);

    private static string OnOff(bool value) => value ? "On" : "Off";

    private static string StateSlug(bool value) => value ? "on" : "off";

    private static string Friendly(MonitoringFeature feature) => feature switch
    {
        MonitoringFeature.Fps => "FPS",
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };

    private static string Friendly(KernctlPreference preference) => preference switch
    {
        KernctlPreference.PerformanceMode => "Performance mode",
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, null),
    };

    private static string ToSlug(MonitoringFeature feature) => feature switch
    {
        MonitoringFeature.Fps => "fps",
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };

    private static string ToSlug(KernctlPreference preference) => preference switch
    {
        KernctlPreference.PerformanceMode => "performance-mode",
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, null),
    };

    private sealed record FeatureSnapshot(bool Enabled);
}

public static class PowerSchemeIds
{
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static Guid For(KnownPowerScheme scheme) => scheme switch
    {
        KnownPowerScheme.PowerSaver => PowerSaver,
        KnownPowerScheme.Balanced => Balanced,
        KnownPowerScheme.HighPerformance => HighPerformance,
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, null),
    };
}
