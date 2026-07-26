using System.Security.Principal;

namespace Kernctl.Platform.Windows;

public interface IWindowsBrokerDiagnostics
{
    bool IsElevated();
}

public sealed class WindowsBrokerDiagnostics : IWindowsBrokerDiagnostics
{
    public bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
