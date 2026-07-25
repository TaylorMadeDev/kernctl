namespace Kernctl.Core.Actions;

/// <summary>Describes privilege needed by a future system action.</summary>
public enum PrivilegeRequirement
{
    StandardUser,
    ElevatedBroker,
}

/// <summary>Describes the detected state before a proposed action.</summary>
public sealed record DetectionResult(bool IsApplicable, string Summary);

/// <summary>Describes a change to the user before it can be applied.</summary>
public sealed record ActionExplanation(string Summary, string Impact, string RollbackPlan);

/// <summary>Represents a structured action operation result.</summary>
public sealed record ActionResult(bool Succeeded, string Message, bool RestartRequired);

/// <summary>
/// Defines the reversible lifecycle required of every future system modification.
/// Implementations must capture sufficient state for verification and rollback.
/// </summary>
public interface ISystemAction
{
    string Id { get; }

    PrivilegeRequirement RequiredPrivilege { get; }

    ValueTask<DetectionResult> DetectAsync(CancellationToken cancellationToken);

    ValueTask<ActionExplanation> ExplainAsync(CancellationToken cancellationToken);

    ValueTask<ActionResult> ApplyAsync(CancellationToken cancellationToken);

    ValueTask<ActionResult> VerifyAsync(CancellationToken cancellationToken);

    ValueTask<ActionResult> UndoAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Applies reversible actions transactionally, undoing completed actions in reverse
/// order if a later action fails.
/// </summary>
public interface ITransactionalActionEngine
{
    ValueTask<ActionResult> ApplyAsync(
        IReadOnlyList<ISystemAction> actions,
        CancellationToken cancellationToken);
}
