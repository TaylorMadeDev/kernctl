using System.Diagnostics;
using System.Runtime.InteropServices;
using Kernctl.Core.Gaming;

namespace Kernctl.Platform.Windows;

public sealed class WindowsGameProcessService : IGameProcessService
{
    public Task<GameProcessReference?> FindRunningAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = GameValidation.NormalizeIdentityPath(executablePath);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var candidate = process.MainModule?.FileName;
                    if (candidate is not null
                        && string.Equals(
                            GameValidation.NormalizeIdentityPath(candidate),
                            normalized,
                            StringComparison.Ordinal))
                    {
                        return Task.FromResult<GameProcessReference?>(
                            CreateReference(process, candidate));
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                {
                    // A protected or exiting process is not a launch candidate.
                }
            }
        }

        return Task.FromResult<GameProcessReference?>(null);
    }

    public Task<GameProcessReference> LaunchAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = GameValidation.ValidateLaunch(
            executablePath,
            workingDirectory,
            arguments);
        if (!validation.IsValid)
        {
            throw new GameLaunchException(validation.Errors[0]);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = validation.NormalizedExecutablePath!,
            WorkingDirectory = validation.NormalizedWorkingDirectory!,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new GameLaunchException("Windows did not create the game process.");
            using (process)
            {
                return Task.FromResult(CreateReference(process, validation.NormalizedExecutablePath!));
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or FileNotFoundException)
        {
            throw new GameLaunchException(
                "Windows could not start the selected executable.",
                exception);
        }
    }

    public Task<bool> IsRunningAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryOpenExact(process, out var opened) && DisposeAsTrue(opened));
    }

    public Task<GameProcessPriority?> GetPriorityAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOpenExact(process, out var opened))
        {
            return Task.FromResult<GameProcessPriority?>(null);
        }

        using (opened)
        {
            try
            {
                return Task.FromResult<GameProcessPriority?>(opened.PriorityClass switch
                {
                    ProcessPriorityClass.Normal => GameProcessPriority.Normal,
                    ProcessPriorityClass.AboveNormal => GameProcessPriority.AboveNormal,
                    ProcessPriorityClass.High => GameProcessPriority.High,
                    _ => null,
                });
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
                return Task.FromResult<GameProcessPriority?>(null);
            }
        }
    }

    public Task<GameProcessOperationResult> SetPriorityAsync(
        GameProcessReference process,
        GameProcessPriority priority,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GameValidation.IsAllowedPriority(priority))
        {
            return Task.FromResult(
                GameProcessOperationResult.Failure("Realtime and unknown priorities are not allowed."));
        }

        if (!TryOpenExact(process, out var opened))
        {
            return Task.FromResult(
                GameProcessOperationResult.Failure("The game process exited before its priority could be changed."));
        }

        using (opened)
        {
            try
            {
                opened.PriorityClass = priority switch
                {
                    GameProcessPriority.Normal => ProcessPriorityClass.Normal,
                    GameProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
                    GameProcessPriority.High => ProcessPriorityClass.High,
                    _ => throw new InvalidOperationException("Unsupported process priority."),
                };
                return Task.FromResult(
                    GameProcessOperationResult.Success($"Set the game process to {priority} priority."));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
            {
                return Task.FromResult(
                    GameProcessOperationResult.Failure(
                        $"Windows denied the process priority change: {exception.Message}"));
            }
        }
    }

    public Task<GameProcessOperationResult> RequestCloseAsync(
        GameProcessReference process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOpenExact(process, out var opened))
        {
            return Task.FromResult(
                GameProcessOperationResult.Failure("The process is no longer running."));
        }

        using (opened)
        {
            try
            {
                return Task.FromResult(opened.CloseMainWindow()
                    ? GameProcessOperationResult.Success("Windows was asked to close the application.")
                    : GameProcessOperationResult.Failure(
                        "The application has no responsive main window. kernctl did not force terminate it."));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
                return Task.FromResult(
                    GameProcessOperationResult.Failure(
                        $"Windows denied the close request: {exception.Message}"));
            }
        }
    }

    internal static bool TryOpenExact(
        GameProcessReference reference,
        out Process process)
    {
        process = null!;
        try
        {
            var candidate = Process.GetProcessById(reference.ProcessId);
            var start = new DateTimeOffset(candidate.StartTime.ToUniversalTime());
            if ((start - reference.StartedAtUtc).Duration() > TimeSpan.FromSeconds(1))
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return !candidate.HasExited;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return false;
        }
    }

    private static GameProcessReference CreateReference(Process process, string executablePath) =>
        new(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime()),
            executablePath);

    private static bool DisposeAsTrue(Process process)
    {
        process.Dispose();
        return true;
    }
}

