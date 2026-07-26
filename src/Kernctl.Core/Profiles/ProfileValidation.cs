using System.Collections.Immutable;

namespace Kernctl.Core.Profiles;

public static class ProfileValidation
{
    public static ProfileValidationResult Validate(SystemProfile? profile)
    {
        var issues = ImmutableArray.CreateBuilder<ProfileValidationIssue>();
        if (profile is null)
        {
            return new(false, [new("profile.required", "A profile is required.")]);
        }

        if (profile.SchemaVersion != SystemProfile.CurrentSchemaVersion)
        {
            issues.Add(new("profile.schema", "The profile schema version is not supported."));
        }

        if (!IsSafeId(profile.Id))
        {
            issues.Add(new("profile.id", "Profile IDs may contain lowercase letters, digits, and hyphens only."));
        }

        ValidateText(profile.Name, 1, 64, "profile.name", "Profile name", issues);
        ValidateText(profile.Description, 1, 240, "profile.description", "Profile description", issues);

        var duplicateTargets = profile.OrderedActions
            .GroupBy(action => action.TargetKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateTargets)
        {
            issues.Add(new(
                "action.conflict",
                $"More than one action targets '{duplicate.Key}'. Resolve the conflict before saving."));
        }

        var duplicateIds = profile.OrderedActions
            .GroupBy(action => action.Id)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateIds)
        {
            issues.Add(new("action.id.duplicate", "Action definition IDs must be unique.", duplicate.Key));
        }

        foreach (var action in profile.OrderedActions)
        {
            ValidateAction(action, issues);
        }

        if (profile.TriggerConfiguration.Cooldown < TimeSpan.FromSeconds(5)
            || profile.TriggerConfiguration.Cooldown > TimeSpan.FromHours(1))
        {
            issues.Add(new("trigger.cooldown", "Automatic switching cooldown must be between 5 seconds and 1 hour."));
        }

        foreach (var trigger in profile.TriggerConfiguration.Triggers)
        {
            if (trigger.Priority is < 0 or > 100)
            {
                issues.Add(new("trigger.priority", "Trigger priority must be between 0 and 100."));
            }

            if (trigger.Kind is ProfileTriggerKind.GameStarted or ProfileTriggerKind.GameExited)
            {
                if (string.IsNullOrWhiteSpace(trigger.SelectedExecutablePath)
                    || !Path.IsPathFullyQualified(trigger.SelectedExecutablePath))
                {
                    issues.Add(new("trigger.executable", "Game triggers require a user-selected executable."));
                }
            }
            else if (!string.IsNullOrWhiteSpace(trigger.SelectedExecutablePath))
            {
                issues.Add(new("trigger.executable.unexpected", "Only game triggers may contain an executable path."));
            }
        }

        var triggerConflicts = profile.TriggerConfiguration.Triggers
            .GroupBy(
                trigger => (trigger.Kind, NormalizePath(trigger.SelectedExecutablePath)),
                EqualityComparer<(ProfileTriggerKind, string?)>.Default)
            .Where(group => group.Count() > 1);
        foreach (var conflict in triggerConflicts)
        {
            issues.Add(new("trigger.conflict", $"Duplicate {conflict.Key.Item1} triggers must be resolved."));
        }

        return new(issues.Count == 0, issues.ToImmutable());
    }

    public static string SanitizeFileName(string id)
    {
        var safe = new string(id
            .ToLowerInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            .Take(80)
            .ToArray());
        return string.IsNullOrEmpty(safe) ? $"profile-{Guid.NewGuid():N}" : safe;
    }

    public static bool IsSafeExportFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && Path.GetInvalidFileNameChars().All(character => !fileName.Contains(character))
        && fileName == Path.GetFileName(fileName);

    private static void ValidateAction(
        ProfileActionDefinition action,
        ImmutableArray<ProfileValidationIssue>.Builder issues)
    {
        if (action.Id == Guid.Empty)
        {
            issues.Add(new("action.id", "Every action needs an ID."));
        }

        if (string.IsNullOrWhiteSpace(action.TargetKey) || action.TargetKey.Length > 120)
        {
            issues.Add(new("action.target", "Every action needs a bounded target key.", action.Id));
        }

        var isValid = action.Kind switch
        {
            ProfileActionKind.PowerScheme =>
                action.PowerScheme is not null
                && action.MonitoringFeature is null
                && action.Preference is null
                && action.Enabled is null,
            ProfileActionKind.Monitoring =>
                action.PowerScheme is null
                && action.MonitoringFeature is not null
                && action.Preference is null
                && action.Enabled is not null,
            ProfileActionKind.KernctlPreference =>
                action.PowerScheme is null
                && action.MonitoringFeature is null
                && action.Preference is not null
                && action.Enabled is not null,
            _ => false,
        };
        if (!isValid)
        {
            issues.Add(new(
                "action.value",
                $"The strongly typed value for {action.Kind} is missing or contains unrelated fields.",
                action.Id));
        }
    }

    private static void ValidateText(
        string? value,
        int minimum,
        int maximum,
        string code,
        string label,
        ImmutableArray<ProfileValidationIssue>.Builder issues)
    {
        var length = value?.Trim().Length ?? 0;
        if (length < minimum || length > maximum)
        {
            issues.Add(new(code, $"{label} must be between {minimum} and {maximum} characters."));
        }
    }

    private static bool IsSafeId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 80
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).ToUpperInvariant();
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return path.Trim().ToUpperInvariant();
        }
    }
}
