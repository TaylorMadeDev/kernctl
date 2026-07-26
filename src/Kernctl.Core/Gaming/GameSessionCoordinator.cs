using Kernctl.Core.Actions;
using Kernctl.Core.Profiles;

namespace Kernctl.Core.Gaming;

public interface IGameSessionCoordinator
{
    IReadOnlyList<GameSession> ActiveSessions { get; }

    event EventHandler<GameProcessMetrics>? MetricsChanged;

    event EventHandler? SessionsChanged;

    Task<GameSession> LaunchAndMonitorAsync(
        Guid gameId,
        CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public sealed class GameSessionCoordinator(
    IGameLibraryService library,
    IGameProcessService processService,
    IGameProcessTreeMonitor processTreeMonitor,
    IProfileCatalogService profileCatalog,
    IProfileEngine profileEngine,
    IActionTransactionEngine actionEngine,
    IGamePriorityTargetContext priorityTargetContext) : IGameSessionCoordinator, IDisposable
{
    private readonly List<GameSession> activeSessions = [];
    private readonly List<TaskCompletionSource> activeOperations = [];
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private readonly CancellationTokenSource shutdownCancellation = new();

    public IReadOnlyList<GameSession> ActiveSessions
    {
        get
        {
            lock (activeSessions)
            {
                return activeSessions.ToArray();
            }
        }
    }

    public event EventHandler<GameProcessMetrics>? MetricsChanged;

    public event EventHandler? SessionsChanged;

    public async Task<GameSession> LaunchAndMonitorAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (activeOperations)
        {
            activeOperations.Add(completion);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            shutdownCancellation.Token);
        try
        {
            return await LaunchAndMonitorCoreAsync(gameId, linkedCancellation.Token);
        }
        finally
        {
            lock (activeOperations)
            {
                activeOperations.Remove(completion);
            }

            completion.TrySetResult();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        shutdownCancellation.Cancel();
        Task[] pending;
        lock (activeOperations)
        {
            pending = activeOperations.Select(completion => completion.Task).ToArray();
        }

        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken);
        }
    }

