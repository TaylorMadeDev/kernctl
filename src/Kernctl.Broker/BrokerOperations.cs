using System.Text.Json;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker;

public sealed record BrokerOperationDescriptor(
    string OperationId,
    int PayloadVersion,
    int RequiredProtocolVersion,
    BrokerRiskClassification Risk,
    bool MutatesState,
    TimeSpan MaximumExecutionTime,
    string AuditDescription);

public sealed record BrokerOperationValidation(bool IsValid, string SafeMessage)
{
    public static BrokerOperationValidation Valid { get; } = new(true, string.Empty);

    public static BrokerOperationValidation Invalid(string safeMessage) =>
        new(false, safeMessage);
}

public sealed record BrokerOperationResult(
    BrokerResponseStatus Status,
    string SafeMessage,
    string? ErrorCode,
    JsonElement Payload);

public sealed class BrokerOperationContext(
    string brokerVersion,
    bool isElevated,
    TimeSpan idleTimeout,
    IReadOnlyCollection<BrokerCapability> capabilities,
    Action requestShutdown)
{
    public string BrokerVersion { get; } = brokerVersion;

    public bool IsElevated { get; } = isElevated;

    public TimeSpan IdleTimeout { get; } = idleTimeout;

    public IReadOnlyCollection<BrokerCapability> Capabilities { get; } = capabilities;

    public void RequestShutdown() => requestShutdown();
}

public interface IBrokerOperation
{
    BrokerOperationDescriptor Descriptor { get; }

    BrokerOperationValidation Validate(JsonElement payload);

    Task<BrokerOperationResult> ExecuteAsync(
        BrokerOperationContext context,
        JsonElement payload,
        CancellationToken cancellationToken);
}

public interface IBrokerOperationRegistry
{
    IReadOnlyCollection<IBrokerOperation> Operations { get; }

    bool TryGet(string operationId, out IBrokerOperation? operation);
}

public sealed class BrokerOperationRegistry : IBrokerOperationRegistry
{
    private readonly IReadOnlyDictionary<string, IBrokerOperation> operations;

    public BrokerOperationRegistry(IEnumerable<IBrokerOperation> operations)
    {
        var registered = operations.ToDictionary(
            operation => operation.Descriptor.OperationId,
            StringComparer.Ordinal);
        if (registered.Count == 0
            || registered.Values.Any(operation =>
                !BrokerProtocolValidation.IsOperationId(operation.Descriptor.OperationId)
                || operation.Descriptor.PayloadVersion <= 0
                || operation.Descriptor.RequiredProtocolVersion != BrokerProtocol.CurrentVersion
                || operation.Descriptor.MutatesState
                || operation.Descriptor.MaximumExecutionTime <= TimeSpan.Zero
                || operation.Descriptor.MaximumExecutionTime > TimeSpan.FromSeconds(10)
                || string.IsNullOrWhiteSpace(operation.Descriptor.AuditDescription)))
        {
            throw new InvalidOperationException("The broker operation registry is invalid.");
        }

        this.operations = registered;
    }

    public IReadOnlyCollection<IBrokerOperation> Operations => [.. operations.Values];

    public bool TryGet(string operationId, out IBrokerOperation? operation) =>
        operations.TryGetValue(operationId, out operation);

    public static BrokerOperationRegistry CreateDiagnostics()
    {
        BrokerOperationRegistry? registry = null;
        var operations = new IBrokerOperation[]
        {
            new GetInfoOperation(),
            new GetCapabilitiesOperation(() =>
                registry?.Operations.Select(ToCapability).ToArray()
                ?? Array.Empty<BrokerCapability>()),
            new PingOperation(),
            new ShutdownOperation(),
        };
        registry = new(operations);
        return registry;
    }

    public static BrokerCapability ToCapability(IBrokerOperation operation) =>
        new(
            operation.Descriptor.OperationId,
            operation.Descriptor.PayloadVersion,
            operation.Descriptor.RequiredProtocolVersion,
            operation.Descriptor.Risk,
            operation.Descriptor.MutatesState,
            checked((int)operation.Descriptor.MaximumExecutionTime.TotalMilliseconds),
            operation.Descriptor.AuditDescription);
}

