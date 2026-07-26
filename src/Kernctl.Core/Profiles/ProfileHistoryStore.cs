using System.Collections.Immutable;
using System.Text.Json;

namespace Kernctl.Core.Profiles;

public interface IProfileHistoryStore
{
    Task<IReadOnlyList<ProfileActivationHistoryEntry>> ReadAsync(
        CancellationToken cancellationToken = default);

    Task AppendAsync(
        ProfileActivationHistoryEntry entry,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class ProfileHistoryStore(string rootDirectory, int retentionLimit = 200)
    : IProfileHistoryStore, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string historyPath = Path.Combine(
        Path.GetFullPath(rootDirectory),
        "profile-history.json");
    private readonly int retentionLimit = retentionLimit > 0
        ? retentionLimit
        : throw new ArgumentOutOfRangeException(nameof(retentionLimit));
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<ProfileActivationHistoryEntry>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AppendAsync(
        ProfileActivationHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        var sanitized = entry with
        {
            Trigger = SanitizeTrigger(entry.Trigger),
            RollbackStatus = SanitizeText(entry.RollbackStatus, 120),
        };
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadUnsafeAsync(cancellationToken);
            var entries = current
                .Append(sanitized)
                .OrderByDescending(item => item.StartedAtUtc)
                .Take(retentionLimit)
                .ToImmutableArray();
            var document = new HistoryDocument
            {
                Entries = entries,
            };
            await ProfileStore.WriteAtomicAsync(
                historyPath,
                JsonSerializer.Serialize(document, ProfileJson.Options),
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(historyPath))
            {
                File.Delete(historyPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private async Task<IReadOnlyList<ProfileActivationHistoryEntry>> ReadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(historyPath))
        {
            return [];
        }

        var information = new FileInfo(historyPath);
        if (information.Length > 1024 * 1024)
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(historyPath, cancellationToken);
            var document = JsonSerializer.Deserialize<HistoryDocument>(json, ProfileJson.Options);
            return document?.SchemaVersion == SchemaVersion
                ? document.Entries
                : [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static string SanitizeTrigger(string trigger)
    {
        var safe = SanitizeText(trigger, 80);
        return safe.Contains('\\')
            || safe.Contains('/')
            || safe.Contains("--", StringComparison.Ordinal)
            ? "Automatic trigger"
            : safe;
    }

    private static string SanitizeText(string? value, int maximumLength)
    {
        var safe = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "Not recorded" : safe;
    }

    private sealed record HistoryDocument
    {
        public int SchemaVersion { get; init; } = ProfileHistoryStore.SchemaVersion;

        public ImmutableArray<ProfileActivationHistoryEntry> Entries { get; init; } = [];
    }
}
