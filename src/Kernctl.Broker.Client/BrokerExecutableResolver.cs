using System.Security.Cryptography;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Client;

public sealed class BrokerExecutableResolver : IBrokerExecutableResolver
{
    public ResolvedBrokerExecutable Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new BrokerClientException(
                BrokerErrorCodes.UnsupportedPlatform,
                "Administrator operations are available only on Windows.");
        }

        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (applicationDirectory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new BrokerClientException(
                BrokerErrorCodes.BrokerUnavailable,
                "The administrator broker requires a local application installation.");
        }

        var candidate = Path.GetFullPath(
            Path.Combine(applicationDirectory, "Kernctl.Broker.exe"));
        if (!string.Equals(
                Path.GetDirectoryName(candidate),
                Path.TrimEndingDirectorySeparator(applicationDirectory),
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate))
        {
            throw new BrokerClientException(
                BrokerErrorCodes.BrokerUnavailable,
                "The administrator broker is missing from the application installation.");
        }

        using var stream = new FileStream(
            candidate,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return new(candidate, Convert.ToHexString(SHA256.HashData(stream)));
    }
}

public sealed class BrokerClientException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
