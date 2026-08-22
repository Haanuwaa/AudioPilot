using AudioPilot.Models;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.Services.Routines;

internal sealed class RoutineAppOutputLease(
    string leaseKey,
    string routineId,
    string routineName,
    int rootProcessId,
    string triggerAppPath,
    string outputDeviceId,
    string outputDeviceName,
    string inputDeviceId = "",
    string inputDeviceName = "")
{
    public RoutineAppStartProcessSnapshot? ProcessIdentity { get; init; }
    public long? AutomaticActivationSequence { get; set; }
    public Guid Generation { get; set; } = Guid.NewGuid();
    public string LeaseKey { get; } = leaseKey;
    public string RoutineId { get; set; } = routineId;
    public string RoutineName { get; set; } = routineName;
    public int RootProcessId { get; } = rootProcessId;
    public string TriggerAppPath { get; set; } = triggerAppPath;
    public string OutputDeviceId { get; set; } = outputDeviceId;
    public string? OutputDeviceStableId { get; set; }
    public string? InputDeviceStableId { get; set; }
    public string OutputDeviceName { get; set; } = outputDeviceName;
    public string InputDeviceId { get; set; } = inputDeviceId;
    public string InputDeviceName { get; set; } = inputDeviceName;
    public bool CompletionOverlayShown { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public HashSet<uint> AppliedOutputProcessIds { get; } = [];
    public HashSet<uint> AppliedInputProcessIds { get; } = [];

    internal CycleDevice? ResolveTarget(bool playback, IReadOnlyList<CycleDevice> activeDevices) =>
        AudioPilot.Helpers.PersistedAudioDeviceResolver.TryResolveMatch(new CycleDevice
        {
            Id = playback ? OutputDeviceId : InputDeviceId,
            Name = playback ? OutputDeviceName : InputDeviceName,
            StableId = playback ? OutputDeviceStableId : InputDeviceStableId,
        }, activeDevices);

    public RoutineAppOutputLease Clone()
    {
        var clone = new RoutineAppOutputLease(LeaseKey, RoutineId, RoutineName, RootProcessId, TriggerAppPath, OutputDeviceId, OutputDeviceName, InputDeviceId, InputDeviceName)
        {
            OutputDeviceStableId = OutputDeviceStableId,
            InputDeviceStableId = InputDeviceStableId,
            CompletionOverlayShown = CompletionOverlayShown,
            CreatedUtc = CreatedUtc,
            Generation = Generation,
            AutomaticActivationSequence = AutomaticActivationSequence,
            ProcessIdentity = ProcessIdentity,
        };
        clone.AppliedOutputProcessIds.UnionWith(AppliedOutputProcessIds);
        clone.AppliedInputProcessIds.UnionWith(AppliedInputProcessIds);
        return clone;
    }
}

internal readonly record struct RoutineAppStartMatch(AudioRoutine Routine, int ProcessId);
internal readonly record struct RoutineAppStartSnapshotSet(
    IReadOnlyList<RoutineAppStartProcessSnapshot> Snapshots,
    Dictionary<int, RoutineAppStartProcessSnapshot> SnapshotsByPid);
