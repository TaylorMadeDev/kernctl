using System.Security.Cryptography;
using System.Text.Json;

namespace Kernctl.Core.Actions;

public static class ActionSnapshotIntegrity
{
    public const int CurrentSnapshotSchemaVersion = 1;
    public const int MaximumPayloadBytes = 256 * 1024;
    private static readonly string[] ProhibitedNameFragments =
    [
        "password",
        "passwd",
        "token",
        "cookie",
        "secret",
        "credential",
        "authorization",
        "authentication",
        "$type",
        "$values",
    ];

    public static ActionStateSnapshot Create(
        Guid transactionId,
        ActionDescriptor descriptor,
        ActionStatePayload payload,
        DateTimeOffset capturedAtUtc)
    {
        if (payload.SchemaVersion <= 0)
        {
            throw new ActionEngineException("Snapshot schema version must be positive.");
        }

        var bytes = ValidateAndGetBytes(payload.OriginalState);
        return new(
            payload.SchemaVersion,
            transactionId,
            descriptor.Id,
            descriptor.SchemaVersion,
            capturedAtUtc,
            payload.OriginalState.Clone(),
            new SnapshotIntegrity("SHA-256", ComputeDigest(bytes), bytes.Length));
    }

    public static void Validate(ActionStateSnapshot snapshot)
    {
        if (snapshot.SnapshotSchemaVersion <= 0
            || snapshot.TransactionId == Guid.Empty
            || string.IsNullOrWhiteSpace(snapshot.ActionId)
            || snapshot.ActionSchemaVersion <= 0
            || snapshot.CapturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ActionEngineException("Rollback snapshot metadata is invalid.");
        }

        var bytes = ValidateAndGetBytes(snapshot.OriginalState);
        if (!string.Equals(snapshot.Integrity.Algorithm, "SHA-256", StringComparison.Ordinal)
            || snapshot.Integrity.PayloadBytes != bytes.Length
            || !string.Equals(
                snapshot.Integrity.Digest,
                ComputeDigest(bytes),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionEngineException("Rollback snapshot integrity validation failed.");
        }
    }

    private static byte[] ValidateAndGetBytes(JsonElement state)
    {
        if (state.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ActionEngineException("Rollback snapshot payload is missing.");
        }

        ValidatePropertyNames(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, ActionJson.Options);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new ActionEngineException(
                $"Rollback snapshot exceeds the {MaximumPayloadBytes / 1024} KB limit.");
        }

        return bytes;
    }

    private static void ValidatePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalizedName = property.Name.ToLowerInvariant();
                if (ProhibitedNameFragments.Any(normalizedName.Contains))
                {
                    throw new ActionEngineException(
                        $"Rollback snapshot contains prohibited field '{property.Name}'.");
                }

                ValidatePropertyNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidatePropertyNames(item);
            }
        }
    }

    private static string ComputeDigest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
