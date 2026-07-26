using System.Collections.Immutable;

namespace Kernctl.Core.Actions;

public sealed record TransactionJournal(
    int JournalSchemaVersion,
    Guid TransactionId,
    TransactionState State,
    bool IsDryRun,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ActionRestartRequirement RestartRequirement,
    bool RollbackAttempted,
    ImmutableArray<JournalActionEntry> Actions,
    ImmutableArray<ActionError> Errors,
    TransactionElevationRecord? Elevation = null)
{
    public const int CurrentSchemaVersion = 1;

    public static TransactionJournal Create(
        Guid transactionId,
        ActionTransactionRequest request,
        IReadOnlyList<ActionDescriptor> descriptors,
        DateTimeOffset startedAtUtc) =>
        new(
            CurrentSchemaVersion,
            transactionId,
            TransactionState.Created,
            request.IsDryRun,
            startedAtUtc,
            startedAtUtc,
            null,
            descriptors.Select(descriptor => descriptor.RestartRequirement).DefaultIfEmpty().Max(),
            false,
            [
                .. descriptors.Select((descriptor, index) => new JournalActionEntry(
                    index,
                    descriptor.Id,
                    descriptor.DisplayName,
                    descriptor.SchemaVersion,
                    ActionExecutionState.Pending,
                    null,
                    null,
                    false,
                    null)),
            ],
            [],
            null);
}

public enum TransactionElevationState
{
    Requested,
    Granted,
    Declined,
    Failed,
    Closed,
}

public sealed record TransactionElevationRecord(
    TransactionElevationState State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool IsRollback,
    string SafeOutcome);

public sealed record JournalActionEntry(
    int Order,
    string ActionId,
    string DisplayName,
    int ActionSchemaVersion,
    ActionExecutionState State,
    ActionPlan? Plan,
    ActionStateSnapshot? Snapshot,
    bool MayHaveMutated,
    ActionError? Error);

public sealed record JournalReadError(string FileName, string SafeMessage);

public sealed record JournalScanResult(
    IReadOnlyList<TransactionJournal> Journals,
    IReadOnlyList<JournalReadError> Errors);

public interface IActionJournalStore
{
    Task SaveAsync(TransactionJournal journal, CancellationToken cancellationToken = default);

    Task<TransactionJournal> LoadAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default);

    Task<JournalScanResult> ScanActiveAsync(CancellationToken cancellationToken = default);

    Task ArchiveAsync(TransactionJournal journal, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransactionJournal>> ReadArchiveAsync(
        CancellationToken cancellationToken = default);
}

public interface IActionHistoryService
{
    Task<IReadOnlyList<TransactionHistoryEntry>> ReadAsync(
        CancellationToken cancellationToken = default);
}

public sealed class ActionHistoryService(IActionJournalStore journalStore) : IActionHistoryService
{
    public async Task<IReadOnlyList<TransactionHistoryEntry>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var journals = await journalStore.ReadArchiveAsync(cancellationToken);
        return journals
            .OrderByDescending(journal => journal.CompletedAtUtc ?? journal.UpdatedAtUtc)
            .Select(journal => new TransactionHistoryEntry(
                journal.TransactionId,
                journal.StartedAtUtc,
                journal.CompletedAtUtc,
                [.. journal.Actions.OrderBy(action => action.Order).Select(action => action.DisplayName)],
                journal.State,
                journal.IsDryRun,
                journal.RollbackAttempted,
                journal.RestartRequirement,
                [
                    .. journal.Errors
                        .Select(error => error.UserMessage)
                        .Distinct(StringComparer.Ordinal),
                ]))
            .ToArray();
    }
}
