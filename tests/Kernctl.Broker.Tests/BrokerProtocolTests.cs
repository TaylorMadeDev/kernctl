using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Tests;

public sealed class BrokerProtocolTests
{
    [Fact]
    public void HandshakeRejectsIncompatibleVersionAndExpiredSession()
    {
        var now = DateTimeOffset.UtcNow;
        var incompatible = ValidHandshake(now) with { ProtocolVersion = 99 };
        var expired = ValidHandshake(now) with { ExpiresAtUtc = now.AddMinutes(-3) };

        Assert.Equal(
            BrokerErrorCodes.IncompatibleVersion,
            BrokerProtocolValidation.ValidateHandshake(incompatible, now)?.Code);
        Assert.Equal(
            BrokerErrorCodes.SessionExpired,
            BrokerProtocolValidation.ValidateHandshake(expired, now)?.Code);
    }

    [Fact]
    public void RequestValidationRejectsSessionMismatchAndUnsafeOperationIdentifier()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Guid.NewGuid();
        var mismatch = ValidRequest(now, Guid.NewGuid());
        var unsafeIdentifier = ValidRequest(now, session) with
        {
            OperationId = "powershell -Command whoami",
        };

        Assert.Equal(
            BrokerErrorCodes.SessionMismatch,
            BrokerProtocolValidation.ValidateRequest(mismatch, session, now)?.Code);
        Assert.Equal(
            BrokerErrorCodes.InvalidRequest,
            BrokerProtocolValidation.ValidateRequest(unsafeIdentifier, session, now)?.Code);
    }

    [Fact]
    public async Task FrameCodecRoundTripsExplicitUtf8Json()
    {
        await using var stream = new MemoryStream();
        var request = ValidRequest(DateTimeOffset.UtcNow, Guid.NewGuid());

        await BrokerFrameCodec.WriteAsync(
            stream,
            request,
            BrokerJsonContext.Default.BrokerRequestEnvelope,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var roundTrip = await BrokerFrameCodec.ReadAsync(
            stream,
            BrokerJsonContext.Default.BrokerRequestEnvelope,
            TestContext.Current.CancellationToken);

        Assert.Equal(request.ProtocolVersion, roundTrip.ProtocolVersion);
        Assert.Equal(request.SessionId, roundTrip.SessionId);
        Assert.Equal(request.RequestId, roundTrip.RequestId);
        Assert.Equal(request.OperationId, roundTrip.OperationId);
        Assert.Equal(request.PayloadVersion, roundTrip.PayloadVersion);
        Assert.True(JsonElement.DeepEquals(request.Payload, roundTrip.Payload));
        Assert.Equal(request.CreatedAtUtc, roundTrip.CreatedAtUtc);
        Assert.Equal(request.ExpiresAtUtc, roundTrip.ExpiresAtUtc);
    }

    [Fact]
    public async Task FrameCodecRejectsOversizedAndMalformedFramesWithoutUnlimitedBuffering()
    {
        var oversizedHeader = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(
            oversizedHeader,
            BrokerProtocol.MaximumFrameBytes + 1);
        await using var oversized = new MemoryStream(oversizedHeader);

        var oversizedError = await Assert.ThrowsAsync<BrokerProtocolException>(
            () => BrokerFrameCodec.ReadAsync(
                oversized,
                BrokerJsonContext.Default.BrokerRequestEnvelope,
                TestContext.Current.CancellationToken));

        var malformedPayload = Encoding.UTF8.GetBytes("{not-json}");
        var malformedFrame = new byte[sizeof(int) + malformedPayload.Length];
        BinaryPrimitives.WriteInt32BigEndian(
            malformedFrame.AsSpan(0, sizeof(int)),
            malformedPayload.Length);
        malformedPayload.CopyTo(malformedFrame.AsSpan(sizeof(int)));
        await using var malformed = new MemoryStream(malformedFrame);
        var malformedError = await Assert.ThrowsAsync<BrokerProtocolException>(
            () => BrokerFrameCodec.ReadAsync(
                malformed,
                BrokerJsonContext.Default.BrokerRequestEnvelope,
                TestContext.Current.CancellationToken));

        Assert.Equal(BrokerErrorCodes.InvalidFrame, oversizedError.Code);
        Assert.Equal(BrokerErrorCodes.InvalidFrame, malformedError.Code);
    }

    [Fact]
    public void SerializerRejectsUnmappedPropertiesAndPolymorphicTypeMetadata()
    {
        var request = ValidRequest(DateTimeOffset.UtcNow, Guid.NewGuid());
        var json = JsonSerializer.Serialize(
            request,
            BrokerJsonContext.Default.BrokerRequestEnvelope);
        var withTypeMetadata = json.Replace(
            "\"payload\":{",
            "\"unexpected\":true,\"payload\":{\"$type\":\"System.String\",",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                withTypeMetadata,
                BrokerJsonContext.Default.BrokerRequestEnvelope));
    }

    internal static BrokerHandshakeRequest ValidHandshake(DateTimeOffset now) =>
        new(
            BrokerProtocol.CurrentVersion,
            "1.0.0",
            Guid.NewGuid(),
            new(42, now.UtcTicks, 1, @"C:\kernctl\Kernctl.App.exe", "S-1-5-21-1"),
            "diagnostics",
            now.AddMinutes(1));

    internal static BrokerRequestEnvelope ValidRequest(
        DateTimeOffset now,
        Guid sessionId) =>
        new(
            BrokerProtocol.CurrentVersion,
            sessionId,
            Guid.NewGuid(),
            BrokerOperationIds.Ping,
            1,
            BrokerPayload.EmptyObject(),
            now,
            now.AddSeconds(10));
}
