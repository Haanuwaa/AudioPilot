using System.Text.Json.Serialization;

namespace AudioPilot.Models;

/// <summary>An endpoint mute action that leaves its volume level unchanged.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoutineMuteAction>))]
public enum RoutineMuteAction { Unchanged, Mute, Unmute }

public sealed partial class AudioRoutine
{
    private RoutineMuteAction _outputMuteAction;
    private RoutineMuteAction _inputMuteAction;
    public RoutineMuteAction OutputMuteAction
    {
        get => _outputMuteAction;
        set { if (SetField(ref _outputMuteAction, value)) NotifyMuteActionsChanged(); }
    }
    public RoutineMuteAction InputMuteAction
    {
        get => _inputMuteAction;
        set { if (SetField(ref _inputMuteAction, value)) NotifyMuteActionsChanged(); }
    }
    [JsonIgnore] public bool HasMuteTarget => OutputMuteAction != RoutineMuteAction.Unchanged || InputMuteAction != RoutineMuteAction.Unchanged;

    private void NotifyMuteActionsChanged()
    {
        OnPropertyChanged(nameof(HasMuteTarget));
        OnPropertyChanged(nameof(HasExecutionTarget));
        OnPropertyChanged(nameof(TargetKindBadgeText));
        OnPropertyChanged(nameof(TargetSummary));
    }
}
