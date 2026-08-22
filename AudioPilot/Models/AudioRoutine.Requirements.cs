using System.Text.Json.Serialization;

namespace AudioPilot.Models;

public sealed partial class AudioRoutine
{
    private RoutineConditions _conditions = new();

    public RoutineConditions Conditions
    {
        get => _conditions;
        set
        {
            if (!SetField(ref _conditions, value ?? new())) return;
            OnPropertyChanged(nameof(ConditionsSummary));
            OnPropertyChanged(nameof(HasConditions));
        }
    }
    [JsonIgnore]
    public RoutineDeviceReference? TriggerDevice
    {
        get => CurrentTrigger.Device;
        set { if (SetTriggerField(CurrentTrigger.Device, newValue => CurrentTrigger = CurrentTrigger with { Device = newValue }, value)) OnTriggerSummariesChanged(); }
    }
    [JsonIgnore]
    public DeviceAvailabilityTransition DeviceTransition
    {
        get => CurrentTrigger.DeviceTransition;
        set { if (SetTriggerField(CurrentTrigger.DeviceTransition, newValue => CurrentTrigger = CurrentTrigger with { DeviceTransition = newValue }, value)) OnTriggerSummariesChanged(); }
    }
    [JsonIgnore] public string ConditionsSummary => Conditions.Summary;
    [JsonIgnore] public bool HasConditions => Conditions.HasRequirements;
}
