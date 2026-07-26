using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Client;

public sealed record CurrentClientIdentity(BrokerProcessIdentity Identity, string Sha256);

public interface ICurrentProcessIdentityProvider
{
    CurrentClientIdentity GetCurrent();
}

public sealed class CurrentProcessIdentityProvider : ICurrentProcessIdentityProvider
{
    public CurrentClientIdentity GetCurrent()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new BrokerClientException(
                BrokerErrorCodes.UnsupportedPlatform,
                "Administrator operations are available only on Windows.");
        }

        var path = Path.GetFullPath(
            Environment.ProcessPath
            ?? throw new BrokerClientException(
                BrokerErrorCodes.BrokerUnavailable,
                "The application executable identity is unavailable."));
        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new BrokerClientException(
                BrokerErrorCodes.BrokerUnavailable,
                "The current Windows user identity is unavailable.");
        using var executable = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(executable));
        return new(
            new(
                Environment.ProcessId,
                new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks,
                process.SessionId,
                path,
                sid),
            hash);
    }
}
