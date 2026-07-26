using System.Text.Json;
using Kernctl.Core.Actions;

namespace Kernctl.Core.Gaming;

public sealed record GameProcessReference(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath);

public sealed record GameProcessMetrics(
    DateTimeOffset CapturedAtUtc,
    TimeSpan Duration,
    double CpuPercent,
    long WorkingSetBytes,
    GameProcessPriority Priority,
    int ProcessCount);

public sealed record GameProcessOperationResult(bool Succeeded, string Summary)
{
    public static GameProcessOperationResult Success(string summary) => new(true, summary);

    public static GameProcessOperationResult Failure(string summary) => new(false, summary);
}

public sealed record GameProcessTreeResult(
    TimeSpan Duration,
    long PeakWorkingSetBytes,
    double AverageCpuPercent,
    string Summary);

public interface IGameProcessService
{
    Task<GameProcessReference?> FindRunningAsync(
        string executablePath,
        CancellationToken cancellationToken = default);

    Task<GameProcessReference> LaunchAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    Task<bool> IsRunningAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default);

    Task<GameProcessPriority?> GetPriorityAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default);

    Task<GameProcessOperationResult> SetPriorityAsync(
        GameProcessReference process,
        GameProcessPriority priority,
        CancellationToken cancellationToken = default);

    Task<GameProcessOperationResult> RequestCloseAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default);
}

public interface IGameProcessTreeMonitor
{
    Task<GameProcessTreeResult> MonitorAsync(
        GameProcessReference root,
        Action<GameProcessMetrics>? metricsChanged = null,
        CancellationToken cancellationToken = default);
}

public interface IFpsProvider
{
    bool IsAvailable { get; }

    string Status { get; }

    ValueTask<double?> TryGetFramesPerSecondAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableFpsProvider : IFpsProvider
{
    public bool IsAvailable => false;

    public string Status => "FPS provider unavailable.";

    public ValueTask<double?> TryGetFramesPerSecondAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<double?>(null);
    }
}

public sealed record GamePriorityTarget(GameProcessReference Process, GameProcessPriority Desired);

public interface IGamePriorityTargetContext
{
    GamePriorityTarget? Current { get; }

    IDisposable BeginSelection(GameProcessReference process, GameProcessPriority priority);
}

public sealed class GamePriorityTargetContext : IGamePriorityTargetContext
{
    private readonly object sync = new();
    private GamePriorityTarget? current;

    public GamePriorityTarget? Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    public IDisposable BeginSelection(GameProcessReference process, GameProcessPriority priority)
    {
        if (!GameValidation.IsAllowedPriority(priority))
        {
            throw new InvalidDataException("Realtime and unknown process priorities are not allowed.");
        }

        lock (sync)
        {
            if (current is not null)
            {
                throw new InvalidOperationException("Another game priority plan is already being prepared.");
            }

            current = new(process, priority);
        }

        return new Selection(this);
    }

    private void Clear()
    {
        lock (sync)
        {
            current = null;
        }
    }

    private sealed class Selection(GamePriorityTargetContext owner) : IDisposable
    {
        private GamePriorityTargetContext? currentOwner = owner;

        public void Dispose() => Interlocked.Exchange(ref currentOwner, null)?.Clear();
    }
}

public static class GamePriorityActionIds
{
    public static string For(GameProcessPriority priority) =>
        priority switch
        {
            GameProcessPriority.Normal => "game.process-priority.normal",
            GameProcessPriority.AboveNormal => "game.process-priority.above-normal",
            GameProcessPriority.High => "game.process-priority.high",
            _ => throw new InvalidDataException("Realtime and unknown process priorities are not allowed."),
        };
}

