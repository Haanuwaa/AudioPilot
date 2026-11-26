using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Platform
{
    internal static partial class ProcessEnumerationHelper
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorAccessDenied = 5;
        private const uint StillActiveExitCode = 259;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public nint ExitStatus;
            public nint PebBaseAddress;
            public nint AffinityMask;
            public int BasePriority;
            public nint UniqueProcessId;
            public nint InheritedFromUniqueProcessId;
        }

        internal static long? TryGetStartTimeUtcTicks(uint processId)
        {
            if (processId == 0 || processId > int.MaxValue) return null;
            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            return process.IsInvalid ? null : ReadStartTimeUtcTicks(process);
        }

        internal static bool TryGetRunningStartTimeUtcTicks(uint processId, out long startedUtcTicks, out bool accessDenied)
        {
            startedUtcTicks = 0;
            accessDenied = false;
            if (processId == 0 || processId > int.MaxValue) return false;
            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid)
            {
                accessDenied = Marshal.GetLastPInvokeError() == ErrorAccessDenied;
                return false;
            }
            if (!GetExitCodeProcess(process, out uint exitCode) || exitCode != StillActiveExitCode)
                return false;
            if (ReadStartTimeUtcTicks(process) is not long ticks)
                return false;
            startedUtcTicks = ticks;
            return true;
        }

        internal static int TryGetParentProcessId(int processId)
        {
            if (processId <= 0) return 0;
            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (process.IsInvalid) return 0;
            ProcessBasicInformation pbi = new();
            int status = NtQueryInformationProcess(process, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            return status == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : 0;
        }

        internal static (string? Path, long? StartedUtcTicks) TryGetImageIdentity(int processId)
        {
            if (processId <= 0) return default;
            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (process.IsInvalid) return default;
            string? path = ReadImagePath(process);
            return path == null ? default : (path, ReadStartTimeUtcTicks(process));
        }

        internal static unsafe (long? StartedUtcTicks, string? PinKey) TryGetMixerAppIdentity(uint processId)
        {
            if (processId == 0 || processId > int.MaxValue) return default;
            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid || ReadStartTimeUtcTicks(process) is not long started) return default;

            // The packaged app ID survives package updates. A desktop app is scoped to its full image path.
            const int capacity = 256;
            char* appId = stackalloc char[capacity];
            uint length = capacity;
            string? identity = GetApplicationUserModelId(process, ref length, appId) == 0 && length is > 1 and <= capacity
                ? "app:" + new string(appId, 0, (int)length - 1)
                : ReadImagePath(process) is { Length: > 0 } path ? "exe:" + path : null;
            string? key = identity == null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
            return (started, key);
        }

        private static unsafe string? ReadImagePath(SafeProcessHandle process)
        {
            const int initialPathCapacity = 512;
            char* path = stackalloc char[initialPathCapacity];
            uint length = initialPathCapacity;
            if (QueryFullProcessImageName(process, 0, path, ref length))
                return new string(path, 0, (int)length);
            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer) return default;

            char[] longPath = ArrayPool<char>.Shared.Rent(32768);
            try
            {
                fixed (char* buffer = longPath)
                {
                    length = (uint)longPath.Length;
                    return QueryFullProcessImageName(process, 0, buffer, ref length)
                        ? new string(buffer, 0, (int)length)
                        : null;
                }
            }
            finally { ArrayPool<char>.Shared.Return(longPath, clearArray: true); }
        }

        private static long? ReadStartTimeUtcTicks(SafeProcessHandle process) =>
            GetProcessTimes(process, out long created, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(created).Ticks : null;

        [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* path, ref uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(SafeProcessHandle process, out long creationFileTime, out long exitFileTime, out long kernelTime, out long userTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(
            SafeProcessHandle processHandle,
            int processInformationClass,
            ref ProcessBasicInformation processInformation,
            int processInformationLength,
            out int returnLength);

        [LibraryImport("kernel32.dll")]
        private static unsafe partial int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, char* appId);

        internal delegate int ProcessIdEnumerator(Span<int> buffer);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool K32EnumProcesses(int* processIds, uint bufferBytes, out uint returnedBytes);

        internal static void EnumerateProcesses(Action<Process> visitor)
        {
            ArgumentNullException.ThrowIfNull(visitor);

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        visitor(process);
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static HashSet<int> CaptureRunningProcessIds(ProcessIdEnumerator? enumerate = null)
        {
            enumerate ??= ReadProcessIds;
            int capacity = 1024;
            while (true)
            {
                int[] buffer = ArrayPool<int>.Shared.Rent(capacity);
                try
                {
                    int count = enumerate(buffer);
                    if ((uint)count > (uint)buffer.Length)
                        throw new InvalidOperationException("Process enumeration returned an invalid count.");

                    // A full buffer can be truncated; never report it as a complete snapshot.
                    if (count < buffer.Length)
                    {
                        var processIds = new HashSet<int>(count);
                        foreach (int processId in buffer.AsSpan(0, count)) processIds.Add(processId);
                        return processIds;
                    }

                    capacity = checked(buffer.Length * 2);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(buffer);
                }
            }
        }

        private static unsafe int ReadProcessIds(Span<int> buffer)
        {
            fixed (int* pointer = buffer)
            {
                if (!K32EnumProcesses(pointer, checked((uint)buffer.Length * sizeof(int)), out uint returnedBytes))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                return checked((int)(returnedBytes / sizeof(int)));
            }
        }
    }
}
