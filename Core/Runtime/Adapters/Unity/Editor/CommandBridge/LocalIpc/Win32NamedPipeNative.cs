#if !GODOT
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YokiFrame.Unity
{
    internal static class Win32NamedPipeNative
    {
        internal static readonly IntPtr InvalidHandleValue =
            new IntPtr(-1);

        internal const uint PipeAccessDuplex = 0x00000003;
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagFirstPipeInstance = 0x00080000;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const uint PipeTypeByte = 0x00000000;
        internal const uint PipeReadModeByte = 0x00000000;
        internal const uint PipeWait = 0x00000000;
        internal const uint PipeRejectRemoteClients = 0x00000008;
        internal const uint SddlRevision1 = 1;
        internal const uint TokenQuery = 0x0008;
        internal const int TokenUser = 1;

        internal const int ErrorAccessDenied = 5;
        internal const int ErrorInvalidHandle = 6;
        internal const int ErrorBrokenPipe = 109;
        internal const int ErrorInsufficientBuffer = 122;
        internal const int ErrorNoData = 232;
        internal const int ErrorPipeBusy = 231;
        internal const int ErrorPipeConnected = 535;
        internal const int ErrorOperationAborted = 995;
        internal const int ErrorIoPending = 997;
        internal const uint WaitObject0 = 0;
        internal const uint WaitFailed = 0xFFFFFFFF;
        internal const uint Infinite = 0xFFFFFFFF;

        internal static IntPtr CreateCurrentUserOnlyPipe(
            string pipeName,
            bool firstInstance)
        {
            var sddl =
                "D:P(A;;GA;;;" + GetCurrentUserSidString() + ")";
            IntPtr descriptor;
            uint descriptorLength;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    sddl,
                    SddlRevision1,
                    out descriptor,
                    out descriptorLength))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to create the current-user pipe DACL.");
            }

            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                    SecurityDescriptor = descriptor,
                    InheritHandle = 0
                };
                var openMode =
                    PipeAccessDuplex |
                    FileFlagOverlapped;
                if (firstInstance)
                    openMode |= FileFlagFirstPipeInstance;

                var handle = CreateNamedPipe(
                    @"\\.\pipe\" + pipeName,
                    openMode,
                    PipeTypeByte |
                    PipeReadModeByte |
                    PipeWait |
                    PipeRejectRemoteClients,
                    LocalIpcProtocol.MaxActiveClients + 1,
                    64 * 1024,
                    64 * 1024,
                    0,
                    ref attributes);
                if (handle == InvalidHandleValue)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "CreateNamedPipe failed for " + pipeName + ".");
                }

                return handle;
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        internal static bool IsDisconnectError(int error)
        {
            return error == ErrorBrokenPipe ||
                   error == ErrorNoData ||
                   error == ErrorInvalidHandle ||
                   error == ErrorOperationAborted;
        }

        internal static bool ConnectOverlapped(
            IntPtr handle,
            out int error)
        {
            error = 0;
            using (var operation = new OverlappedOperation())
            {
                if (ConnectNamedPipe(
                        handle,
                        ref operation.Value))
                {
                    return true;
                }

                error = Marshal.GetLastWin32Error();
                if (error == ErrorPipeConnected)
                    return true;
                if (error != ErrorIoPending)
                    return false;

                uint transferred;
                return operation.TryComplete(
                    handle,
                    out transferred,
                    out error);
            }
        }

        internal static bool ReadOverlapped(
            IntPtr handle,
            byte[] buffer,
            int offset,
            uint bytesToRead,
            out uint bytesRead,
            out int error)
        {
            return TransferOverlapped(
                handle,
                buffer,
                offset,
                bytesToRead,
                false,
                out bytesRead,
                out error);
        }

        internal static bool WriteOverlapped(
            IntPtr handle,
            byte[] buffer,
            int offset,
            uint bytesToWrite,
            out uint bytesWritten,
            out int error)
        {
            return TransferOverlapped(
                handle,
                buffer,
                offset,
                bytesToWrite,
                true,
                out bytesWritten,
                out error);
        }

        internal static bool TryWakeListener(string pipeName)
        {
            if (string.IsNullOrEmpty(pipeName))
                return false;

            var client = CreateFile(
                @"\\.\pipe\" + pipeName,
                GenericRead | GenericWrite,
                0,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (client == InvalidHandleValue)
                return false;

            CloseHandle(client);
            return true;
        }

        internal static void CloseIfValid(IntPtr handle)
        {
            if (handle == IntPtr.Zero ||
                handle == InvalidHandleValue)
            {
                return;
            }

            CancelIfValid(handle);
            CloseHandle(handle);
        }

        internal static void CancelIfValid(IntPtr handle)
        {
            if (handle == IntPtr.Zero ||
                handle == InvalidHandleValue)
            {
                return;
            }

            CancelIoEx(handle, IntPtr.Zero);
        }

        private static bool TransferOverlapped(
            IntPtr handle,
            byte[] buffer,
            int offset,
            uint byteCount,
            bool write,
            out uint transferred,
            out int error)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 ||
                offset > buffer.Length ||
                byteCount > checked((uint)(buffer.Length - offset)))
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            transferred = 0;
            error = 0;
            var pinned = GCHandle.Alloc(
                buffer,
                GCHandleType.Pinned);
            try
            {
                using (var operation = new OverlappedOperation())
                {
                    var bufferAddress = IntPtr.Add(
                        pinned.AddrOfPinnedObject(),
                        offset);
                    var completedSynchronously = write
                        ? WriteFile(
                            handle,
                            bufferAddress,
                            byteCount,
                            out transferred,
                            ref operation.Value)
                        : ReadFile(
                            handle,
                            bufferAddress,
                            byteCount,
                            out transferred,
                            ref operation.Value);
                    if (completedSynchronously)
                        return true;

                    error = Marshal.GetLastWin32Error();
                    if (error != ErrorIoPending)
                        return false;

                    return operation.TryComplete(
                        handle,
                        out transferred,
                        out error);
                }
            }
            finally
            {
                pinned.Free();
            }
        }

        private static string GetCurrentUserSidString()
        {
            IntPtr token;
            if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TokenQuery,
                    out token))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "OpenProcessToken failed.");
            }

            try
            {
                uint requiredLength;
                GetTokenInformation(
                    token,
                    TokenUser,
                    IntPtr.Zero,
                    0,
                    out requiredLength);
                var lengthError = Marshal.GetLastWin32Error();
                if (requiredLength == 0 ||
                    lengthError != ErrorInsufficientBuffer)
                {
                    throw new Win32Exception(
                        lengthError,
                        "GetTokenInformation did not report the required size.");
                }

                var tokenInformation =
                    Marshal.AllocHGlobal(checked((int)requiredLength));
                try
                {
                    if (!GetTokenInformation(
                            token,
                            TokenUser,
                            tokenInformation,
                            requiredLength,
                            out requiredLength))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "GetTokenInformation failed.");
                    }

                    var tokenUser =
                        (TokenUserValue)Marshal.PtrToStructure(
                            tokenInformation,
                            typeof(TokenUserValue));
                    IntPtr sidString;
                    if (!ConvertSidToStringSid(
                            tokenUser.User.Sid,
                            out sidString))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "ConvertSidToStringSid failed.");
                    }

                    try
                    {
                        var value = Marshal.PtrToStringUni(sidString);
                        if (string.IsNullOrEmpty(value))
                        {
                            throw new InvalidOperationException(
                                "Current user SID is empty.");
                        }

                        return value;
                    }
                    finally
                    {
                        LocalFree(sidString);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(tokenInformation);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr SecurityDescriptor;
            internal int InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            internal IntPtr Sid;
            internal uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenUserValue
        {
            internal SidAndAttributes User;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeOverlapped
        {
            internal IntPtr Internal;
            internal IntPtr InternalHigh;
            internal uint Offset;
            internal uint OffsetHigh;
            internal IntPtr EventHandle;
        }

        private sealed class OverlappedOperation : IDisposable
        {
            internal NativeOverlapped Value;

            internal OverlappedOperation()
            {
                Value = new NativeOverlapped
                {
                    EventHandle = CreateEvent(
                        IntPtr.Zero,
                        true,
                        false,
                        null)
                };
                if (Value.EventHandle == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "CreateEvent failed for Named Pipe I/O.");
                }
            }

            public void Dispose()
            {
                var eventHandle = Value.EventHandle;
                Value.EventHandle = IntPtr.Zero;
                if (eventHandle != IntPtr.Zero)
                    CloseHandle(eventHandle);
            }

            internal bool TryComplete(
                IntPtr handle,
                out uint transferred,
                out int error)
            {
                transferred = 0;
                error = 0;
                var waitResult = WaitForSingleObject(
                    Value.EventHandle,
                    Infinite);
                if (waitResult != WaitObject0)
                {
                    error = waitResult == WaitFailed
                        ? Marshal.GetLastWin32Error()
                        : ErrorOperationAborted;
                    return false;
                }

                if (GetOverlappedResult(
                        handle,
                        ref Value,
                        out transferred,
                        false))
                {
                    return true;
                }

                error = Marshal.GetLastWin32Error();
                return false;
            }
        }

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            EntryPoint = "CreateNamedPipeW")]
        private static extern IntPtr CreateNamedPipe(
            string name,
            uint openMode,
            uint pipeMode,
            int maxInstances,
            uint outBufferSize,
            uint inBufferSize,
            uint defaultTimeout,
                ref SecurityAttributes securityAttributes);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            EntryPoint = "CreateFileW")]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConnectNamedPipe(
            IntPtr namedPipe,
            ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DisconnectNamedPipe(
            IntPtr namedPipe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(
            IntPtr file,
            IntPtr buffer,
            uint bytesToRead,
            out uint bytesRead,
            ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(
            IntPtr file,
            IntPtr buffer,
            uint bytesToWrite,
            out uint bytesWritten,
            ref NativeOverlapped overlapped);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            EntryPoint = "CreateEventW")]
        private static extern IntPtr CreateEvent(
            IntPtr eventAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool manualReset,
            [MarshalAs(UnmanagedType.Bool)] bool initialState,
            string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            IntPtr handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetOverlappedResult(
            IntPtr file,
            ref NativeOverlapped overlapped,
            out uint bytesTransferred,
            [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelIoEx(
            IntPtr file,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            uint tokenInformationLength,
            out uint returnLength);

        [DllImport(
            "advapi32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            EntryPoint = "ConvertSidToStringSidW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertSidToStringSid(
            IntPtr sid,
            out IntPtr stringSid);

        [DllImport(
            "advapi32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            EntryPoint =
                "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool
            ConvertStringSecurityDescriptorToSecurityDescriptor(
                string stringSecurityDescriptor,
                uint stringSecurityDescriptorRevision,
                out IntPtr securityDescriptor,
                out uint securityDescriptorSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
#endif
