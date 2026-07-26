using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using Kernctl.Broker.Protocol;
using Kernctl.Platform.Windows;

namespace Kernctl.Broker.Tests;

public sealed class WindowsBrokerIntegrationTests
{
    [Fact]
    public async Task ProductionIdentityVerifierUsesOsPipeIdentityAndRejectsUntrustedLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var windowsIdentity = WindowsIdentity.GetCurrent();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var path = Environment.ProcessPath ?? throw new InvalidOperationException();
        using var executable = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(executable));
        var identity = new BrokerProcessIdentity(
            Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks,
            process.SessionId,
            path,
            windowsIdentity.User?.Value ?? throw new InvalidOperationException());
        var launch = new BrokerLaunchOptions(
            $"kernctl-{Guid.NewGuid():N}-{Guid.NewGuid():N}",
            Guid.NewGuid(),
            identity,
            hash,
            "diagnostics",
            DateTimeOffset.UtcNow.AddMinutes(1));
        using var server = new RestrictedNamedPipeFactory().Create(
            launch.PipeName,
            identity.UserSid);
        Assert.True(WindowsNative.RejectsRemoteClients(server.SafePipeHandle));
        await using var client = new NamedPipeClientStream(
            ".",
            launch.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var wait = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await wait;

        var result = new ClientIdentityVerifier(
            new AuthenticodeExecutableTrustVerifier()).Verify(
                server,
                launch,
                new(
                    BrokerProtocol.CurrentVersion,
                    "1.0.0",
                    launch.SessionId,
                    identity,
                    launch.RequestedCapability,
                    launch.ExpiresAtUtc));

        Assert.False(result.Succeeded);
        Assert.Equal(BrokerErrorCodes.ClientIdentityRejected, result.Error?.Code);
        Assert.Contains(
            "trusted layout",
            result.Error?.SafeMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecuredSingleClientPipeCompletesHandshakeRejectsUnknownOperationAndShutsDown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException();
        var now = DateTimeOffset.UtcNow;
        var client = new BrokerProcessIdentity(
            Environment.ProcessId,
            now.UtcTicks,
            System.Diagnostics.Process.GetCurrentProcess().SessionId,
            Environment.ProcessPath ?? @"C:\testhost.exe",
            sid);
        var launch = new BrokerLaunchOptions(
            $"kernctl-{Guid.NewGuid():N}-{Guid.NewGuid():N}",
            Guid.NewGuid(),
            client,
            new string('A', 64),
            "diagnostics",
            now.AddMinutes(1));
        var verifier = new AcceptingClientVerifier(client);
        var audit = new RecordingAuditSink();
        var host = new BrokerHost(
            new RestrictedNamedPipeFactory(),
            verifier,
            BrokerOperationRegistry.CreateDiagnostics(),
            new FakeDiagnostics(),
            audit,
            new(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                BrokerProtocol.MaximumRequestsPerSession,
                RequireElevation: false));
        var hostTask = host.RunAsync(launch, TestContext.Current.CancellationToken);

        await using var pipe = new NamedPipeClientStream(
            ".",
            launch.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await BrokerFrameCodec.WriteAsync(
            pipe,
            new BrokerHandshakeRequest(
                BrokerProtocol.CurrentVersion,
                "1.0.0",
                launch.SessionId,
                client,
                launch.RequestedCapability,
                launch.ExpiresAtUtc),
            BrokerJsonContext.Default.BrokerHandshakeRequest,
            TestContext.Current.CancellationToken);
        var handshake = await BrokerFrameCodec.ReadAsync(
            pipe,
            BrokerJsonContext.Default.BrokerHandshakeResponse,
            TestContext.Current.CancellationToken);

        var unknown = Request(launch.SessionId, "broker.run-command");
        await BrokerFrameCodec.WriteAsync(
            pipe,
            unknown,
            BrokerJsonContext.Default.BrokerRequestEnvelope,
            TestContext.Current.CancellationToken);
        var rejected = await BrokerFrameCodec.ReadAsync(
            pipe,
            BrokerJsonContext.Default.BrokerResponseEnvelope,
            TestContext.Current.CancellationToken);

        await BrokerFrameCodec.WriteAsync(
            pipe,
            unknown,
            BrokerJsonContext.Default.BrokerRequestEnvelope,
            TestContext.Current.CancellationToken);
        var duplicate = await BrokerFrameCodec.ReadAsync(
            pipe,
            BrokerJsonContext.Default.BrokerResponseEnvelope,
            TestContext.Current.CancellationToken);

        var shutdown = Request(launch.SessionId, BrokerOperationIds.Shutdown);
        await BrokerFrameCodec.WriteAsync(
            pipe,
            shutdown,
            BrokerJsonContext.Default.BrokerRequestEnvelope,
            TestContext.Current.CancellationToken);
        var shutdownResponse = await BrokerFrameCodec.ReadAsync(
            pipe,
            BrokerJsonContext.Default.BrokerResponseEnvelope,
            TestContext.Current.CancellationToken);
        var exitCode = await hostTask;

        Assert.Null(handshake.Error);
        Assert.Equal(BrokerResponseStatus.Rejected, rejected.Status);
        Assert.Equal(BrokerErrorCodes.UnknownOperation, rejected.ErrorCode);
        Assert.Equal(BrokerResponseStatus.Rejected, duplicate.Status);
        Assert.Equal(BrokerErrorCodes.DuplicateRequest, duplicate.ErrorCode);
        Assert.Equal(BrokerResponseStatus.Success, shutdownResponse.Status);
        Assert.Equal(0, exitCode);
        Assert.Equal(1, verifier.CallCount);
        Assert.Contains("broker.run-command", audit.RejectedOperations);
    }

    private static BrokerRequestEnvelope Request(Guid sessionId, string operationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            BrokerProtocol.CurrentVersion,
            sessionId,
            Guid.NewGuid(),
            operationId,
            1,
            BrokerPayload.EmptyObject(),
            now,
            now.AddSeconds(10));
    }

    private sealed class AcceptingClientVerifier(BrokerProcessIdentity identity)
        : IClientIdentityVerifier
    {
        public int CallCount { get; private set; }

        public ClientIdentityVerification Verify(
            NamedPipeServerStream pipe,
            BrokerLaunchOptions launch,
            BrokerHandshakeRequest handshake)
        {
            CallCount++;
            return new(true, identity, null);
        }
    }

    private sealed class FakeDiagnostics : IWindowsBrokerDiagnostics
    {
        public bool IsElevated() => false;
    }

    private sealed class RecordingAuditSink : IBrokerAuditSink
    {
        public List<string> RejectedOperations { get; } = [];

        public void BrokerStarted()
        {
        }

        public void ClientVerified()
        {
        }

        public void HandshakeCompleted()
        {
        }

        public void RequestRejected(string operationId, string errorCode) =>
            RejectedOperations.Add(operationId);

        public void OperationCompleted(string operationId, string status)
        {
        }

        public void BrokerStopped(string reason)
        {
        }
    }
}
