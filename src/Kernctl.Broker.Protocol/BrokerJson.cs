using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kernctl.Broker.Protocol;

public static class BrokerJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        TypeInfoResolver = BrokerJsonContext.Default,
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BrokerHandshakeRequest))]
[JsonSerializable(typeof(BrokerHandshakeResponse))]
[JsonSerializable(typeof(BrokerRequestEnvelope))]
[JsonSerializable(typeof(BrokerResponseEnvelope))]
[JsonSerializable(typeof(BrokerCapabilitiesPayload))]
[JsonSerializable(typeof(BrokerInfoPayload))]
[JsonSerializable(typeof(BrokerPingPayload))]
[JsonSerializable(typeof(BrokerShutdownPayload))]
[JsonSerializable(typeof(BrokerStructuredError))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class BrokerJsonContext : JsonSerializerContext;
