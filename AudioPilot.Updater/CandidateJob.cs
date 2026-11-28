using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Updater;

/// <summary>Only an uncommitted, microphone-deferred candidate belongs to this kill-on-helper-exit job.</summary>
internal sealed partial class CandidateJob : IDisposable
{
    private readonly SafeFileHandle _job;

    public CandidateJob(Process child)
    {
        _job = CreateJobObject(0, null);
        try
        {
            SetLimit(0x2000);
            if (!AssignProcessToJobObject(_job, child.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { _job.Dispose(); throw; }
    }

    public void Release() { SetLimit(0); _job.Dispose(); }
    public void Dispose() => _job.Dispose();

    internal static void TerminateUncontained(Process child, CandidateOwner owner)
    {
        if (child.HasExited) return;
        // Keep termination on this process handle, including if its PID is later reused.
        SafeProcessHandle handle = child.SafeHandle;
        if (!GetProcessTimes(handle, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (child.Id != owner.Pid || DateTime.FromFileTimeUtc(created).Ticks != owner.StartedUtcTicks)
            throw new InvalidDataException("The candidate process identity changed.");
        if (!TerminateProcess(handle, 1))
        {
            int error = Marshal.GetLastPInvokeError();
            if (!child.HasExited) throw new Win32Exception(error);
        }
    }

    private unsafe void SetLimit(uint flags)
    {
        var info = new ExtendedLimits { Basic = new BasicLimits { Flags = flags } };
        if (!SetInformationJobObject(_job, 9, &info, (uint)sizeof(ExtendedLimits))) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, ExtendedLimits* information, uint length);
}
