using Kernctl.Platform.Windows;

namespace Kernctl.Broker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!BrokerLaunchOptions.TryParse(args, out var launch, out _)
            || launch is null)
        {
            return 2;
        }

        var host = new BrokerHost(
            new RestrictedNamedPipeFactory(),
            new ClientIdentityVerifier(new AuthenticodeExecutableTrustVerifier()),
            BrokerOperationRegistry.CreateDiagnostics(),
            new WindowsBrokerDiagnostics(),
            new EventSourceBrokerAuditSink(),
            BrokerHostOptions.Production);
        return await host.RunAsync(launch, CancellationToken.None);
    }
}
