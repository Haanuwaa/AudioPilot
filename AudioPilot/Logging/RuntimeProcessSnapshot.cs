using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioPilot.Logging;

/// <summary>On-demand process counters, sampled without forcing a collection or polling in the background.</summary>
public sealed record RuntimeProcessSnapshot(int ProcessId, string Architecture, long AllocatedBytes,
    long ManagedBytes, long PrivateBytes, long WorkingSetBytes, int Handles, int Threads,
    int Gen0Collections, int Gen1Collections, int Gen2Collections)
{
    public static RuntimeProcessSnapshot Capture()
    {
        using Process process = Process.GetCurrentProcess();
        return new(process.Id, RuntimeInformation.ProcessArchitecture.ToString(), GC.GetTotalAllocatedBytes(precise: false),
            GC.GetTotalMemory(forceFullCollection: false), process.PrivateMemorySize64, process.WorkingSet64,
            process.HandleCount, process.Threads.Count, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
}
