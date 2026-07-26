using System.ComponentModel;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Kernctl.Broker.Protocol;
using Kernctl.Platform.Windows;

namespace Kernctl.Broker;

public sealed record BrokerHostOptions(
    TimeSpan ConnectionTimeout,
    TimeSpan IdleTimeout,
    int MaximumRequests,
    bool RequireElevation)
{
    public static BrokerHostOptions Production { get; } = new(
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(15),
        BrokerProtocol.MaximumRequestsPerSession,
        RequireElevation: true);
}

public sealed class BrokerHost(
    IRestrictedNamedPipeFactory pipeFactory,
    IClientIdentityVerifier clientIdentityVerifier,
    IBrokerOperationRegistry operationRegistry,
    IWindowsBrokerDiagnostics diagnostics,
    IBrokerAuditSink audit,
    BrokerHostOptions hostOptions)
{
    private readonly string brokerVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<int> RunAsync(
        BrokerLaunchOptions launch,
        CancellationToken cancellationToken)
    {
        if (hostOptions.ConnectionTimeout <= TimeSpan.Zero
            || hostOptions.ConnectionTimeout > TimeSpan.FromMinutes(1)
            || hostOptions.IdleTimeout <= TimeSpan.Zero
            || hostOptions.IdleTimeout > TimeSpan.FromMinutes(1)
            || hostOptions.MaximumRequests is < 1 or > BrokerProtocol.MaximumRequestsPerSession)
        {
            return 9;
        }

        if (!OperatingSystem.IsWindows())
        {
            return 10;
        }

        if (hostOptions.RequireElevation && !diagnostics.IsElevated())
        {
            return 11;
        }

        audit.BrokerStarted();
        var stopReason = "completed";
        try
        {
            using var pipe = pipeFactory.Create(
                launch.PipeName,
                launch.ExpectedClient.UserSid);
            using (var connectionTimeout =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectionTimeout.CancelAfter(hostOptions.ConnectionTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(connectionTimeout.Token);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    stopReason = "connection-timeout";
                    return 12;
                }
            }

            BrokerHandshakeRequest handshake;
            try
            {
                handshake = await ReadWithIdleTimeoutAsync(
                    pipe,
                    BrokerJsonContext.Default.BrokerHandshakeRequest,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is BrokerProtocolException
                    or EndOfStreamException
                    or OperationCanceledException)
            {
                stopReason = "invalid-handshake-frame";
                return 13;
            }

            var handshakeError = BrokerProtocolValidation.ValidateHandshake(
                handshake,
                DateTimeOffset.UtcNow);
            var identity = handshakeError is null
                ? clientIdentityVerifier.Verify(pipe, launch, handshake)
                : new ClientIdentityVerification(false, null, handshakeError);
            if (!identity.Succeeded)
            {
                await TryWriteHandshakeFailureAsync(
                    pipe,
                    launch,
                    identity.Error
                    ?? new(
                        BrokerErrorCodes.ClientIdentityRejected,
                        "The application identity was rejected.",
                        RetryPossible: false),
                    cancellationToken);
                stopReason = "client-rejected";
                return 14;
            }

            audit.ClientVerified();
            var brokerIdentity = CreateBrokerIdentity();
            await BrokerFrameCodec.WriteAsync(
                pipe,
                new BrokerHandshakeResponse(
                    BrokerProtocol.CurrentVersion,
                    handshake.ApplicationVersion,
                    brokerVersion,
                    launch.SessionId,
                    brokerIdentity,
                    launch.RequestedCapability,
                    launch.ExpiresAtUtc,
                    null),
                BrokerJsonContext.Default.BrokerHandshakeResponse,
                cancellationToken);
            audit.HandshakeCompleted();

            var seenRequestIds = new HashSet<Guid>();
            var shutdownRequested = false;
            var capabilities = operationRegistry.Operations
                .Select(BrokerOperationRegistry.ToCapability)
                .ToArray();
            var operationContext = new BrokerOperationContext(
                brokerVersion,
                diagnostics.IsElevated(),
                hostOptions.IdleTimeout,
                capabilities,
                () => shutdownRequested = true);

            for (var requestCount = 0;
                 requestCount < hostOptions.MaximumRequests && !shutdownRequested;
                 requestCount++)
            {
                BrokerRequestEnvelope request;
                try
                {
                    request = await ReadWithIdleTimeoutAsync(
                        pipe,
                        BrokerJsonContext.Default.BrokerRequestEnvelope,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    stopReason = "idle-timeout";
                    return 0;
                }
                catch (EndOfStreamException)
                {
                    stopReason = "client-disconnected";
                    return 0;
                }
                catch (BrokerProtocolException)
                {
                    stopReason = "invalid-request-frame";
                    return 15;
                }

                var response = await DispatchAsync(
                    request,
                    launch.SessionId,
                    launch.ExpiresAtUtc,
                    operationContext,
                    seenRequestIds,
                    cancellationToken);
                await BrokerFrameCodec.WriteAsync(
                    pipe,
                    response,
                    BrokerJsonContext.Default.BrokerResponseEnvelope,
                    cancellationToken);
                audit.OperationCompleted(request.OperationId, response.Status.ToString());
            }

            stopReason = shutdownRequested ? "explicit-shutdown" : "request-limit";
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopReason = "cancelled";
            return 0;
        }
        catch (IOException)
        {
            stopReason = "io-failure";
            return 16;
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or UnauthorizedAccessException
                or InvalidOperationException
                or BrokerProtocolException)
        {
            stopReason = "broker-failure";
            return 17;
        }
        finally
        {
            audit.BrokerStopped(stopReason);
        }
    }

    private async Task<BrokerResponseEnvelope> DispatchAsync(
        BrokerRequestEnvelope request,
        Guid establishedSessionId,
        DateTimeOffset sessionExpiresAtUtc,
        BrokerOperationContext context,
        HashSet<Guid> seenRequestIds,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var error = nowUtc >= sessionExpiresAtUtc
            ? new BrokerStructuredError(
                BrokerErrorCodes.SessionExpired,
                "The administrator session has expired.",
                RetryPossible: false)
            : BrokerProtocolValidation.ValidateRequest(
                request,
                establishedSessionId,
                nowUtc);

        if (error is null && !seenRequestIds.Add(request.RequestId))
        {
            error = new(
                BrokerErrorCodes.DuplicateRequest,
                "The administrator request identifier was already used.",
                RetryPossible: false);
        }

        if (error is not null)
        {
            return Rejected(request, error);
        }

        if (!operationRegistry.TryGet(request.OperationId, out var operation)
            || operation is null)
        {
            return Rejected(
                request,
                new(
                    BrokerErrorCodes.UnknownOperation,
                    "The requested administrator operation is not registered.",
                    RetryPossible: false));
        }

        if (request.PayloadVersion != operation.Descriptor.PayloadVersion)
        {
            return Rejected(
                request,
                new(
                    BrokerErrorCodes.InvalidRequest,
                    "The operation payload version is unsupported.",
                    RetryPossible: false));
        }

        var validation = operation.Validate(request.Payload);
        if (!validation.IsValid)
        {
            return Rejected(
                request,
                new(
                    BrokerErrorCodes.InvalidRequest,
                    validation.SafeMessage,
                    RetryPossible: false));
        }

        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(operation.Descriptor.MaximumExecutionTime);
            var result = await operation.ExecuteAsync(
                context,
                request.Payload,
                timeout.Token);
            if (!BrokerProtocolValidation.IsBounded(
                    result.SafeMessage,
                    BrokerProtocol.MaximumStringLength)
                || (result.ErrorCode is not null
                    && !BrokerProtocolValidation.IsBounded(result.ErrorCode, 128))
                || (result.Status == BrokerResponseStatus.Success
                    && result.ErrorCode is not null)
                || (result.Status != BrokerResponseStatus.Success
                    && result.ErrorCode is null)
                || result.Payload.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    request,
                    BrokerErrorCodes.OperationFailed,
                    "The administrator operation returned an invalid result.");
            }

            return new(
                BrokerProtocol.CurrentVersion,
                request.SessionId,
                request.RequestId,
                request.OperationId,
                result.Status,
                result.SafeMessage,
                result.ErrorCode,
                result.Payload,
                brokerVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                request,
                BrokerErrorCodes.OperationTimedOut,
                "The administrator operation timed out.");
        }
        catch (Exception)
        {
            return Failure(
                request,
                BrokerErrorCodes.OperationFailed,
                "The administrator operation failed safely.");
        }
    }

    private BrokerResponseEnvelope Rejected(
        BrokerRequestEnvelope request,
        BrokerStructuredError error)
    {
        audit.RequestRejected(
            BrokerProtocolValidation.IsOperationId(request.OperationId)
                ? request.OperationId
                : "<invalid>",
            error.Code);
        return new(
            BrokerProtocol.CurrentVersion,
            request.SessionId,
            request.RequestId,
            request.OperationId,
            BrokerResponseStatus.Rejected,
            error.SafeMessage,
            error.Code,
            EmptyPayload(),
            brokerVersion);
    }

    private BrokerResponseEnvelope Failure(
        BrokerRequestEnvelope request,
        string code,
        string message) =>
        new(
            BrokerProtocol.CurrentVersion,
            request.SessionId,
            request.RequestId,
            request.OperationId,
            BrokerResponseStatus.Failed,
            message,
            code,
            EmptyPayload(),
            brokerVersion);

    private async Task<T> ReadWithIdleTimeoutAsync<T>(
        Stream pipe,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var idle =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(hostOptions.IdleTimeout);
        return await BrokerFrameCodec.ReadAsync(pipe, typeInfo, idle.Token);
    }

    private static JsonElement EmptyPayload() =>
        BrokerPayload.EmptyObject();

    [SupportedOSPlatform("windows")]
    private static BrokerProcessIdentity CreateBrokerIdentity()
    {
        using var process = WindowsNative.OpenProcessForIdentity(Environment.ProcessId);
        return new(
            Environment.ProcessId,
            WindowsNative.QueryProcessStartUtcTicks(process),
            Environment.ProcessId == 0 ? 0 : System.Diagnostics.Process.GetCurrentProcess().SessionId,
            Environment.ProcessPath ?? string.Empty,
            WindowsNative.QueryProcessUserSid(process));
    }

    [SupportedOSPlatform("windows")]
    private async Task TryWriteHandshakeFailureAsync(
        Stream pipe,
        BrokerLaunchOptions launch,
        BrokerStructuredError error,
        CancellationToken cancellationToken)
    {
        try
        {
            await BrokerFrameCodec.WriteAsync(
                pipe,
                new BrokerHandshakeResponse(
                    BrokerProtocol.CurrentVersion,
                    string.Empty,
                    brokerVersion,
                    launch.SessionId,
                    CreateBrokerIdentity(),
                    string.Empty,
                    launch.ExpiresAtUtc,
                    error),
                BrokerJsonContext.Default.BrokerHandshakeResponse,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException
                or BrokerProtocolException
                or OperationCanceledException)
        {
        }
    }
}