    private async Task<GameSession> LaunchAndMonitorCoreAsync(
        Guid gameId,
        CancellationToken cancellationToken)
    {
        await launchGate.WaitAsync(cancellationToken);
        GameDefinition game;
        GameProcessReference? process = null;
        ProfileApplicationResult? profileApplication = null;
        Guid? priorityTransactionId = null;
        var previousProfileId = profileCatalog.ActiveProfile.Id;
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var outcome = GameSessionOutcome.Completed;
        var summary = "The game session completed and temporary settings were restored.";
        string? priorityWarning = null;
        GameProcessTreeResult? metrics = null;
        try
        {
            game = library.GetRequired(gameId);
            var validation = GameValidation.ValidateLaunch(
                game.Installation.ExecutablePath,
                game.Launch.WorkingDirectory,
                game.Launch.Arguments);
            if (!validation.IsValid)
            {
                throw new GameLaunchException(validation.Errors[0]);
            }

            process = await processService.FindRunningAsync(
                validation.NormalizedExecutablePath!,
                cancellationToken);

            if (library.Preferences.AutomaticProfilesEnabled
                && game.Launch.AutoApplyProfile
                && !string.IsNullOrWhiteSpace(game.Launch.ProfileId))
            {
                var profile = profileCatalog.GetRequired(game.Launch.ProfileId);
                var plan = await profileEngine.BuildPlanAsync(profile, cancellationToken);
                if (!plan.CanApply)
                {
                    outcome = GameSessionOutcome.ProfileApplyFailed;
                    throw new GameLaunchException(
                        "The assigned profile could not be applied safely, so the game was not launched.");
                }

                profileApplication = await profileEngine.ApplyAsync(
                    plan,
                    $"Game started: {game.Name}",
                    cancellationToken);
                if (profileApplication.Outcome != ProfileOutcome.Succeeded)
                {
                    outcome = GameSessionOutcome.ProfileApplyFailed;
                    throw new GameLaunchException(
                        "The assigned profile did not commit, so the game was not launched.");
                }
            }

            process ??= await processService.LaunchAsync(
                validation.NormalizedExecutablePath!,
                validation.NormalizedWorkingDirectory!,
                game.Launch.Arguments,
                cancellationToken);

            var active = new GameSession(
                sessionId,
                game.Id,
                game.Name,
                process.ProcessId,
                startedAt,
                startedAt,
                TimeSpan.Zero,
                GameSessionOutcome.Completed,
                "Session active.",
                game.Launch.ProfileId,
                game.Launch.Priority,
                null,
                null);
            lock (activeSessions)
            {
                activeSessions.Add(active);
            }

            SessionsChanged?.Invoke(this, EventArgs.Empty);

            using (priorityTargetContext.BeginSelection(process, game.Launch.Priority))
            {
                var priorityPlan = await actionEngine.PlanAsync(
                    new([GamePriorityActionIds.For(game.Launch.Priority)]),
                    cancellationToken);
                if (priorityPlan.CanExecute)
                {
                    var priorityResult = await actionEngine.ExecuteAsync(priorityPlan, cancellationToken);
                    if (priorityResult.Succeeded)
                    {
                        priorityTransactionId = priorityResult.TransactionId;
                    }
                    else
                    {
                        priorityWarning = priorityResult.Errors.FirstOrDefault()?.UserMessage
                            ?? "the configured process priority could not be applied";
                    }
                }
                else
                {
                    priorityWarning = priorityPlan.Actions
                        .SelectMany(action => action.Validation.Issues.Select(issue => issue.Message)
                            .Concat(action.Plan.UnavailableReasons))
                        .FirstOrDefault()
                        ?? "the configured process priority is unavailable";
                }
            }

            metrics = await processTreeMonitor.MonitorAsync(
                process,
                value => MetricsChanged?.Invoke(this, value),
                cancellationToken);
            if (priorityWarning is not null)
            {
                summary =
                    $"The game session completed, but {priorityWarning.TrimEnd('.')}. Other temporary settings were restored.";
            }
        }
        catch (OperationCanceledException)
        {
            outcome = GameSessionOutcome.Cancelled;
            summary = "The game session was cancelled; kernctl attempted to restore temporary settings.";
        }
        catch (GameLaunchException exception)
        {
            if (outcome != GameSessionOutcome.ProfileApplyFailed)
            {
                outcome = GameSessionOutcome.LaunchFailed;
            }

            summary = exception.Message;
        }
        catch (Exception)
        {
            outcome = process is null
                ? GameSessionOutcome.LaunchFailed
                : GameSessionOutcome.MonitoringFailed;
            summary = process is null
                ? "The game could not be launched."
                : "The game ended after process monitoring became unavailable.";
        }
        finally
        {
            var restoreFailed = false;
            if (priorityTransactionId is not null)
            {
                try
                {
                    var result = await actionEngine.RollbackAsync(
                        priorityTransactionId.Value,
                        CancellationToken.None);
                    restoreFailed |= result.FinalState is not TransactionState.RolledBack;
                }
                catch (Exception exception) when (
                    exception is ActionEngineException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    restoreFailed = true;
                }
            }

            if (profileApplication?.TransactionId is not null
                && (library.GetRequired(gameId).Launch.RestorePreviousProfileOnExit
                    || outcome != GameSessionOutcome.Completed))
            {
                var profileRestored = false;
                try
                {
                    var result = await profileEngine.RestoreAsync(
                        profileApplication.TransactionId.Value,
                        profileApplication.ProfileId,
                        profileApplication.ProfileName,
                        CancellationToken.None);
                    profileRestored = result.Outcome == ProfileOutcome.RolledBack;
                    restoreFailed |= !profileRestored;
                }
                catch (Exception exception) when (
                    exception is ProfileBusyException
                        or ActionEngineException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    restoreFailed = true;
                }

                if (profileRestored)
                {
                    try
                    {
                        await profileCatalog.SetActiveAsync(
                            previousProfileId,
                            CancellationToken.None);
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or KeyNotFoundException)
                    {
                        restoreFailed = true;
                    }
                }
            }

            if (restoreFailed)
            {
                outcome = GameSessionOutcome.RestoreFailed;
                summary = "The game ended, but one or more temporary settings could not be restored. Review transaction recovery.";
            }

            lock (activeSessions)
            {
                activeSessions.RemoveAll(session => session.Id == sessionId);
            }

            SessionsChanged?.Invoke(this, EventArgs.Empty);
            launchGate.Release();
        }

        var endedAt = DateTimeOffset.UtcNow;
        var gameName = library.GetRequired(gameId).Name;
        var completed = new GameSession(
            sessionId,
            gameId,
            gameName,
            ProcessId: null,
            startedAt,
            endedAt,
            metrics?.Duration ?? endedAt - startedAt,
            outcome,
            summary,
            library.GetRequired(gameId).Launch.ProfileId,
            library.GetRequired(gameId).Launch.Priority,
            metrics?.PeakWorkingSetBytes,
            metrics?.AverageCpuPercent);
        await library.RecordSessionAsync(completed, CancellationToken.None);
        return completed;
    }

    public void Dispose()
    {
        shutdownCancellation.Cancel();
        lock (activeOperations)
        {
            if (activeOperations.Count == 0)
            {
                launchGate.Dispose();
                shutdownCancellation.Dispose();
            }
        }
    }
}

public sealed class GameLaunchException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
