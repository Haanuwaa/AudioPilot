using System.Text.Json.Serialization;

namespace AudioPilot.Models;

public sealed partial class AudioRoutine
{
    private RoutineDeviceReference? _communicationsOutput;
    private RoutineDeviceReference? _communicationsInput;

    public RoutineDeviceReference? CommunicationsOutput
    {
        get => _communicationsOutput;
        set { if (SetField(ref _communicationsOutput, value)) NotifyCommunicationsTargetsChanged(); }
    }

    public RoutineDeviceReference? CommunicationsInput
    {
        get => _communicationsInput;
        set { if (SetField(ref _communicationsInput, value)) NotifyCommunicationsTargetsChanged(); }
    }

    [JsonIgnore] public bool HasCommunicationsTarget => CommunicationsOutput != null || CommunicationsInput != null;
    [JsonIgnore] internal string CommunicationsFingerprint => $"{CommunicationsOutput?.IdentityKey}\u001e{CommunicationsInput?.IdentityKey}";

    internal string? ValidateCommunicationsTargets() =>
        CommunicationsOutput is { IsValid: false } or { Playback: false } ? "Choose a valid communications playback device."
        : CommunicationsInput is { IsValid: false } or { Playback: true } ? "Choose a valid communications microphone."
        : SwitchOutputPerApp && HasCommunicationsTarget ? "Communications targets change system defaults and cannot be combined with application routing." : null;

    private void NotifyCommunicationsTargetsChanged()
    {
        OnPropertyChanged(nameof(HasCommunicationsTarget));
        OnPropertyChanged(nameof(HasExecutionTarget));
        OnPropertyChanged(nameof(TargetKindBadgeText));
        OnPropertyChanged(nameof(TargetSummary));
    }
}