public sealed class WindowsGameProcessTreeMonitor : IGameProcessTreeMonitor
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LauncherGracePeriod = TimeSpan.FromSeconds(5);

    public async Task<GameProcessTreeResult> MonitorAsync(
        GameProcessReference root,
        Action<GameProcessMetrics>? metricsChanged = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var knownProcessIds = new HashSet<int> { root.ProcessId };
        var previousCpu = new Dictionary<int, TimeSpan>();
        var samples = new List<double>();
        long peakWorkingSet = 0;
        DateTimeOffset? allExitedAt = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = ProcessTreeSnapshot.Capture();
            AddDescendants(root.ProcessId, snapshot.ParentByProcessId, knownProcessIds);
            var running = new List<Process>();
            foreach (var processId in knownProcessIds)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        running.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                {
                    // A process can exit between the read-only snapshot and this sample.
                }
            }

            if (running.Count == 0)
            {
                allExitedAt ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - allExitedAt >= LauncherGracePeriod)
                {
                    break;
                }
            }
            else
            {
                allExitedAt = null;
                var totalCpuDelta = TimeSpan.Zero;
                long workingSet = 0;
                foreach (var process in running)
                {
                    using (process)
                    {
                        try
                        {
                            var totalCpu = process.TotalProcessorTime;
                            if (previousCpu.TryGetValue(process.Id, out var prior))
                            {
                                totalCpuDelta += totalCpu - prior;
                            }

                            previousCpu[process.Id] = totalCpu;
                            workingSet += process.WorkingSet64;
                        }
                        catch (Exception exception) when (
                            exception is InvalidOperationException
                                or System.ComponentModel.Win32Exception)
                        {
                            // Protected or exiting descendants are omitted from this sample.
                        }
                    }
                }

                var cpuPercent = Math.Clamp(
                    totalCpuDelta.TotalSeconds / SampleInterval.TotalSeconds
                    / Math.Max(1, Environment.ProcessorCount)
                    * 100,
                    0,
                    100);
                samples.Add(cpuPercent);
                peakWorkingSet = Math.Max(peakWorkingSet, workingSet);
                var priority = await GetDisplayPriorityAsync(root, cancellationToken);
                metricsChanged?.Invoke(new(
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow - startedAt,
                    cpuPercent,
                    workingSet,
                    priority,
                    running.Count));
            }

            await Task.Delay(SampleInterval, cancellationToken);
        }

        return new(
            DateTimeOffset.UtcNow - startedAt,
            peakWorkingSet,
            samples.Count == 0 ? 0 : samples.Average(),
            "The configured process tree exited.");
    }

    private static async Task<GameProcessPriority> GetDisplayPriorityAsync(
        GameProcessReference root,
        CancellationToken cancellationToken)
    {
        var service = new WindowsGameProcessService();
        return await service.GetPriorityAsync(root, cancellationToken)
            ?? GameProcessPriority.Normal;
    }

    private static void AddDescendants(
        int rootProcessId,
        IReadOnlyDictionary<int, int> parents,
        HashSet<int> known)
    {
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);
        while (queue.TryDequeue(out var parent))
        {
            foreach (var child in parents.Where(pair => pair.Value == parent).Select(pair => pair.Key))
            {
                if (known.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    private sealed record ProcessTreeSnapshot(IReadOnlyDictionary<int, int> ParentByProcessId)
    {
        private const uint SnapshotProcesses = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new(-1);

        public static ProcessTreeSnapshot Capture()
        {
            var values = new Dictionary<int, int>();
            var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == InvalidHandleValue)
            {
                return new(values);
            }

            try
            {
                var entry = new ProcessEntry32
                {
                    Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                };
                if (!Process32First(snapshot, ref entry))
                {
                    return new(values);
                }

                do
                {
                    values[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                }
                while (Process32Next(snapshot, ref entry));

                return new(values);
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
