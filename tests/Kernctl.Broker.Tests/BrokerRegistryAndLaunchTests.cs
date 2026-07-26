using Kernctl.Broker.Client;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Tests;

public sealed class BrokerRegistryAndLaunchTests
{
    [Fact]
    public void ProductionRegistryContainsOnlyExactNonMutatingDiagnostics()
    {
        var registry = BrokerOperationRegistry.CreateDiagnostics();

        Assert.Equal(
            BrokerOperationIds.Diagnostics.OrderBy(value => value, StringComparer.Ordinal),
            registry.Operations
                .Select(operation => operation.Descriptor.OperationId)
                .OrderBy(value => value, StringComparer.Ordinal));
        Assert.All(registry.Operations, operation =>
        {
            Assert.False(operation.Descriptor.MutatesState);
            Assert.Equal(BrokerRiskClassification.Diagnostic, operation.Descriptor.Risk);
        });
        Assert.False(registry.TryGet("broker.run-command", out _));
        Assert.False(registry.TryGet("BROKER.PING", out _));
    }

    [Fact]
    public void LaunchArgumentParserRejectsUnknownKeysAndInvalidPipeNames()
    {
        var arguments = ValidArguments();
        var unknown = arguments.Concat(["--command", "whoami"]).ToArray();
        var invalidPipe = arguments.ToArray();
        invalidPipe[1] = "predictable";

        Assert.False(BrokerLaunchOptions.TryParse(unknown, out _, out _));
        Assert.False(BrokerLaunchOptions.TryParse(invalidPipe, out _, out _));
    }

    [Fact]
    public async Task UacCancellationIsMappedToNormalClientCancellation()
    {
        var client = new BrokerClient(
            new FakeResolver(),
            new FakeIdentityProvider(),
            new CancelledLauncher(),
            BrokerClientOptions.Default);
        var progress = new List<BrokerLaunchProgress>();

        var result = await client.OpenAsync(
            "diagnostics",
            new InlineProgress(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerClientOpenStatus.Cancelled, result.Status);
        Assert.Equal(BrokerErrorCodes.ElevationCancelled, result.Error?.Code);
        Assert.Contains(progress, update =>
            update.Stage == BrokerLaunchStage.PermissionDeclined);
    }

    [Fact]
    public void ExecutableTrustPolicyRejectsSignerMismatchAndReleaseRejectsUnsignedPair()
    {
        Assert.False(ClientIdentityVerifier.IsTrustPairAccepted(
            new(true, "AAAA"),
            new(true, "BBBB")));

        var unsignedAccepted = ClientIdentityVerifier.IsTrustPairAccepted(
            new(false, null),
            new(false, null));
#if DEBUG
        Assert.True(unsignedAccepted);
#else
        Assert.False(unsignedAccepted);
#endif
    }

    private static string[] ValidArguments()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            "--pipe",
            $"kernctl-{Guid.NewGuid():N}-{Guid.NewGuid():N}",
            "--session",
            Guid.NewGuid().ToString("N"),
            "--client-pid",
            "42",
            "--client-start-utc-ticks",
            now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--client-session",
            "1",
            "--client-path",
            @"C:\kernctl\Kernctl.App.exe",
            "--client-sid",
            "S-1-5-21-1",
            "--client-sha256",
            new string('A', 64),
            "--capability",
            "diagnostics",
            "--expires-utc-ticks",
            now.AddMinutes(1).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        ];
    }

    private sealed class FakeResolver : IBrokerExecutableResolver
    {
        public ResolvedBrokerExecutable Resolve() =>
            new(@"C:\kernctl\Kernctl.Broker.exe", new string('A', 64));
    }

    private sealed class FakeIdentityProvider : ICurrentProcessIdentityProvider
    {
        public CurrentClientIdentity GetCurrent()
        {
            var now = DateTimeOffset.UtcNow;
            return new(
                new(
                    Environment.ProcessId,
                    now.UtcTicks,
                    1,
                    @"C:\kernctl\Kernctl.App.exe",
                    "S-1-5-21-1"),
                new string('B', 64));
        }
    }

    private sealed class CancelledLauncher : IUacBrokerLauncher
    {
        public Task<BrokerLaunchResult> LaunchAsync(
            ResolvedBrokerExecutable executable,
            BrokerProcessLaunch launch,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BrokerLaunchResult(
                false,
                true,
                null,
                new(
                    BrokerErrorCodes.ElevationCancelled,
                    "Administrator permission was declined. No changes were made.",
                    RetryPossible: true)));
    }

    private sealed class InlineProgress(Action<BrokerLaunchProgress> callback)
        : IProgress<BrokerLaunchProgress>
    {
        public void Report(BrokerLaunchProgress value) => callback(value);
    }
}
