using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kernctl.Broker.Protocol;

namespace Kernctl.Broker;

public sealed record ClientIdentityVerification(
    bool Succeeded,
    BrokerProcessIdentity? ActualIdentity,
    BrokerStructuredError? Error);

public interface IClientIdentityVerifier
{
    ClientIdentityVerification Verify(
        NamedPipeServerStream pipe,
        BrokerLaunchOptions launch,
        BrokerHandshakeRequest handshake);
}

public interface IExecutableTrustVerifier
{
    ExecutableTrustResult Verify(string executablePath);
}

public sealed record ExecutableTrustResult(
    bool IsSignedAndTrusted,
    string? SignerThumbprint);

public sealed class ClientIdentityVerifier(
    IExecutableTrustVerifier executableTrustVerifier) : IClientIdentityVerifier
{
    public ClientIdentityVerification Verify(
        NamedPipeServerStream pipe,
        BrokerLaunchOptions launch,
        BrokerHandshakeRequest handshake)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return Rejected("The administrator broker is only supported on Windows.");
            }

            var actualPid = checked((int)WindowsNative.GetPipeClientProcessId(pipe.SafePipeHandle));
            var actualSession = checked((int)WindowsNative.GetPipeClientSessionId(pipe.SafePipeHandle));
            using var process = WindowsNative.OpenProcessForIdentity(actualPid);
            var actualPath = Path.GetFullPath(WindowsNative.QueryProcessPath(process));
            var actualStartTicks = WindowsNative.QueryProcessStartUtcTicks(process);
            var actualUserSid = WindowsNative.QueryProcessUserSid(process);
            var actual = new BrokerProcessIdentity(
                actualPid,
                actualStartTicks,
                actualSession,
                actualPath,
                actualUserSid);

            if (actual.ProcessId != launch.ExpectedClient.ProcessId
                || actual.ProcessStartUtcTicks != launch.ExpectedClient.ProcessStartUtcTicks
                || actual.WindowsSessionId != launch.ExpectedClient.WindowsSessionId
                || !PathEquals(actual.ExecutablePath, launch.ExpectedClient.ExecutablePath)
                || !string.Equals(
                    actual.UserSid,
                    launch.ExpectedClient.UserSid,
                    StringComparison.Ordinal)
                || !IdentitiesMatch(actual, handshake.Client)
                || handshake.SessionId != launch.SessionId
                || handshake.RequestedCapability != launch.RequestedCapability
                || handshake.ExpiresAtUtc != launch.ExpiresAtUtc)
            {
                return Rejected("The connecting application identity did not match the UAC request.");
            }

            using var executable = new FileStream(
                actual.ExecutablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(executable));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(launch.ExpectedClientSha256)))
            {
                return Rejected("The connecting application executable changed during elevation.");
            }

            var brokerPath = Path.GetFullPath(
                Environment.ProcessPath
                ?? throw new InvalidOperationException("The broker executable path is unavailable."));
            if (!string.Equals(
                    Path.GetFileName(actual.ExecutablePath),
                    "Kernctl.App.exe",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(brokerPath),
                    "Kernctl.Broker.exe",
                    StringComparison.OrdinalIgnoreCase)
                || !PathEquals(
                    Path.GetDirectoryName(actual.ExecutablePath)!,
                    Path.GetDirectoryName(brokerPath)!))
            {
                return Rejected("The application and administrator broker are not in a trusted layout.");
            }

            var clientTrust = executableTrustVerifier.Verify(actual.ExecutablePath);
            var brokerTrust = executableTrustVerifier.Verify(brokerPath);
            if (!IsTrustPairAccepted(clientTrust, brokerTrust))
            {
                return Rejected("Executable signature verification failed.");
            }

            return new(true, actual, null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or CryptographicException
                or InvalidOperationException
                or OverflowException
                or FormatException)
        {
            return Rejected("The connecting application identity could not be verified.");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IdentitiesMatch(
        BrokerProcessIdentity left,
        BrokerProcessIdentity right) =>
        left.ProcessId == right.ProcessId
        && left.ProcessStartUtcTicks == right.ProcessStartUtcTicks
        && left.WindowsSessionId == right.WindowsSessionId
        && PathEquals(left.ExecutablePath, right.ExecutablePath)
        && string.Equals(left.UserSid, right.UserSid, StringComparison.Ordinal);

    private static ClientIdentityVerification Rejected(string message) =>
        new(
            false,
            null,
            new(
                BrokerErrorCodes.ClientIdentityRejected,
                message,
                RetryPossible: false));

    internal static bool IsTrustPairAccepted(
        ExecutableTrustResult client,
        ExecutableTrustResult broker)
    {
#if DEBUG
        if (!client.IsSignedAndTrusted && !broker.IsSignedAndTrusted)
        {
            return true;
        }
#endif
        return client.IsSignedAndTrusted
            && broker.IsSignedAndTrusted
            && !string.IsNullOrWhiteSpace(client.SignerThumbprint)
            && string.Equals(
                client.SignerThumbprint,
                broker.SignerThumbprint,
                StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AuthenticodeExecutableTrustVerifier : IExecutableTrustVerifier
{
    public ExecutableTrustResult Verify(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(executablePath))
        {
            return new(false, null);
        }

        var trusted = WinTrustNative.IsTrusted(executablePath);
        if (!trusted)
        {
            return new(false, null);
        }

        try
        {
#pragma warning disable SYSLIB0057 // WinTrust validates integrity; this API only reads the embedded signer.
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
            return new(
                true,
                Convert.ToHexString(SHA256.HashData(certificate.RawData)));
        }
        catch (CryptographicException)
        {
            return new(false, null);
        }
    }
}

internal static partial class WinTrustNative
{
    private static readonly Guid GenericVerifyAction =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool IsTrusted(string path)
    {
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UserInterfaceChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 1,
                ProviderFlags = 0x00000080,
                UserInterfaceContext = 0,
            };
            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyAction, ref data);
            data.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, GenericVerifyAction, ref data);
            return result == 0;
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UserInterfaceChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UserInterfaceContext;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust", SetLastError = true)]
    private static partial int WinVerifyTrust(
        IntPtr windowHandle,
        in Guid actionId,
        ref WinTrustData trustData);
}
