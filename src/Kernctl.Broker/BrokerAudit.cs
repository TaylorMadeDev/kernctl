using System.Diagnostics.Tracing;

namespace Kernctl.Broker;

public interface IBrokerAuditSink
{
    void BrokerStarted();

    void ClientVerified();

    void HandshakeCompleted();

    void RequestRejected(string operationId, string errorCode);

    void OperationCompleted(string operationId, string status);

    void BrokerStopped(string reason);
}

public sealed class EventSourceBrokerAuditSink : IBrokerAuditSink
{
    public void BrokerStarted() => KernctlBrokerEventSource.Log.BrokerStarted();

    public void ClientVerified() => KernctlBrokerEventSource.Log.ClientVerified();

    public void HandshakeCompleted() => KernctlBrokerEventSource.Log.HandshakeCompleted();

    public void RequestRejected(string operationId, string errorCode) =>
        KernctlBrokerEventSource.Log.RequestRejected(operationId, errorCode);

    public void OperationCompleted(string operationId, string status) =>
        KernctlBrokerEventSource.Log.OperationCompleted(operationId, status);

    public void BrokerStopped(string reason) => KernctlBrokerEventSource.Log.BrokerStopped(reason);
}

[EventSource(Name = "kernctl-Broker")]
internal sealed class KernctlBrokerEventSource : EventSource
{
    public static KernctlBrokerEventSource Log { get; } = new();

    [Event(1, Level = EventLevel.Informational)]
    public void BrokerStarted() => WriteEvent(1);

    [Event(2, Level = EventLevel.Informational)]
    public void ClientVerified() => WriteEvent(2);

    [Event(3, Level = EventLevel.Informational)]
    public void HandshakeCompleted() => WriteEvent(3);

    [Event(4, Level = EventLevel.Warning)]
    public void RequestRejected(string operationId, string errorCode) =>
        WriteEvent(4, operationId, errorCode);

    [Event(5, Level = EventLevel.Informational)]
    public void OperationCompleted(string operationId, string status) =>
        WriteEvent(5, operationId, status);

    [Event(6, Level = EventLevel.Informational)]
    public void BrokerStopped(string reason) => WriteEvent(6, reason);
}
