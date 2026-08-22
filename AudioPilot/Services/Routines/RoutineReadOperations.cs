using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Routines;

/// <summary>Read-only boundaries shared by execution requirements and readiness checks.</summary>
internal sealed record RoutineReadOperations(Func<bool, IReadOnlyList<CycleDevice>> GetActiveDevices)
{
    internal Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;
    internal Func<string, bool?>? IsApplicationRunning { get; init; }
    internal IRoutineProcessSnapshotProvider? ProcessSnapshots { get; init; }
    internal Func<IReadOnlyCollection<string>?>? GetConnectedNetworks { get; init; }
    internal Func<bool, CycleDevice?>? GetDefaultDevice { get; init; }
    internal Func<bool, bool, string?>? GetMuteBlockReason { get; init; }

    internal static RoutineReadOperations Create(AudioDeviceService audio, Logger logger, IRoutineProcessSnapshotProvider processSnapshots) => new(
        playback => playback ? audio.GetActivePlaybackCycleEntries() : audio.GetActiveCaptureCycleEntries())
    {
        ProcessSnapshots = processSnapshots,
        IsApplicationRunning = target => RoutineApplicationRouting.GetApplicationPresence(target,
            processSnapshots.CaptureAll(CliRoutineExecutionPolicy.GetCaptureOptionsForTriggerTarget(target))),
        GetConnectedNetworks = () =>
        {
            using var monitor = new WinRtNetworkMonitor(logger);
            return monitor.TryGetConnectedNetworkNames(out var networks) ? networks : null;
        },
        GetDefaultDevice = playback => ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            using MMDevice? device = playback ? audio.TryGetPlaybackDeviceForRoutine(null) : audio.TryGetRecordingDeviceForRoutine(null);
            return device == null ? null : new CycleDevice { Id = device.ID, Name = device.FriendlyName };
        }),
    };
}
