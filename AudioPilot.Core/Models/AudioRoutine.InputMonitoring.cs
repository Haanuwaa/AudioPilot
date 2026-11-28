using System.Text.Json.Serialization;

namespace AudioPilot.Models;

/// <summary>An input monitoring ("Listen to this device") action for a routine.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoutineInputMonitoringAction>))]
public enum RoutineInputMonitoringAction { Unchanged, Enable, Disable }

public sealed partial class AudioRoutine
{
    private RoutineInputMonitoringAction _inputMonitoringAction;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RoutineInputMonitoringAction InputMonitoringAction
    {
        get => _inputMonitoringAction;
        set { if (SetField(ref _inputMonitoringAction, value)) NotifyInputMonitoringActionChanged(); }
    }

    [JsonIgnore]
    public bool HasInputMonitoringTarget => InputMonitoringAction != RoutineInputMonitoringAction.Unchanged;

    private void NotifyInputMonitoringActionChanged()
    {
        OnPropertyChanged(nameof(HasInputMonitoringTarget));
        OnPropertyChanged(nameof(HasExecutionTarget));
        OnPropertyChanged(nameof(TargetKindBadgeText));
        OnPropertyChanged(nameof(TargetSummary));
    }
}
