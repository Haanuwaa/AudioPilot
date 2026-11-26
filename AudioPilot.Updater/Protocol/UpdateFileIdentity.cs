using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.UpdateProtocol;

internal static partial class UpdateFileIdentity
{
    internal static void RequireUnlinked(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("The updater could not verify the open file.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 || info.LinkCount != 1)
            throw new InvalidDataException("The updater cannot change a linked file.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint Volume, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}
