using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Client;

public sealed class BrokerSession : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly Process brokerProcess;
    private readonly BrokerClientOptions options;
    private readonly DateTimeOffset expiresAtUtc;
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private int requestCount;
    private bool disposed;

    internal BrokerSession(
        Guid sessionId,
        string brokerVersion,
        DateTimeOffset expiresAtUtc,
        NamedPipeClientStream pipe,
        Process brokerProcess,
        BrokerClientOptions options)
    {
        SessionId = sessionId;
        BrokerVersion = brokerVersion;
        this.expiresAtUtc = expiresAtUtc;
        this.pipe = pipe;
        this.brokerProcess = brokerProcess;
        this.options = options;
    }

    public Guid SessionId { get; }

    public string BrokerVersion { get; }

    public Task<BrokerResponseEnvelope> GetInfoAsync(
        CancellationToken cancellationToken = default) =>
        SendDiagnosticAsync(BrokerOperationIds.GetInfo, cancellationToken);

    public Task<BrokerResponseEnvelope> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        SendDiagnosticAsync(BrokerOperationIds.GetCapabilities, cancellationToken);

    public Task<BrokerResponseEnvelope> PingAsync(
        CancellationToken cancellationToken = default) =>
        SendDiagnosticAsync(BrokerOperationIds.Ping, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (pipe.IsConnected && requestCount < BrokerProtocol.MaximumRequestsPerSession)
            {
                await SendCoreAsync(
                    BrokerOperationIds.Shutdown,
                    allowDisposing: true,
                    shutdownTimeout.Token);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or OperationCanceledException
                or BrokerClientException
                or BrokerProtocolException)
        {
        }
        finally
        {
            await pipe.DisposeAsync();
            try
            {
                await brokerProcess.WaitForExitAsync(shutdownTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                // An elevated process cannot be force-terminated by the UI. Its idle timeout remains active.
            }

            brokerProcess.Dispose();
            requestLock.Dispose();
        }
    }

    internal async Task<BrokerResponseEnvelope> SendDiagnosticAsync(
        string operationId,
        CancellationToken cancellationToken) =>
        await SendCoreAsync(operationId, allowDisposing: false, cancellationToken);

    private async Task<BrokerResponseEnvelope> SendCoreAsync(
        string operationId,
        bool allowDisposing,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed && !allowDisposing, this);

        if (!BrokerOperationIds.Diagnostics.Contains(operationId, StringComparer.Ordinal))
        {
            throw new BrokerClientException(
                BrokerErrorCodes.UnknownOperation,
                "The client attempted an operation outside its diagnostic allowlist.");
        }

        await requestLock.WaitAsync(cancellationToken);
        try
        {
            if (++requestCount > BrokerProtocol.MaximumRequestsPerSession)
            {
                throw new BrokerClientException(
                    BrokerErrorCodes.RequestLimitExceeded,
                    "The administrator session request limit was reached.");
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var requestExpiry = nowUtc + options.RequestTimeout;
            if (requestExpiry > expiresAtUtc)
            {
                requestExpiry = expiresAtUtc;
            }

            if (requestExpiry <= nowUtc)
            {
                throw new BrokerClientException(
                    BrokerErrorCodes.SessionExpired,
                    "The administrator session has expired.");
            }

            var request = new BrokerRequestEnvelope(
                BrokerProtocol.CurrentVersion,
                SessionId,
                Guid.NewGuid(),
                operationId,
                PayloadVersion: 1,
                EmptyPayload(),
                nowUtc,
                requestExpiry);
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            await BrokerFrameCodec.WriteAsync(
                pipe,
                request,
                BrokerJsonContext.Default.BrokerRequestEnvelope,
                timeout.Token);
            var response = await BrokerFrameCodec.ReadAsync(
                pipe,
                BrokerJsonContext.Default.BrokerResponseEnvelope,
                timeout.Token);
            if (response.ProtocolVersion != BrokerProtocol.CurrentVersion
                || response.SessionId != SessionId
                || response.RequestId != request.RequestId
                || response.OperationId != request.OperationId
                || string.IsNullOrWhiteSpace(response.BrokerVersion)
                || response.SafeMessage.Length > BrokerProtocol.MaximumStringLength)
            {
                throw new BrokerClientException(
                    BrokerErrorCodes.InvalidRequest,
                    "The administrator broker returned a mismatched response.");
            }

            return response;
        }
        finally
        {
            requestLock.Release();
        }
    }

    private static JsonElement EmptyPayload() =>
        BrokerPayload.EmptyObject();
}
