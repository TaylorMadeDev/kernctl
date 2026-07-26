using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker.Client;

public sealed class WindowsUacBrokerLauncher : IUacBrokerLauncher
{
    private const int UacCancelledError = 1223;

    public async Task<BrokerLaunchResult> LaunchAsync(
        ResolvedBrokerExecutable executable,
        BrokerProcessLaunch launch,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failed(
                BrokerErrorCodes.UnsupportedPlatform,
                "Administrator operations are available only on Windows.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.AbsolutePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable.AbsolutePath)!,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        AddArguments(startInfo, launch);

        try
        {
            var startTask = Task.Run(
                () => Process.Start(startInfo),
                CancellationToken.None);
            var completed = await Task.WhenAny(
                startTask,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            if (completed != startTask)
            {
                _ = ObserveLateLaunchAsync(startTask);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var process = await startTask;
            if (process is null)
            {
                return Failed(
                    BrokerErrorCodes.LaunchFailed,
                    "The administrator broker could not be started.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                process.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new(true, false, process, null);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledError)
        {
            return new(
                false,
                true,
                null,
                new(
                    BrokerErrorCodes.ElevationCancelled,
                    "Administrator permission was declined. No changes were made.",
                    RetryPossible: true));
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or FileNotFoundException)
        {
            return Failed(
                BrokerErrorCodes.LaunchFailed,
                "The administrator broker could not be started.");
        }
    }

    private static void AddArguments(
        ProcessStartInfo startInfo,
        BrokerProcessLaunch launch)
    {
        Add(startInfo, "--pipe", launch.PipeName);
        Add(startInfo, "--session", launch.SessionId.ToString("N"));
        Add(
            startInfo,
            "--client-pid",
            launch.ClientIdentity.ProcessId.ToString(CultureInfo.InvariantCulture));
        Add(
            startInfo,
            "--client-start-utc-ticks",
            launch.ClientIdentity.ProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture));
        Add(
            startInfo,
            "--client-session",
            launch.ClientIdentity.WindowsSessionId.ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "--client-path", launch.ClientIdentity.ExecutablePath);
        Add(startInfo, "--client-sid", launch.ClientIdentity.UserSid);
        Add(startInfo, "--client-sha256", launch.ClientSha256);
        Add(startInfo, "--capability", launch.RequestedCapability);
        Add(
            startInfo,
            "--expires-utc-ticks",
            launch.ExpiresAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private static void Add(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static BrokerLaunchResult Failed(string code, string message) =>
        new(
            false,
            false,
            null,
            new(code, message, RetryPossible: true));

    private static async Task ObserveLateLaunchAsync(Task<Process?> startTask)
    {
        try
        {
            using var process = await startTask;
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or FileNotFoundException)
        {
        }
    }
}
