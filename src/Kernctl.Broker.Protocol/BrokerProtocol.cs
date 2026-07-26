using System.Collections.Immutable;
using System.Text.Json;

namespace Kernctl.Broker.Protocol;

public static class BrokerProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumStringLength = 1024;
    public const int MaximumOperationIdLength = 64;
    public const int MaximumRequestsPerSession = 8;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromMinutes(2);
}

public static class BrokerOperationIds
{
    public const string GetInfo = "broker.get-info";
    public const string GetCapabilities = "broker.get-capabilities";
    public const string Ping = "broker.ping";
    public const string Shutdown = "broker.shutdown";

    public static ImmutableArray<string> Diagnostics { get; } =
        [GetInfo, GetCapabilities, Ping, Shutdown];
}

public static class BrokerPayload
{
    private static readonly JsonElement Empty = CreateEmpty();

    public static JsonElement EmptyObject() => Empty.Clone();

    private static JsonElement CreateEmpty()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

public static class BrokerErrorCodes
{
    public const string InvalidFrame = "BROKER_INVALID_FRAME";
    public const string InvalidHandshake = "BROKER_INVALID_HANDSHAKE";
    public const string IncompatibleVersion = "BROKER_INCOMPATIBLE_VERSION";
    public const string SessionExpired = "BROKER_SESSION_EXPIRED";
    public const string SessionMismatch = "BROKER_SESSION_MISMATCH";
    public const string ClientIdentityRejected = "BROKER_CLIENT_IDENTITY_REJECTED";
    public const string UnknownOperation = "BROKER_UNKNOWN_OPERATION";
    public const string InvalidRequest = "BROKER_INVALID_REQUEST";
    public const string DuplicateRequest = "BROKER_DUPLICATE_REQUEST";
    public const string RequestExpired = "BROKER_REQUEST_EXPIRED";
    public const string RequestLimitExceeded = "BROKER_REQUEST_LIMIT_EXCEEDED";
    public const string OperationTimedOut = "BROKER_OPERATION_TIMED_OUT";
    public const string OperationFailed = "BROKER_OPERATION_FAILED";
    public const string ElevationCancelled = "BROKER_ELEVATION_CANCELLED";
    public const string LaunchFailed = "BROKER_LAUNCH_FAILED";
    public const string ConnectionTimedOut = "BROKER_CONNECTION_TIMED_OUT";
    public const string BrokerUnavailable = "BROKER_UNAVAILABLE";
    public const string UnsupportedPlatform = "BROKER_UNSUPPORTED_PLATFORM";
}

public enum BrokerResponseStatus
{
    Success,
    Rejected,
    Failed,
    Cancelled,
}

public enum BrokerRiskClassification
{
    Diagnostic,
    Low,
    Moderate,
    High,
}

public sealed record BrokerProcessIdentity(
    int ProcessId,
    long ProcessStartUtcTicks,
    int WindowsSessionId,
    string ExecutablePath,
    string UserSid);

public sealed record BrokerHandshakeRequest(
    int ProtocolVersion,
    string ApplicationVersion,
    Guid SessionId,
    BrokerProcessIdentity Client,
    string RequestedCapability,
    DateTimeOffset ExpiresAtUtc);

public sealed record BrokerHandshakeResponse(
    int ProtocolVersion,
    string ApplicationVersion,
    string BrokerVersion,
    Guid SessionId,
    BrokerProcessIdentity Broker,
    string GrantedCapability,
    DateTimeOffset ExpiresAtUtc,
    BrokerStructuredError? Error);

public sealed record BrokerRequestEnvelope(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId,
    string OperationId,
    int PayloadVersion,
    JsonElement Payload,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record BrokerResponseEnvelope(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId,
    string OperationId,
    BrokerResponseStatus Status,
    string SafeMessage,
    string? ErrorCode,
    JsonElement Payload,
    string BrokerVersion);

public sealed record BrokerStructuredError(
    string Code,
    string SafeMessage,
    bool RetryPossible);

public sealed record BrokerCapability(
    string OperationId,
    int PayloadVersion,
    int RequiredProtocolVersion,
    BrokerRiskClassification Risk,
    bool MutatesState,
    int MaximumExecutionMilliseconds,
    string AuditDescription);

public sealed record BrokerCapabilitiesPayload(
    int ProtocolVersion,
    ImmutableArray<BrokerCapability> Operations,
    int MaximumRequestsPerSession,
    int MaximumFrameBytes,
    int IdleTimeoutSeconds);

public sealed record BrokerInfoPayload(
    string BrokerVersion,
    int ProtocolVersion,
    int ProcessId,
    bool IsElevated,
    ImmutableArray<string> DiagnosticOperationIds,
    int IdleTimeoutSeconds);

public sealed record BrokerPingPayload(DateTimeOffset BrokerTimeUtc);

public sealed record BrokerShutdownPayload(bool ShutdownAccepted);
