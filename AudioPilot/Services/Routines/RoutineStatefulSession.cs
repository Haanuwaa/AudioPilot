using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

/// <summary>
/// Captures the previous default device state for a stateful routine activation.
/// </summary>
/// <remarks>
/// Stateful routines keep enough activation context to restore prior defaults when the last active session
/// for a trigger path ends, without treating device-change routines as sticky sessions.
/// </remarks>
internal readonly record struct RoutineAudioRestoreSnapshot(
    string PreviousOutputDeviceId,
    string PreviousOutputDeviceName,
    string PreviousInputDeviceId,
    string PreviousInputDeviceName,
    float? PreviousOutputVolumePercent = null,
    bool? PreviousOutputMuted = null,
    float? PreviousInputVolumePercent = null,
    bool? PreviousInputMuted = null,
    RoutineAudioRestoration? AudioRestoration = null);

internal sealed class RoutineStatefulSession(
    string sessionKey,
    string routineId,
    string routineName,
    RoutineTriggerKind triggerKind,
    long activationSequence,
    bool restorePreviousAudioOnDeactivate,
    RoutineAudioRestoreSnapshot? restoreSnapshot,
    int? rootProcessId = null,
    string routineConfigurationFingerprint = "",
    RoutineProcessSnapshot? processIdentity = null)
{
    public RoutineProcessSnapshot? ProcessIdentity { get; } = processIdentity;
    public RoutineAppOutputLease? RoutingLease { get; init; }
    public string TriggerKey { get; init; } = routineId;
    public string ActionFingerprint { get; init; } = string.Empty;
    public string SessionKey { get; } = sessionKey;
    public string RoutineId { get; } = routineId;
    public string RoutineName { get; } = routineName;
    public RoutineTriggerKind TriggerKind { get; } = triggerKind;
    public long ActivationSequence { get; } = activationSequence;
    public bool RestorePreviousAudioOnDeactivate { get; } = restorePreviousAudioOnDeactivate;
    public RoutineAudioRestoreSnapshot? RestoreSnapshot { get; } = restoreSnapshot;
    public int? RootProcessId { get; } = rootProcessId;
    public string RoutineConfigurationFingerprint { get; } = routineConfigurationFingerprint;
}
