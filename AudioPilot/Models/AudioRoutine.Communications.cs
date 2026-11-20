using System.Text.Json.Serialization;
using AudioPilot.Localization;

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

    internal string? ValidateCommunicationsTargets() => ValidateCommunicationsTargetsMessage() is { } message ? LocalizationService.English.Format(message) : null;

    internal LocalizedText? ValidateCommunicationsTargetsMessage() =>
        CommunicationsOutput is { IsValid: false } or { Playback: false } ? new("Validation_CommunicationsOutput")
        : CommunicationsInput is { IsValid: false } or { Playback: true } ? new("Validation_CommunicationsInput")
        : RouteToApplication && HasCommunicationsTarget ? new("Validation_CommunicationsRouting") : null;

    private void NotifyCommunicationsTargetsChanged()
    {
        OnPropertyChanged(nameof(HasCommunicationsTarget));
        OnPropertyChanged(nameof(HasExecutionTarget));
        OnPropertyChanged(nameof(TargetKindBadgeText));
        OnPropertyChanged(nameof(TargetSummary));
    }
}
