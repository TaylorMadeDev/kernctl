using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kernctl.Broker.Protocol;

public static class BrokerFrameCodec
{
    private const int HeaderBytes = sizeof(int);

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        if (payload.Length is <= 0 or > BrokerProtocol.MaximumFrameBytes)
        {
            throw new BrokerProtocolException(
                BrokerErrorCodes.InvalidFrame,
                "The broker message exceeds the allowed size.");
        }

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var header = new byte[HeaderBytes];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header);
        if (payloadLength is <= 0 or > BrokerProtocol.MaximumFrameBytes)
        {
            throw new BrokerProtocolException(
                BrokerErrorCodes.InvalidFrame,
                "The broker message has an invalid size.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await ReadExactlyAsync(stream, payload, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo)
                ?? throw new BrokerProtocolException(
                    BrokerErrorCodes.InvalidFrame,
                    "The broker message is empty.");
        }
        catch (JsonException exception)
        {
            throw new BrokerProtocolException(
                BrokerErrorCodes.InvalidFrame,
                "The broker message contains invalid JSON.",
                exception);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The broker connection closed during a message.");
            }

            offset += read;
        }
    }
}

public sealed class BrokerProtocolException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
