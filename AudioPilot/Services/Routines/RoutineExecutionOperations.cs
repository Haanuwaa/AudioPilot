using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Routines;

internal sealed record RoutineExecutionOptions(
    bool PreserveAudioLevels = false,
    bool MuteMic = false,
    bool MuteSound = false,
    bool Deafen = false,
    bool AllowDeferredRouting = true,
    bool CaptureAudioRestoration = false);

internal readonly record struct RoutineDeviceSwitchRequest(
    bool Playback,
    CycleDevice Target,
    RoutineExecutionOptions Options,
    bool RestoreMasterVolume,
    bool RestoreMicVolume,
    string OperationId,
    bool Communications = false)
{
    internal Role[] Roles => Communications ? [Role.Communications] : [Role.Console, Role.Multimedia];
}

/// <summary>Audio and reconnect operations used by the shared executor; callers retain ownership of these resources.</summary>
internal sealed record RoutineExecutionOperations(
    Func<bool, IReadOnlyList<CycleDevice>> GetActiveDevices,
    Func<bool, CycleDevice, CycleDevice?> FindActiveDevice,
    Func<bool, CycleDevice, IReadOnlyList<CycleDevice>, string, CancellationToken, Task<BluetoothReconnectAttemptResult>> ReconnectAsync,
    Func<bool, CycleDevice, BluetoothReconnectAttemptResult, string, CancellationToken, Task> WaitForReconnectAsync,
    Func<RoutineDeviceSwitchRequest, ValueTask<(bool Success, string? DeviceName)>> SwitchDefaultAsync,
    Func<bool, CycleDevice, uint, string, ValueTask<ProcessAudioDeviceSwitchResult>> SwitchApplicationAsync,
    Func<bool, string?, int, string, bool> ApplyVolume)
{
    internal Func<bool, string?, int, bool, string, RoutineVolumeApplicationResult>? ApplyOwnedVolume { get; init; }
    internal Func<RoutineDeviceSwitchRequest, ValueTask<RoutineRoleSwitchResult>>? SwitchRolesAsync { get; init; }
    internal Func<bool, string?, bool, bool, string, RoutineMuteApplicationResult>? ApplyMute { get; init; }
    internal Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;
    internal Func<string, bool>? IsApplicationRunning { get; init; }
    internal Func<IReadOnlyCollection<string>?>? GetConnectedNetworks { get; init; }

    internal static RoutineExecutionOperations Create(
        AudioDeviceService audio,
        Func<BluetoothReconnectCoordinator> getReconnectCoordinator,
        BluetoothReconnectOptions reconnectOptions,
        Logger logger,
        Func<int, CancellationToken, Task>? delayAsync = null,
        IRoutineProcessSnapshotProvider? processSnapshots = null,
        Func<bool, bool, string?>? muteGuard = null)
    {
        var operations = new RoutineExecutionOperations(
            playback => playback ? audio.GetActivePlaybackCycleEntries() : audio.GetActiveCaptureCycleEntries(),
            (playback, target) => playback
                ? audio.TryGetActivePlaybackCycleEntry(target.Id, target.Name)
                : audio.TryGetActiveRecordingCycleEntry(target.Id, target.Name),
            (playback, target, active, opId, cancellationToken) => getReconnectCoordinator().TryReconnectDetailedAsync(
                [target], active.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                playback ? BluetoothReconnectDeviceKind.Output : BluetoothReconnectDeviceKind.Input,
                reconnectOptions, opId, cancellationToken: cancellationToken),
            (_, _, result, _, cancellationToken) => result.Attempted
                ? (delayAsync ?? Task.Delay)(RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs, cancellationToken)
                : Task.CompletedTask,
            request => request.Playback
                ? audio.SwitchAudioDeviceAsync(request.Target.Id, request.Options.MuteMic, request.Options.MuteSound,
                    request.Options.Deafen, request.Options.PreserveAudioLevels, request.RestoreMasterVolume, request.RestoreMicVolume,
                    opId: request.OperationId, preserveEndpointMute: true, roles: request.Roles)
                : audio.SwitchInputDeviceToAsync(request.Target.Id, request.Target.Name,
                    request.Options.PreserveAudioLevels && request.RestoreMicVolume, showOverlay: null, opId: request.OperationId, roles: request.Roles),
            (playback, target, processId, opId) => playback
                ? audio.SwitchApplicationOutputDeviceDetailedAsync(processId, target.Id, target.Name, opId)
                : audio.SwitchApplicationInputDeviceDetailedAsync(processId, target.Id, target.Name, opId),
            (playback, targetId, percent, opId) => ComThreadingHelper.RunOnCoreAudioThread(() =>
            {
                using MMDevice? device = playback
                    ? audio.TryGetPlaybackDeviceForRoutine(targetId)
                    : audio.TryGetRecordingDeviceForRoutine(targetId);
                return AppCliOverlayCoordinator.TryApplyEndpointVolume(logger, device, percent, opId,
                    muteAtZero: false, unmuteAboveZero: false, out _);
            }))
        {
            ApplyOwnedVolume = (playback, id, percent, capture, opId) => RoutineEndpointVolumeService.Apply(audio, logger, playback, id, percent, capture, opId),
            ApplyMute = (playback, id, muted, capture, opId) => RoutineEndpointMuteService.Apply(audio, logger, playback, id, muted, capture, opId, muteGuard),
            IsApplicationRunning = target => (processSnapshots ?? new RoutineProcessSnapshotProvider(logger))
                .CaptureAll(AudioPilot.Cli.CliRoutineExecutionPolicy.GetCaptureOptionsForTriggerTarget(target))
                .Any(process => RoutineApplicationRouting.IsRoutineAppDirectMatch(AudioPilot.Helpers.RoutineTriggerPathHelper.NormalizeTriggerTarget(target), process)),
            GetConnectedNetworks = () =>
            {
                using var monitor = new WinRtNetworkMonitor(logger);
                return monitor.TryGetConnectedNetworkNames(out var networks) ? networks : null;
            },
        };
        return operations with
        {
            SwitchRolesAsync = async request =>
            {
                string? Read(Role role) => ComThreadingHelper.RunOnCoreAudioThread(() =>
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device = enumerator.GetDefaultAudioEndpoint(request.Playback ? DataFlow.Render : DataFlow.Capture, role);
                    return device.ID;
                });
                var pending = new RoutineRoleRestoration(request.Playback, request.Target.Id, request.Roles, Read,
                    AudioRoleConfiguration.ApplyConfiguredRole, observer => audio.DefaultRoleChanged += observer,
                    observer => audio.DefaultRoleChanged -= observer, logger);
                bool retained = false;
                try
                {
                    (bool success, string? name) = pending.AlreadySelected ? (true, request.Target.Name) : await operations.SwitchDefaultAsync(request);
                    if (!success) return new(false, name);
                    pending.Claim();
                    retained = request.Options.CaptureAudioRestoration;
                    return new(true, name, retained ? pending : null);
                }
                finally { if (!retained) pending.Dispose(); }
            },
        };
    }
}
