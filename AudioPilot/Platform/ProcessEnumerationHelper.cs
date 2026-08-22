using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioPilot.Platform
{
    internal static partial class ProcessEnumerationHelper
    {
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
