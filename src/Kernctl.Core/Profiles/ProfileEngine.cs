using System.Collections.Immutable;
using Kernctl.Core.Actions;

namespace Kernctl.Core.Profiles;

public interface IProfileEngine
{
    Task<ProfileApplicationPlan> BuildPlanAsync(
        SystemProfile profile,
        CancellationToken cancellationToken = default);

    Task<ProfileApplicationResult> ApplyAsync(
        ProfileApplicationPlan plan,
        string trigger,
        CancellationToken cancellationToken = default);

    Task<ProfileApplicationResult> RestoreAsync(
        Guid transactionId,
        string profileId,
        string profileName,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileBusyException()
    : InvalidOperationException("Another profile transaction is already running.");

public sealed class ProfileEngine(
    IActionTransactionEngine actionEngine,
    IProfileCatalogService catalog,
    IProfileHistoryStore historyStore) : IProfileEngine, IDisposable
{
    private readonly SemaphoreSlim transactionGate = new(1, 1);

    public async Task<ProfileApplicationPlan> BuildPlanAsync(
        SystemProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = ProfileValidation.Validate(profile);
        if (!validation.IsValid || profile.OrderedActions.IsEmpty)
        {
            return new(
                Guid.NewGuid(),
                profile,
                DateTimeOffset.UtcNow,
                [],
                null,
                validation);
        }

        var actionIds = profile.OrderedActions.Select(ProfileActionIds.For).ToImmutableArray();
        ActionTransactionPlan transactionPlan;
        try
        {
            transactionPlan = await actionEngine.PlanAsync(
                new ActionTransactionRequest(actionIds),
                cancellationToken);
        }
        catch (ActionEngineException)
        {
            var unsupported = profile.OrderedActions
                .Select((definition, index) => new ProfileActionPlanItem(
                    definition.Id,
                    actionIds[index],
                    FriendlyName(definition),
                    "Unknown",
                    ProposedValue(definition),
                    "The requested action is not registered in this kernctl build.",
                    definition.IsRequired,
                    IsReversible: false,
                    "Unavailable",
                    ProfilePlanDisposition.Unsupported,
                    ["This action is unavailable and will never be run as an arbitrary operation."]))
                .ToImmutableArray();
            return new(
                Guid.NewGuid(),
                profile,
                DateTimeOffset.UtcNow,
                unsupported,
                null,
                validation);
        }

        var items = profile.OrderedActions
            .Select((definition, index) =>
            {
                var planned = transactionPlan.Actions[index];
                return new ProfileActionPlanItem(
                    definition.Id,
                    planned.Descriptor.Id,
                    planned.Descriptor.DisplayName,
                    planned.Plan.CurrentState,
                    planned.Plan.DesiredState,
                    planned.Plan.UserExplanation,
                    definition.IsRequired,
                    planned.Plan.SupportsRollback,
                    planned.Plan.RequiredPrivilege == ActionPrivilegeLevel.Administrator
                        ? "Administrator"
                        : "Standard user",
                    GetDisposition(definition, planned),
                    [
                        .. planned.Plan.Warnings,
                        .. planned.Plan.UnavailableReasons,
                        .. planned.Validation.Issues.Select(issue => issue.Message),
                    ]);
            })
            .ToImmutableArray();

        return new(
            Guid.NewGuid(),
            profile,
            DateTimeOffset.UtcNow,
            items,
            transactionPlan,
            validation);
    }

    public async Task<ProfileApplicationResult> ApplyAsync(
        ProfileApplicationPlan plan,
        string trigger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply || plan.TransactionPlan is null)
        {
            throw new InvalidOperationException("The profile plan cannot be applied safely.");
        }

        if (!await transactionGate.WaitAsync(0, cancellationToken))
        {
            throw new ProfileBusyException();
        }

        var activationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var transactionResult = await actionEngine.ExecuteAsync(
                plan.TransactionPlan,
                cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            var outcome = MapOutcome(transactionResult);
            if (transactionResult.Succeeded
                && transactionResult.FinalState == TransactionState.Committed)
            {
                await catalog.SetActiveAsync(plan.Profile.Id, CancellationToken.None);
            }

            var results = plan.Actions.Select(action =>
            {
                var error = transactionResult.Errors.FirstOrDefault(item => item.ActionId == action.ActionId);
                return new ProfileActionResult(
                    action.DefinitionId,
                    action.FriendlyName,
                    error is null && transactionResult.Succeeded,
                    error?.UserMessage
                    ?? (transactionResult.Succeeded
                        ? "Applied and verified."
                        : "Not completed because the transaction did not commit."));
            }).ToImmutableArray();
            var profileResult = new ProfileApplicationResult(
                activationId,
                plan.Profile.Id,
                plan.Profile.Name,
                transactionResult.TransactionId,
                outcome,
                startedAt,
                completedAt,
                results,
                transactionResult.Summary);
            await historyStore.AppendAsync(
                ToHistory(profileResult, SafeTrigger(trigger), transactionResult),
                CancellationToken.None);
            return profileResult;
        }
        finally
        {
            transactionGate.Release();
        }
    }

    public async Task<ProfileApplicationResult> RestoreAsync(
        Guid transactionId,
        string profileId,
        string profileName,
        CancellationToken cancellationToken = default)
    {
        if (!await transactionGate.WaitAsync(0, cancellationToken))
        {
            throw new ProfileBusyException();
        }

        var activationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var transactionResult = await actionEngine.RollbackAsync(transactionId, cancellationToken);
            var outcome = transactionResult.FinalState == TransactionState.RolledBack
                ? ProfileOutcome.RolledBack
                : ProfileOutcome.RollbackFailed;
            var result = new ProfileApplicationResult(
                activationId,
                profileId,
                profileName,
                transactionId,
                outcome,
                startedAt,
                DateTimeOffset.UtcNow,
                [],
                transactionResult.Summary);
            await historyStore.AppendAsync(
                ToHistory(result, "Manual restore", transactionResult),
                CancellationToken.None);
            return result;
        }
        finally
        {
            transactionGate.Release();
        }
    }

