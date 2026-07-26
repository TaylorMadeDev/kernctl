using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kernctl.Core.Actions;

public sealed record ActionJournalOptions(
    string RootDirectory,
    int HistoryRetention = 100,
    int MaximumJournalBytes = 1024 * 1024);

public sealed class FileActionJournalStore : IActionJournalStore
{
    private static readonly Action<ILogger, string, Exception?> LogJournalReadFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1101, nameof(LogJournalReadFailure)),
            "Transaction journal {JournalFileName} could not be read safely.");
    private readonly ActionJournalOptions options;
    private readonly string activeDirectory;
    private readonly string archiveDirectory;
    private readonly ILogger<FileActionJournalStore> logger;

    public FileActionJournalStore(
        ActionJournalOptions options,
        ILogger<FileActionJournalStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        if (options.HistoryRetention < 1 || options.MaximumJournalBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        this.options = options with { RootDirectory = Path.GetFullPath(options.RootDirectory) };
        this.logger = logger ?? NullLogger<FileActionJournalStore>.Instance;
        activeDirectory = Path.Combine(this.options.RootDirectory, "active");
        archiveDirectory = Path.Combine(this.options.RootDirectory, "archive");
    }

    public async Task SaveAsync(
        TransactionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ValidateJournal(journal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, ActionJson.Options);
        EnsureSize(bytes.Length);
        Directory.CreateDirectory(activeDirectory);
        await WriteAtomicAsync(ActivePath(journal.TransactionId), bytes, cancellationToken);
    }

    public Task<TransactionJournal> LoadAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        var activePath = ActivePath(transactionId);
        return ReadJournalAsync(
            File.Exists(activePath) ? activePath : ArchivePath(transactionId),
            cancellationToken);
    }

    public async Task<JournalScanResult> ScanActiveAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(activeDirectory))
        {
            return new JournalScanResult([], []);
        }

        var journals = new List<TransactionJournal>();
        var errors = new List<JournalReadError>();
        foreach (var path in Directory.EnumerateFiles(
                     activeDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                journals.Add(await ReadJournalAsync(path, cancellationToken));
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or ActionEngineException)
            {
                LogJournalReadFailure(logger, Path.GetFileName(path), exception);
                errors.Add(new(
                    Path.GetFileName(path),
                    exception is ActionEngineException
                        ? exception.Message
                        : "The transaction journal could not be read safely."));
            }
        }

        return new JournalScanResult(journals, errors);
    }

    public async Task ArchiveAsync(
        TransactionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ValidateJournal(journal);
        if (!ActionStateMachine.IsTerminal(journal.State))
        {
            throw new ActionEngineException("Only terminal transaction journals can be archived.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, ActionJson.Options);
        EnsureSize(bytes.Length);
        Directory.CreateDirectory(archiveDirectory);
        await WriteAtomicAsync(ArchivePath(journal.TransactionId), bytes, cancellationToken);

        var activePath = ActivePath(journal.TransactionId);
        if (File.Exists(activePath))
        {
            File.Delete(activePath);
        }

        await EnforceRetentionAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TransactionJournal>> ReadArchiveAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(archiveDirectory))
        {
            return [];
        }

        var journals = new List<TransactionJournal>();
        foreach (var path in Directory.EnumerateFiles(
                     archiveDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                journals.Add(await ReadJournalAsync(path, cancellationToken));
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or ActionEngineException)
            {
                LogJournalReadFailure(logger, Path.GetFileName(path), exception);
            }
        }

        return journals;
    }

    private async Task<TransactionJournal> ReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new ActionEngineException("Transaction journal does not exist.");
        }

        EnsureSize(information.Length);
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        TransactionJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<TransactionJournal>(json, ActionJson.Options)
                ?? throw new ActionEngineException("Transaction journal is empty.");
        }
        catch (JsonException exception)
        {
            throw new ActionEngineException("Transaction journal contains malformed JSON.", exception);
        }

        ValidateJournal(journal);
        return journal;
    }

    private static void ValidateJournal(TransactionJournal journal)
    {
        if (journal.JournalSchemaVersion != TransactionJournal.CurrentSchemaVersion)
        {
            throw new ActionEngineException(
                $"Unsupported transaction journal schema version {journal.JournalSchemaVersion}.");
        }

        if (journal.TransactionId == Guid.Empty
            || journal.StartedAtUtc.Offset != TimeSpan.Zero
            || journal.UpdatedAtUtc.Offset != TimeSpan.Zero
            || (journal.CompletedAtUtc is { } completedAtUtc
                && completedAtUtc.Offset != TimeSpan.Zero)
            || journal.Actions.IsDefault
            || journal.Errors.IsDefault)
        {
            throw new ActionEngineException("Transaction journal metadata is invalid.");
        }

        if (journal.Elevation is { } elevation
            && (elevation.RequestedAtUtc.Offset != TimeSpan.Zero
                || (elevation.CompletedAtUtc is { } elevationCompleted
                    && (elevationCompleted.Offset != TimeSpan.Zero
                        || elevationCompleted < elevation.RequestedAtUtc))
                || string.IsNullOrWhiteSpace(elevation.SafeOutcome)
                || elevation.SafeOutcome.Length > 512
                || (elevation.State == TransactionElevationState.Requested
                    && elevation.CompletedAtUtc is not null)
                || (elevation.State != TransactionElevationState.Requested
                    && elevation.CompletedAtUtc is null)))
        {
            throw new ActionEngineException("Transaction elevation metadata is invalid.");
        }

        var ordered = journal.Actions.OrderBy(action => action.Order).ToArray();
        if (ordered.Select(action => action.Order).Where((order, index) => order != index).Any()
            || ordered.Select(action => action.ActionId).Distinct(StringComparer.Ordinal).Count()
                != ordered.Length)
        {
            throw new ActionEngineException("Transaction journal action ordering is invalid.");
        }

        foreach (var action in journal.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.ActionId)
                || action.ActionSchemaVersion <= 0)
            {
                throw new ActionEngineException("Transaction journal contains an invalid action.");
            }

            if (action.Plan is not null
                && (action.Plan.ActionId != action.ActionId
                    || action.Plan.ActionSchemaVersion != action.ActionSchemaVersion))
            {
                throw new ActionEngineException("Transaction journal contains an incompatible plan.");
            }

            if (action.State is not (ActionExecutionState.Pending or ActionExecutionState.Detected)
                && action.Plan is null)
            {
                throw new ActionEngineException("Transaction journal is missing a required action plan.");
            }

            if (action.Snapshot is not null)
            {
                ActionSnapshotIntegrity.Validate(action.Snapshot);
                if (action.Snapshot.TransactionId != journal.TransactionId
                    || action.Snapshot.ActionId != action.ActionId
                    || action.Snapshot.ActionSchemaVersion != action.ActionSchemaVersion)
                {
                    throw new ActionEngineException("Rollback snapshot ownership is invalid.");
                }
            }
        }
    }

    private async Task EnforceRetentionAsync(CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(
                archiveDirectory,
                "*.json",
                SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(options.HistoryRetention)
            .ToArray();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Delete();
        }

        await Task.CompletedTask;
    }

    private static async Task WriteAtomicAsync(
        string destination,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ActionEngineException("Journal destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                Encoding.UTF8.GetString(contents),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string ActivePath(Guid transactionId) =>
        Path.Combine(activeDirectory, $"{transactionId:N}.json");

    private string ArchivePath(Guid transactionId) =>
        Path.Combine(archiveDirectory, $"{transactionId:N}.json");

    private void EnsureSize(long size)
    {
        if (size > options.MaximumJournalBytes)
        {
            throw new ActionEngineException(
                $"Transaction journal exceeds the {options.MaximumJournalBytes / 1024} KB limit.");
        }
    }
}
