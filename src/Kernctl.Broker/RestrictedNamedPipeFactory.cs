using System.IO.Pipes;

namespace Kernctl.Broker;

public interface IRestrictedNamedPipeFactory
{
    NamedPipeServerStream Create(string pipeName, string clientUserSid);
}

public sealed class RestrictedNamedPipeFactory : IRestrictedNamedPipeFactory
{
    public NamedPipeServerStream Create(string pipeName, string clientUserSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The elevated broker requires Windows.");
        }

        var handle = WindowsNative.CreateRestrictedPipe(pipeName, clientUserSid);
        try
        {
            return new NamedPipeServerStream(
                PipeDirection.InOut,
                isAsync: true,
                isConnected: false,
                handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
