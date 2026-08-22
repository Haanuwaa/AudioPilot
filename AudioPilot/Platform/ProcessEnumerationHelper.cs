using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Platform
{
    internal static partial class ProcessEnumerationHelper
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        internal static long? TryGetStartTimeUtcTicks(uint processId)
        {
            if (processId == 0 || processId > int.MaxValue) return null;
            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            return !process.IsInvalid && GetProcessTimes(process, out long created, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(created).Ticks : null;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(SafeProcessHandle process, out long creationFileTime, out long exitFileTime, out long kernelTime, out long userTime);

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
