using System.Runtime.InteropServices;
using Kernctl.Core.Models;
using Kernctl.Core.Services;

namespace Kernctl.Platform.Windows;

/// <summary>Reads CPU and physical-memory load through documented read-only Win32 APIs.</summary>
public sealed class WindowsSystemMetricsService : ISystemMetricsService, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ulong previousIdle;
    private ulong previousKernel;
    private ulong previousUser;

    public async ValueTask<SystemMetricsSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                throw new InvalidOperationException("Windows CPU metrics are unavailable.");
            }

            var idleValue = idle.ToUInt64();
            var kernelValue = kernel.ToUInt64();
            var userValue = user.ToUInt64();
            var cpu = 0;
            if (previousKernel != 0 || previousUser != 0)
            {
                var totalDelta = kernelValue - previousKernel + userValue - previousUser;
                var idleDelta = idleValue - previousIdle;
                cpu = totalDelta == 0
                    ? 0
                    : (int)Math.Round(Math.Clamp(
                        (double)(totalDelta - idleDelta) / totalDelta * 100,
                        0,
                        100));
            }

            previousIdle = idleValue;
            previousKernel = kernelValue;
            previousUser = userValue;
            var memory = new MemoryStatus
            {
                Length = (uint)Marshal.SizeOf<MemoryStatus>(),
            };
            if (!GlobalMemoryStatusEx(ref memory))
            {
                throw new InvalidOperationException("Windows memory metrics are unavailable.");
            }

            return new(cpu, (int)memory.MemoryLoad, "Windows", false, DateTimeOffset.UtcNow);
        }
        finally
        {
            gate.Release();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    public void Dispose() => gate.Dispose();
}