    public void Dispose() => transactionGate.Dispose();

    private static ProfilePlanDisposition GetDisposition(
        ProfileActionDefinition definition,
        PlannedAction planned)
    {
        if (!planned.Descriptor.IsAvailable
            || !planned.Detection.IsAvailable
            || !planned.Validation.IsValid
            || !planned.Plan.UnavailableReasons.IsEmpty)
        {
            return ProfilePlanDisposition.Unsupported;
        }

        if (planned.Plan.RestartRequirement != ActionRestartRequirement.None)
        {
            return ProfilePlanDisposition.RequiresRestart;
        }

        if (definition.RequiresConfirmation)
        {
            return ProfilePlanDisposition.RequiresConfirmation;
        }

        return string.Equals(
            planned.Plan.CurrentState,
            planned.Plan.DesiredState,
            StringComparison.OrdinalIgnoreCase)
            ? ProfilePlanDisposition.AlreadyConfigured
            : ProfilePlanDisposition.WillChange;
    }

    private static ProfileOutcome MapOutcome(TransactionExecutionResult result)
    {
        if (result.Succeeded && result.FinalState == TransactionState.Committed)
        {
            return ProfileOutcome.Succeeded;
        }

        return result.FinalState switch
        {
            TransactionState.RolledBack => ProfileOutcome.RolledBack,
            TransactionState.PartiallyRolledBack => ProfileOutcome.RollbackFailed,
            _ when result.Errors.Length > 0 && result.RollbackAttempted => ProfileOutcome.PartialSuccess,
            _ => ProfileOutcome.Failed,
        };
    }

    private static ProfileActivationHistoryEntry ToHistory(
        ProfileApplicationResult profileResult,
        string trigger,
        TransactionExecutionResult transactionResult) =>
        new(
            profileResult.ActivationId,
            profileResult.ProfileId,
            profileResult.ProfileName,
            trigger,
            profileResult.StartedAtUtc,
            profileResult.CompletedAtUtc,
            profileResult.Outcome,
            profileResult.Actions.Count(action => action.Succeeded),
            profileResult.Actions.Count(action => !action.Succeeded),
            transactionResult.RollbackAttempted
                ? transactionResult.FinalState.ToString()
                : "Not required",
            transactionResult.TransactionId);

    private static string SafeTrigger(string? trigger)
    {
        var safe = new string((trigger ?? "Manual")
            .Where(character => !char.IsControl(character))
            .Take(80)
            .ToArray());
        return safe.Contains('\\') || safe.Contains('/') || safe.Contains("--", StringComparison.Ordinal)
            ? "Automatic trigger"
            : safe;
    }

    private static string FriendlyName(ProfileActionDefinition definition) =>
        definition.Kind switch
        {
            ProfileActionKind.PowerScheme => "Windows power scheme",
            ProfileActionKind.Monitoring => $"{definition.MonitoringFeature} monitoring",
            ProfileActionKind.KernctlPreference => $"{definition.Preference} preference",
            _ => "Unsupported action",
        };

    private static string ProposedValue(ProfileActionDefinition definition) =>
        definition.Kind switch
        {
            ProfileActionKind.PowerScheme => definition.PowerScheme?.ToString() ?? "Unknown",
            _ => definition.Enabled is true ? "On" : "Off",
        };
}
