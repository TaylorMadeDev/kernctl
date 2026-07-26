namespace Kernctl.Core.Profiles;

public interface IAutomaticProfileSwitcher
{
    ProfileTriggerDecision Evaluate(
        ProfileTriggerEvent triggerEvent,
        IReadOnlyList<SystemProfile> profiles,
        string activeProfileId);

    void RecordActivation(
        string activatedProfileId,
        string previousProfileId,
        ProfileTriggerDefinition trigger,
        DateTimeOffset activatedAtUtc);
}

public sealed class AutomaticProfileSwitcher : IAutomaticProfileSwitcher
{
    private readonly Dictionary<string, DateTimeOffset> lastActivationByProfile =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TemporaryActivation> temporaryActivations =
        new(StringComparer.OrdinalIgnoreCase);

    public ProfileTriggerDecision Evaluate(
        ProfileTriggerEvent triggerEvent,
        IReadOnlyList<SystemProfile> profiles,
        string activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(triggerEvent);
        ArgumentNullException.ThrowIfNull(profiles);

        if (triggerEvent.Kind == ProfileTriggerKind.GameExited
            && !string.IsNullOrWhiteSpace(triggerEvent.ExecutablePath)
            && temporaryActivations.TryGetValue(
                NormalizePath(triggerEvent.ExecutablePath),
                out var temporary))
        {
            temporaryActivations.Remove(NormalizePath(triggerEvent.ExecutablePath));
            return new(
                ShouldActivate: temporary.PreviousProfileId != activeProfileId,
                temporary.PreviousProfileId,
                "The temporary game profile ended; restore the previous profile.",
                ShouldRestorePreviousProfile: true);
        }

        var matches = profiles
            .Where(profile =>
                profile.TriggerConfiguration is
                {
                    IsEnabled: true,
                    AutomaticBehaviourApproved: true,
                })
            .SelectMany(profile => profile.TriggerConfiguration.Triggers.Select(trigger => (profile, trigger)))
            .Where(candidate => Matches(candidate.trigger, triggerEvent))
            .OrderByDescending(candidate => candidate.trigger.Priority)
            .ThenBy(candidate => candidate.profile.Id, StringComparer.Ordinal)
            .ToArray();
        if (matches.Length == 0)
        {
            return new(false, null, "No approved automatic profile matches this event.");
        }

        var winner = matches[0];
        if (matches.Length > 1 && matches[1].trigger.Priority == winner.trigger.Priority)
        {
            return new(
                false,
                null,
                "Automatic trigger conflict: multiple profiles have the same highest priority.");
        }

        if (lastActivationByProfile.TryGetValue(winner.profile.Id, out var lastActivation)
            && triggerEvent.OccurredAtUtc - lastActivation < winner.profile.TriggerConfiguration.Cooldown)
        {
            return new(false, null, "The matching profile is inside its cooldown period.");
        }

        if (winner.profile.Id == activeProfileId)
        {
            return new(false, null, "The matching profile is already active.");
        }

        return new(true, winner.profile.Id, $"Approved {winner.trigger.Kind} trigger matched.");
    }

    public void RecordActivation(
        string activatedProfileId,
        string previousProfileId,
        ProfileTriggerDefinition trigger,
        DateTimeOffset activatedAtUtc)
    {
        lastActivationByProfile[activatedProfileId] = activatedAtUtc;
        if (trigger.Kind == ProfileTriggerKind.GameStarted
            && trigger.RestorePreviousProfileOnExit
            && !string.IsNullOrWhiteSpace(trigger.SelectedExecutablePath))
        {
            temporaryActivations[NormalizePath(trigger.SelectedExecutablePath)] =
                new(previousProfileId);
        }
    }

    private static bool Matches(
        ProfileTriggerDefinition configured,
        ProfileTriggerEvent observed)
    {
        if (configured.Kind != observed.Kind)
        {
            return false;
        }

        if (configured.Kind is not (ProfileTriggerKind.GameStarted or ProfileTriggerKind.GameExited))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(configured.SelectedExecutablePath)
            && !string.IsNullOrWhiteSpace(observed.ExecutablePath)
            && string.Equals(
                NormalizePath(configured.SelectedExecutablePath),
                NormalizePath(observed.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private sealed record TemporaryActivation(string PreviousProfileId);
}