public sealed class GameProcessPrioritySystemAction(
    IGameProcessService processService,
    IGamePriorityTargetContext targetContext,
    GameProcessPriority desiredPriority) : ISystemAction
{
    private const int SchemaVersion = 1;
    private readonly string actionId = GamePriorityActionIds.For(desiredPriority);

    public ActionDescriptor Descriptor { get; } = new(
        GamePriorityActionIds.For(desiredPriority),
        SchemaVersion,
        $"{desiredPriority} game process priority",
        "Adjusts one validated game process and restores its original priority.",
        "kernctl changes only the process selected by the active game session. Realtime priority is never offered.",
        SystemActionCategory.Performance,
        desiredPriority == GameProcessPriority.High ? ActionRiskLevel.Moderate : ActionRiskLevel.Low,
        ActionPrivilegeLevel.StandardUser,
        ActionRestartRequirement.None,
        [ActionPlatform.Windows],
        SupportsRollback: true,
        IsAvailable: true,
        EstimatedDuration: TimeSpan.FromSeconds(1));

    public async Task<ActionDetectionResult> DetectAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var target = targetContext.Current;
        if (target is null || target.Desired != desiredPriority)
        {
            return ActionDetectionResult.Unavailable(
                "No selected game process",
                "No validated game process is selected.",
                "Priority actions can only be planned by an active kernctl game session.");
        }

        var current = await processService.GetPriorityAsync(target.Process, cancellationToken);
        return current is null
            ? ActionDetectionResult.Unavailable(
                "Unavailable",
                "The process exited or access was denied.",
                "The selected game process cannot be inspected.")
            : ActionDetectionResult.Available(
                current.Value.ToString(),
                $"The game currently uses {current.Value} priority.");
    }

    public Task<ActionPlan> PlanAsync(
        ActionExecutionContext context,
        ActionDetectionResult detection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = targetContext.Current
            ?? throw new InvalidOperationException("No game priority target is selected.");
        var resource = FormatResource(target.Process);
        return Task.FromResult(new ActionPlan(
            actionId,
            SchemaVersion,
            detection.CurrentState,
            desiredPriority.ToString(),
            [new("Set process priority", $"Set this game process to {desiredPriority}.")],
            [resource],
            Descriptor.RiskLevel,
            Descriptor.RequiredPrivilege,
            Descriptor.RestartRequirement,
            SupportsRollback: true,
            desiredPriority == GameProcessPriority.High
                ? ["High priority can reduce responsiveness for other applications. Realtime is never used."]
                : [],
            [],
            $"Change only the configured game process to {desiredPriority} priority, then restore its previous value when the session ends."));
    }

    public async Task<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        if (!TryParseResource(plan, out var process)
            || !GameValidation.IsAllowedPriority(desiredPriority))
        {
            return ActionValidationResult.Invalid(
                new ActionValidationIssue(
                    "game.priority.target-invalid",
                    "The selected process identity is invalid."));
        }

        return await processService.IsRunningAsync(process!, cancellationToken)
            ? ActionValidationResult.Valid
            : ActionValidationResult.Invalid(
                new ActionValidationIssue(
                    "game.priority.process-exited",
                    "The selected game process has exited."));
    }

    public async Task<ActionStatePayload> CaptureStateAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        var process = ParseRequired(plan);
        var priority = await processService.GetPriorityAsync(process, cancellationToken)
            ?? throw new InvalidOperationException("The game process priority could not be captured.");
        return ActionStatePayload.From(SchemaVersion, new PrioritySnapshot(process, priority));
    }

    public async Task<ActionApplyResult> ApplyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        var result = await processService.SetPriorityAsync(
            ParseRequired(plan),
            desiredPriority,
            cancellationToken);
        return result.Succeeded
            ? ActionApplyResult.Success(result.Summary)
            : ActionApplyResult.Failure(
                result.Summary,
                Error(context, ActionExecutionStage.Apply, result.Summary),
                mayHaveMutated: false);
    }

    public async Task<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        CancellationToken cancellationToken)
    {
        var current = await processService.GetPriorityAsync(ParseRequired(plan), cancellationToken);
        return current == desiredPriority
            ? ActionVerificationResult.Success($"Verified {desiredPriority} process priority.")
            : ActionVerificationResult.Failure(
                "The process priority did not match the requested value.",
                Error(
                    context,
                    ActionExecutionStage.Verification,
                    "The process exited, access was denied, or Windows rejected the priority."));
    }

    public async Task<ActionRollbackResult> RollbackAsync(
        ActionExecutionContext context,
        ActionPlan plan,
        ActionStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var state = snapshot.OriginalState.Deserialize<PrioritySnapshot>(ActionJson.Options)
            ?? throw new InvalidDataException("The game priority snapshot is invalid.");
        if (!await processService.IsRunningAsync(state.Process, cancellationToken))
        {
            return ActionRollbackResult.Success(
                "The game process already exited; Windows discarded its process priority.");
        }

        var result = await processService.SetPriorityAsync(
            state.Process,
            state.Priority,
            cancellationToken);
        return result.Succeeded
            ? ActionRollbackResult.Success(result.Summary)
            : ActionRollbackResult.Failure(
                result.Summary,
                Error(context, ActionExecutionStage.Rollback, result.Summary));
    }

    private ActionError Error(
        ActionExecutionContext context,
        ActionExecutionStage stage,
        string diagnostic) =>
        new(
            "game.priority.operation-failed",
            "kernctl could not change the configured game process priority.",
            diagnostic,
            actionId,
            context.TransactionId,
            stage,
            RetryPossible: true,
            RollbackPossible: true);

    private static string FormatResource(GameProcessReference process) =>
        $"game-process:{process.ProcessId}:{process.StartedAtUtc.UtcTicks}";

    private static bool TryParseResource(ActionPlan plan, out GameProcessReference? process)
    {
        process = null;
        var parts = plan.AffectedResources.SingleOrDefault()?.Split(':');
        if (parts is not ["game-process", var processIdText, var ticksText]
            || !int.TryParse(processIdText, out var processId)
            || processId <= 0
            || !long.TryParse(ticksText, out var ticks)
            || ticks <= 0)
        {
            return false;
        }

        process = new(processId, new DateTimeOffset(ticks, TimeSpan.Zero), string.Empty);
        return true;
    }

    private static GameProcessReference ParseRequired(ActionPlan plan) =>
        TryParseResource(plan, out var process)
            ? process!
            : throw new InvalidDataException("The game process resource is invalid.");

    private sealed record PrioritySnapshot(
        GameProcessReference Process,
        GameProcessPriority Priority);
}
