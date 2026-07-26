using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Client;

public sealed record BrokerClientOptions(
    TimeSpan ConnectionTimeout,
    TimeSpan RequestTimeout,
    TimeSpan SessionLifetime)
{
    public static BrokerClientOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(1));
}

public enum BrokerClientOpenStatus
{
    Ready,
    Cancelled,
    Failed,
}

public enum BrokerLaunchStage
{
    Preparing,
    AwaitingConsent,
    Connecting,
    Verifying,
    Ready,
    PermissionDeclined,
    Failed,
}

public sealed record BrokerLaunchProgress(BrokerLaunchStage Stage, string SafeMessage);

public sealed record BrokerClientOpenResult(
    BrokerClientOpenStatus Status,
    BrokerSession? Session,
    BrokerStructuredError? Error)
{
    public static BrokerClientOpenResult Ready(BrokerSession session) =>
        new(BrokerClientOpenStatus.Ready, session, null);

    public static BrokerClientOpenResult Cancelled(BrokerStructuredError error) =>
        new(BrokerClientOpenStatus.Cancelled, null, error);

    public static BrokerClientOpenResult Failed(BrokerStructuredError error) =>
        new(BrokerClientOpenStatus.Failed, null, error);
}

public interface IBrokerClient
{
    Task<BrokerClientOpenResult> OpenAsync(
        string requestedCapability,
        IProgress<BrokerLaunchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ResolvedBrokerExecutable(string AbsolutePath, string Sha256);

public interface IBrokerExecutableResolver
{
    ResolvedBrokerExecutable Resolve();
}

public sealed record BrokerProcessLaunch(
    string PipeName,
    Guid SessionId,
    BrokerProcessIdentity ClientIdentity,
    string ClientSha256,
    string RequestedCapability,
    DateTimeOffset ExpiresAtUtc);

public sealed record BrokerLaunchResult(
    bool Succeeded,
    bool WasCancelled,
    System.Diagnostics.Process? Process,
    BrokerStructuredError? Error);

public interface IUacBrokerLauncher
{
    Task<BrokerLaunchResult> LaunchAsync(
        ResolvedBrokerExecutable executable,
        BrokerProcessLaunch launch,
        CancellationToken cancellationToken);
}
