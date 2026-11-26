using AudioPilot.Models;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio;

/// <summary>
/// Groups live process sessions, displaying active state when available and idle state otherwise.
/// </summary>
internal sealed class AudioSessionSnapshotAccumulator(List<AudioSessionSnapshot> snapshots)
{
    private readonly Dictionary<uint, (int Index, AudioSessionState State)> _processes = [];

    internal bool Add(AudioSessionSnapshot snapshot, AudioSessionState state)
    {
        if (snapshot.ProcessId is not uint pid || state == AudioSessionState.AudioSessionStateExpired)
        {
            return false;
        }

        bool active = state == AudioSessionState.AudioSessionStateActive;
        if (active) snapshot = snapshot with { ActiveDeviceUse = AudioSessionDeviceUse.One };
        if (!_processes.TryGetValue(pid, out var existing))
        {
            _processes.Add(pid, (snapshots.Count, state));
            snapshots.Add(snapshot);
            return true;
        }

        AudioSessionSnapshot previous = snapshots[existing.Index];
        bool preferIncoming = active && existing.State != AudioSessionState.AudioSessionStateActive;
        AudioSessionSnapshot representative = preferIncoming ? snapshot : previous;
        bool combineState = state == existing.State;
        snapshots[existing.Index] = representative with
        {
            Volume = combineState ? Math.Max(previous.Volume, snapshot.Volume) : representative.Volume,
            IsMuted = combineState ? previous.IsMuted && snapshot.IsMuted : representative.IsMuted,
            SessionCount = previous.SessionCount + snapshot.SessionCount,
            ActiveDeviceUse = active && combineState && !string.Equals(previous.EndpointId, snapshot.EndpointId, StringComparison.OrdinalIgnoreCase)
                ? AudioSessionDeviceUse.Multiple : representative.ActiveDeviceUse,
        };
        if (preferIncoming)
        {
            _processes[pid] = (existing.Index, state);
        }
        return true;
    }
}
