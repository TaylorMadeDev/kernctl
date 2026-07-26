using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Client;

public sealed class BrokerClient(
    IBrokerExecutableResolver executableResolver,
    ICurrentProcessIdentityProvider identityProvider,
    IUacBrokerLauncher launcher,
    BrokerClientOptions options) : IBrokerClient
{
    private readonly string applicationVersion =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<BrokerClientOpenResult> OpenAsync(
        string requestedCapability,
        IProgress<BrokerLaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.ConnectionTimeout <= TimeSpan.Zero
            || options.ConnectionTimeout > TimeSpan.FromMinutes(1)
            || options.RequestTimeout <= TimeSpan.Zero
            || options.RequestTimeout > TimeSpan.FromSeconds(30)
            || options.SessionLifetime <= TimeSpan.Zero
            || options.SessionLifetime > BrokerProtocol.MaximumSessionLifetime)
        {
            return Failed(
                BrokerErrorCodes.InvalidRequest,
                "The administrator client timeout policy is invalid.");
        }

        if (requestedCapability != BrokerLaunchOptionsDiagnosticCapability)
        {
            return Failed(
                BrokerErrorCodes.InvalidRequest,
                "The requested administrator capability is not supported.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return Failed(
                BrokerErrorCodes.UnsupportedPlatform,
                "Administrator operations are available only on Windows.");
        }

        NamedPipeClientStream? pipe = null;
        Process? process = null;
        try
        {
            progress?.Report(new(
                BrokerLaunchStage.Preparing,
                "Preparing administrator request."));
            var executable = executableResolver.Resolve();
            var currentClient = identityProvider.GetCurrent();
            var sessionId = Guid.NewGuid();
            var expiresAtUtc = DateTimeOffset.UtcNow + options.SessionLifetime;
            var launch = new BrokerProcessLaunch(
                CreatePipeName(),
                sessionId,
                currentClient.Identity,
                currentClient.Sha256,
                requestedCapability,
                expiresAtUtc);

            progress?.Report(new(
                BrokerLaunchStage.AwaitingConsent,
                "Awaiting Windows administrator consent."));
            var launched = await launcher.LaunchAsync(
                executable,
                launch,
                cancellationToken);
            if (!launched.Succeeded || launched.Process is null)
            {
                progress?.Report(new(
                    launched.WasCancelled
                        ? BrokerLaunchStage.PermissionDeclined
                        : BrokerLaunchStage.Failed,
                    launched.Error?.SafeMessage
                    ?? "The administrator broker could not be started."));
                return launched.WasCancelled
                    ? BrokerClientOpenResult.Cancelled(launched.Error!)
                    : BrokerClientOpenResult.Failed(launched.Error!);
            }

            process = launched.Process;
            progress?.Report(new(
                BrokerLaunchStage.Connecting,
                "Connecting to the administrator broker."));
            pipe = new NamedPipeClientStream(
                ".",
                launch.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await ConnectAsync(pipe, process, cancellationToken);

            progress?.Report(new(
                BrokerLaunchStage.Verifying,
                "Verifying the administrator broker session."));
            using var handshakeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(options.RequestTimeout);
            var handshake = new BrokerHandshakeRequest(
                BrokerProtocol.CurrentVersion,
                applicationVersion,
                sessionId,
                currentClient.Identity,
                requestedCapability,
                expiresAtUtc);
            await BrokerFrameCodec.WriteAsync(
                pipe,
                handshake,
                BrokerJsonContext.Default.BrokerHandshakeRequest,
                handshakeTimeout.Token);
            var response = await BrokerFrameCodec.ReadAsync(
                pipe,
                BrokerJsonContext.Default.BrokerHandshakeResponse,
                handshakeTimeout.Token);
            ValidateHandshakeResponse(
                response,
                launch,
                executable,
                process,
                applicationVersion);
            if (response.Error is not null)
            {
                return BrokerClientOpenResult.Failed(response.Error);
            }

            var session = new BrokerSession(
                sessionId,
                response.BrokerVersion,
                response.ExpiresAtUtc,
                pipe,
                process,
                options);
            pipe = null;
            process = null;
            var sessionReady = false;
            try
            {
                var capabilityResponse = await session.GetCapabilitiesAsync(cancellationToken);
                if (capabilityResponse.Status != BrokerResponseStatus.Success)
                {
                    return BrokerClientOpenResult.Failed(new(
                        capabilityResponse.ErrorCode ?? BrokerErrorCodes.BrokerUnavailable,
                        capabilityResponse.SafeMessage,
                        RetryPossible: true));
                }

                var capabilities = capabilityResponse.Payload.Deserialize(
                    BrokerJsonContext.Default.BrokerCapabilitiesPayload)
                    ?? throw new BrokerClientException(
                        BrokerErrorCodes.InvalidRequest,
                        "The broker capability response was empty.");
                ValidateCapabilities(capabilities);
                progress?.Report(new(
                    BrokerLaunchStage.Ready,
                    "Administrator permission granted."));
                sessionReady = true;
                return BrokerClientOpenResult.Ready(session);
            }
            finally
            {
                if (!sessionReady)
                {
                    await session.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BrokerClientOpenResult.Cancelled(
                new(
                    BrokerErrorCodes.ElevationCancelled,
                    "The administrator request was cancelled. No changes were made.",
                    RetryPossible: true));
        }
        catch (Exception exception) when (
            exception is BrokerClientException
                or BrokerProtocolException
                or IOException
                or UnauthorizedAccessException
                or CryptographicException
                or FormatException
                or InvalidOperationException
                or JsonException)
        {
            progress?.Report(new(
                BrokerLaunchStage.Failed,
                "The administrator broker connection failed safely."));
            return Failed(
                exception is BrokerClientException clientException
                    ? clientException.Code
                    : BrokerErrorCodes.BrokerUnavailable,
                exception.Message);
        }
        finally
        {
            if (pipe is not null)
            {
                await pipe.DisposeAsync();
            }

            process?.Dispose();
        }
    }

    private async Task ConnectAsync(
        NamedPipeClientStream pipe,
        Process process,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectionTimeout);
        var connectTask = pipe.ConnectAsync(timeout.Token);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var completed = await Task.WhenAny(connectTask, exitTask);
        if (completed == exitTask)
        {
            await exitTask;
            throw new BrokerClientException(
                BrokerErrorCodes.BrokerUnavailable,
                "The administrator broker exited before accepting the connection.");
        }

        try
        {
            await connectTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrokerClientException(
                BrokerErrorCodes.ConnectionTimedOut,
                "The administrator broker did not connect in time.");
        }
    }

    private static void ValidateHandshakeResponse(
        BrokerHandshakeResponse response,
        BrokerProcessLaunch launch,
        ResolvedBrokerExecutable executable,
        Process process,
        string expectedApplicationVersion)
    {
        var brokerVersionIsCompatible =
            Version.TryParse(expectedApplicationVersion, out var applicationVersion)
            && Version.TryParse(response.BrokerVersion, out var brokerVersion)
            && applicationVersion.Major == brokerVersion.Major;
        if (response.ProtocolVersion != BrokerProtocol.CurrentVersion
            || response.SessionId != launch.SessionId
            || response.ExpiresAtUtc != launch.ExpiresAtUtc
            || response.Broker.ProcessId != process.Id
            || response.Broker.ProcessStartUtcTicks
                != new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks
            || !string.Equals(
                Path.GetFullPath(response.Broker.ExecutablePath),
                executable.AbsolutePath,
                StringComparison.OrdinalIgnoreCase)
            || response.Broker.WindowsSessionId != launch.ClientIdentity.WindowsSessionId
            || !brokerVersionIsCompatible
            || (response.Error is null
                && (response.ApplicationVersion != expectedApplicationVersion
                    || response.GrantedCapability != launch.RequestedCapability)))
        {
            throw new BrokerClientException(
                BrokerErrorCodes.InvalidHandshake,
                "The administrator broker handshake did not match the launched process.");
        }

        using var brokerFile = new FileStream(
            executable.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var observedHash = SHA256.HashData(brokerFile);
        if (!CryptographicOperations.FixedTimeEquals(
                observedHash,
                Convert.FromHexString(executable.Sha256)))
        {
            throw new BrokerClientException(
                BrokerErrorCodes.ClientIdentityRejected,
                "The administrator broker executable changed during launch.");
        }
    }

    private static void ValidateCapabilities(BrokerCapabilitiesPayload capabilities)
    {
        var expected = BrokerOperationIds.Diagnostics
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var actual = capabilities.Operations
            .Select(operation => operation.OperationId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (capabilities.ProtocolVersion != BrokerProtocol.CurrentVersion
            || capabilities.MaximumFrameBytes != BrokerProtocol.MaximumFrameBytes
            || capabilities.MaximumRequestsPerSession > BrokerProtocol.MaximumRequestsPerSession
            || capabilities.Operations.Any(operation =>
                operation.MutatesState
                || operation.Risk != BrokerRiskClassification.Diagnostic)
            || !actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new BrokerClientException(
                BrokerErrorCodes.IncompatibleVersion,
                "The administrator broker capability set is incompatible.");
        }
    }

    private static string CreatePipeName()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        return $"kernctl-{Guid.NewGuid():N}-{Convert.ToHexString(random).ToLowerInvariant()}";
    }

    private static BrokerClientOpenResult Failed(string code, string message) =>
        BrokerClientOpenResult.Failed(new(code, message, RetryPossible: true));

    private const string BrokerLaunchOptionsDiagnosticCapability = "diagnostics";
}
