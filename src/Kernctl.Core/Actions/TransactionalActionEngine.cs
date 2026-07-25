namespace Kernctl.Core.Actions;

/// <summary>Executes action lifecycles with best-effort transactional rollback.</summary>
public sealed class TransactionalActionEngine : ITransactionalActionEngine
{
    public async ValueTask<ActionResult> ApplyAsync(
        IReadOnlyList<ISystemAction> actions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var applied = new Stack<ISystemAction>();
        var restartRequired = false;

        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detection = await action.DetectAsync(cancellationToken);
            if (!detection.IsApplicable)
            {
                continue;
            }

            _ = await action.ExplainAsync(cancellationToken);
            var apply = await action.ApplyAsync(cancellationToken);
            if (!apply.Succeeded)
            {
                return await RollBackAsync(applied, apply.Message, cancellationToken);
            }

            applied.Push(action);
            restartRequired |= apply.RestartRequired;

            var verification = await action.VerifyAsync(cancellationToken);
            if (!verification.Succeeded)
            {
                return await RollBackAsync(applied, verification.Message, cancellationToken);
            }

            restartRequired |= verification.RestartRequired;
        }

        return new ActionResult(true, "All applicable actions were verified.", restartRequired);
    }

    private static async ValueTask<ActionResult> RollBackAsync(
        Stack<ISystemAction> applied,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        while (applied.TryPop(out var action))
        {
            var undo = await action.UndoAsync(cancellationToken);
            if (!undo.Succeeded)
            {
                return new(
                    false,
                    $"{failureMessage} Rollback also failed for '{action.Id}': {undo.Message}",
                    undo.RestartRequired);
            }
        }

        return new(false, $"{failureMessage} Completed actions were rolled back.", false);
    }
}
