using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Kernctl.Broker;

[SupportedOSPlatform("windows")]
internal static partial class WindowsNative
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserInformationClass = 1;
    private const int SecurityDescriptorRevision = 1;

    public static SafePipeHandle CreateRestrictedPipe(string pipeName, string clientUserSid)
    {
        _ = new SecurityIdentifier(clientUserSid);
        var sddl = $"D:P(A;;GRGW;;;{clientUserSid})(A;;GA;;;SY)(A;;GA;;;BA)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SecurityDescriptorRevision,
                out var descriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            var handle = CreateNamedPipe(
                $@"\\.\pipe\{pipeName}",
                PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
                PipeRejectRemoteClients,
                1,
                Protocol.BrokerProtocol.MaximumFrameBytes,
                Protocol.BrokerProtocol.MaximumFrameBytes,
                0,
                ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            return handle;
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    public static uint GetPipeClientProcessId(SafePipeHandle pipeHandle)
    {
        if (!GetNamedPipeClientProcessId(pipeHandle, out var processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return processId;
    }

    public static uint GetPipeClientSessionId(SafePipeHandle pipeHandle)
    {
        if (!GetNamedPipeClientSessionId(pipeHandle, out var sessionId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return sessionId;
    }

    public static bool RejectsRemoteClients(SafePipeHandle pipeHandle)
    {
        if (!GetNamedPipeInfo(
                pipeHandle,
                out var flags,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (flags & PipeRejectRemoteClients) != 0;
    }

    public static SafeProcessHandle OpenProcessForIdentity(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    public static string QueryProcessPath(SafeProcessHandle processHandle)
    {
        var capacity = 32768;
        var buffer = new char[capacity];
        unsafe
        {
            fixed (char* bufferPointer = buffer)
            {
                if (!QueryFullProcessImageName(
                        processHandle,
                        0,
                        bufferPointer,
                        ref capacity))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        return new string(buffer, 0, capacity);
    }

    public static long QueryProcessStartUtcTicks(SafeProcessHandle processHandle)
    {
        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return DateTimeOffset.FromFileTime(creationTime).UtcTicks;
    }

    public static string QueryProcessUserSid(SafeProcessHandle processHandle)
    {
        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (tokenHandle)
        {
            _ = GetTokenInformation(
                tokenHandle,
                TokenUserInformationClass,
                IntPtr.Zero,
                0,
                out var bytes);
            var error = Marshal.GetLastWin32Error();
            if (bytes <= 0 || error != 122)
            {
                throw new Win32Exception(error);
            }

            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!GetTokenInformation(
                        tokenHandle,
                        TokenUserInformationClass,
                        buffer,
                        bytes,
                        out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
                return new SecurityIdentifier(tokenUser.User.Sid).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        int stringSDRevision,
        out IntPtr securityDescriptor,
        out int securityDescriptorSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        int outputBufferSize,
        int inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientSessionId(
        SafePipeHandle pipe,
        out uint clientSessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeInfo(
        SafePipeHandle pipe,
        out uint flags,
        out uint outputBufferSize,
        out uint inputBufferSize,
        out uint maximumInstances);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        char* executableName,
        ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
