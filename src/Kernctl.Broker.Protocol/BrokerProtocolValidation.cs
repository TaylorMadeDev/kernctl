using System.Text.Json;

namespace Kernctl.Broker.Protocol;

public static class BrokerProtocolValidation
{
    public static BrokerStructuredError? ValidateHandshake(
        BrokerHandshakeRequest request,
        DateTimeOffset nowUtc)
    {
        if (request.ProtocolVersion != BrokerProtocol.CurrentVersion)
        {
            return Error(
                BrokerErrorCodes.IncompatibleVersion,
                "The application and administrator broker use incompatible protocol versions.");
        }

        if (request.SessionId == Guid.Empty
            || request.Client.ProcessId <= 0
            || request.Client.ProcessStartUtcTicks <= 0
            || request.Client.WindowsSessionId < 0
            || !IsBounded(request.ApplicationVersion, 64)
            || !IsBounded(request.RequestedCapability, 64)
            || !IsBounded(request.Client.ExecutablePath, BrokerProtocol.MaximumStringLength)
            || !IsBounded(request.Client.UserSid, 184))
        {
            return Error(
                BrokerErrorCodes.InvalidHandshake,
                "The administrator request contains invalid session metadata.");
        }

        if (!IsUtc(request.ExpiresAtUtc)
            || request.ExpiresAtUtc <= nowUtc
            || request.ExpiresAtUtc > nowUtc + BrokerProtocol.MaximumSessionLifetime)
        {
            return Error(
                BrokerErrorCodes.SessionExpired,
                "The administrator request has expired.");
        }

        return null;
    }

    public static BrokerStructuredError? ValidateRequest(
        BrokerRequestEnvelope request,
        Guid establishedSessionId,
        DateTimeOffset nowUtc)
    {
        if (request.ProtocolVersion != BrokerProtocol.CurrentVersion)
        {
            return Error(
                BrokerErrorCodes.IncompatibleVersion,
                "The request uses an incompatible broker protocol version.");
        }

        if (request.SessionId == Guid.Empty || request.SessionId != establishedSessionId)
        {
            return Error(
                BrokerErrorCodes.SessionMismatch,
                "The request does not belong to this administrator session.");
        }

        if (request.RequestId == Guid.Empty
            || request.PayloadVersion <= 0
            || !IsOperationId(request.OperationId)
            || request.Payload.ValueKind != JsonValueKind.Object)
        {
            return Error(
                BrokerErrorCodes.InvalidRequest,
                "The administrator request is malformed.");
        }

        if (!IsUtc(request.CreatedAtUtc)
            || !IsUtc(request.ExpiresAtUtc)
            || request.CreatedAtUtc > nowUtc + BrokerProtocol.MaximumClockSkew
            || request.ExpiresAtUtc <= nowUtc
            || request.ExpiresAtUtc <= request.CreatedAtUtc
            || request.ExpiresAtUtc > request.CreatedAtUtc + BrokerProtocol.MaximumSessionLifetime)
        {
            return Error(
                BrokerErrorCodes.RequestExpired,
                "The administrator request has expired.");
        }

        return null;
    }

    public static bool IsOperationId(string value) =>
        IsBounded(value, BrokerProtocol.MaximumOperationIdLength)
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    public static bool IsBounded(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static BrokerStructuredError Error(string code, string message) =>
        new(code, message, RetryPossible: false);
}