internal abstract class EmptyPayloadDiagnosticOperation : IBrokerOperation
{
    public abstract BrokerOperationDescriptor Descriptor { get; }

    public BrokerOperationValidation Validate(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && !payload.EnumerateObject().Any()
            ? BrokerOperationValidation.Valid
            : BrokerOperationValidation.Invalid(
                "This diagnostic operation accepts only an empty object payload.");

    public abstract Task<BrokerOperationResult> ExecuteAsync(
        BrokerOperationContext context,
        JsonElement payload,
        CancellationToken cancellationToken);

    protected static BrokerOperationDescriptor Diagnostic(
        string operationId,
        string auditDescription) =>
        new(
            operationId,
            PayloadVersion: 1,
            BrokerProtocol.CurrentVersion,
            BrokerRiskClassification.Diagnostic,
            MutatesState: false,
            MaximumExecutionTime: TimeSpan.FromSeconds(2),
            auditDescription);

    protected static BrokerOperationResult Success<T>(
        string message,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        new(
            BrokerResponseStatus.Success,
            message,
            null,
            JsonSerializer.SerializeToElement(payload, typeInfo));
}

internal sealed class GetInfoOperation : EmptyPayloadDiagnosticOperation
{
    public override BrokerOperationDescriptor Descriptor { get; } =
        Diagnostic(BrokerOperationIds.GetInfo, "Read restricted broker metadata.");

    public override Task<BrokerOperationResult> ExecuteAsync(
        BrokerOperationContext context,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var result = new BrokerInfoPayload(
            context.BrokerVersion,
            BrokerProtocol.CurrentVersion,
            Environment.ProcessId,
            context.IsElevated,
            BrokerOperationIds.Diagnostics,
            checked((int)context.IdleTimeout.TotalSeconds));
        return Task.FromResult(Success(
            "Broker information returned.",
            result,
            BrokerJsonContext.Default.BrokerInfoPayload));
    }
}

internal sealed class GetCapabilitiesOperation(
    Func<IReadOnlyCollection<BrokerCapability>> capabilities)
    : EmptyPayloadDiagnosticOperation
{
    public override BrokerOperationDescriptor Descriptor { get; } =
        Diagnostic(
            BrokerOperationIds.GetCapabilities,
            "List explicitly registered broker diagnostics.");

    public override Task<BrokerOperationResult> ExecuteAsync(
        BrokerOperationContext context,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var result = new BrokerCapabilitiesPayload(
            BrokerProtocol.CurrentVersion,
            [.. capabilities().OrderBy(capability => capability.OperationId, StringComparer.Ordinal)],
            BrokerProtocol.MaximumRequestsPerSession,
            BrokerProtocol.MaximumFrameBytes,
            checked((int)context.IdleTimeout.TotalSeconds));
        return Task.FromResult(Success(
            "Broker capabilities returned.",
            result,
            BrokerJsonContext.Default.BrokerCapabilitiesPayload));
    }
}

internal sealed class PingOperation : EmptyPayloadDiagnosticOperation
{
    public override BrokerOperationDescriptor Descriptor { get; } =
        Diagnostic(BrokerOperationIds.Ping, "Confirm that the broker session is responsive.");

    public override Task<BrokerOperationResult> ExecuteAsync(
        BrokerOperationContext context,
        JsonElement payload,
        CancellationToken cancellationToken) =>
        Task.FromResult(Success(
            "Broker is responsive.",
            new BrokerPingPayload(DateTimeOffset.UtcNow),
            BrokerJsonContext.Default.BrokerPingPayload));
}

internal sealed class ShutdownOperation : EmptyPayloadDiagnosticOperation
{
    public override BrokerOperationDescriptor Descriptor { get; } =
        Diagnostic(BrokerOperationIds.Shutdown, "End the current broker session.");

    public override Task<BrokerOperationResult> ExecuteAsync(
        BrokerOperationContext context,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        context.RequestShutdown();
        return Task.FromResult(Success(
            "Broker shutdown accepted.",
            new BrokerShutdownPayload(true),
            BrokerJsonContext.Default.BrokerShutdownPayload));
    }
}
