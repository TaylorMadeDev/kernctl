using System.ComponentModel;
using System.Runtime.InteropServices;
using Kernctl.Core.Profiles;

namespace Kernctl.Platform.Windows;

public sealed class WindowsPowerSchemeService : IPowerSchemeService
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;

    public Task<Guid> GetActiveSchemeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var result = PowerGetActiveScheme(IntPtr.Zero, out var schemePointer);
        if (result != ErrorSuccess)
        {
            throw CreateError("read the active power scheme", result);
        }

        try
        {
            return Task.FromResult(Marshal.PtrToStructure<Guid>(schemePointer));
        }
        finally
        {
            _ = LocalFree(schemePointer);
        }
    }

    public Task<bool> IsSchemeAvailableAsync(
        KnownPowerScheme scheme,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        var schemeId = GetSchemeId(scheme);
        uint bufferSize = 0;
        var result = PowerReadFriendlyName(
            IntPtr.Zero,
            ref schemeId,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            ref bufferSize);
        return Task.FromResult(result is ErrorSuccess or ErrorMoreData && bufferSize > 0);
    }

    public Task SetActiveSchemeAsync(
        Guid schemeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var result = PowerSetActiveScheme(IntPtr.Zero, ref schemeId);
        if (result != ErrorSuccess)
        {
            throw CreateError("set the active power scheme", result);
        }

        return Task.CompletedTask;
    }

    public Guid GetSchemeId(KnownPowerScheme scheme) => PowerSchemeIds.For(scheme);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows power schemes require Windows.");
        }
    }

    private static InvalidOperationException CreateError(string operation, uint result) =>
        new($"Windows could not {operation}: {new Win32Exception((int)result).Message} ({result}).");

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerGetActiveScheme(
        IntPtr userRootPowerKey,
        out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerSetActiveScheme(
        IntPtr userRootPowerKey,
        ref Guid schemeGuid);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subgroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
