using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Platform;

internal static partial class WindowsFilePath
{
    internal static string? ResolveExisting(string path)
    {
        // Zero access permits opening metadata for a file or directory.
        using SafeFileHandle handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x02000000, 0);
        return handle.IsInvalid ? null : GetFinalPath(handle);
    }

    internal static unsafe string? GetFinalPath(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[512];
        fixed (char* pointer = buffer)
        {
            uint length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
            if (length == 0 || length > 32768) return null;
            if (length < buffer.Length) return NormalizeFinalPath(buffer[..(int)length]);

            var longPath = new char[length];
            fixed (char* longPointer = longPath)
            {
                length = GetFinalPathNameByHandle(handle, longPointer, (uint)longPath.Length, 0);
                return length > 0 && length < longPath.Length ? NormalizeFinalPath(longPath.AsSpan(0, (int)length)) : null;
            }
        }
    }

    internal static unsafe bool IsRegularFileWithSingleLink(SafeFileHandle handle)
    {
        FileInformation info = default;
        return GetFileInformationByHandle(handle, &info) != 0 && info.LinkCount == 1 &&
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static string? NormalizeFinalPath(ReadOnlySpan<char> path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return string.Concat(@"\\", path[8..]);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7 && path[5] == ':' && path[6] == '\\')
            return path[4..].ToString();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
        internal uint VolumeSerialNumber, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share, nint securityAttributes,
        FileMode creationDisposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint capacity, uint flags);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetFileInformationByHandle(SafeFileHandle handle, FileInformation* info);
}
