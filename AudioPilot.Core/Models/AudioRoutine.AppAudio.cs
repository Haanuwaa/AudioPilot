using System.Text.Json.Serialization;

namespace AudioPilot.Models;

public sealed partial class AudioRoutine
{
    private decimal? _appVolumePercent;
    private RoutineMuteAction _appMuteAction;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AppVolumePercent
    {
        get => _appVolumePercent;
        set
        {
            decimal? normalized = value.HasValue ? VolumePercent.NormalizeLevel(value.Value) : null;
            if (!SetField(ref _appVolumePercent, normalized))
            {
                return;
            }

            NotifyAppAudioTargetChanged();
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RoutineMuteAction AppMuteAction
    {
        get => _appMuteAction;
        set
        {
            if (!SetField(ref _appMuteAction, value))
            {
                return;
            }

            NotifyAppAudioTargetChanged();
        }
    }

    [JsonIgnore]
    public bool HasAppVolumeTarget => RouteToApplication && AppVolumePercent.HasValue;

    [JsonIgnore]
    public bool HasAppMuteTarget => RouteToApplication && AppMuteAction != RoutineMuteAction.Unchanged;

    [JsonIgnore]
    public bool HasAppAudioTarget => HasAppVolumeTarget || HasAppMuteTarget;

    private void NotifyAppAudioTargetChanged()
    {
        OnPropertyChanged(nameof(HasAppVolumeTarget));
        OnPropertyChanged(nameof(HasAppMuteTarget));
        OnPropertyChanged(nameof(HasAppAudioTarget));
        OnPropertyChanged(nameof(HasVolumeTarget));
        OnPropertyChanged(nameof(HasExecutionTarget));
        OnPropertyChanged(nameof(TargetKindBadgeText));
        OnPropertyChanged(nameof(TargetSummary));
        OnTriggerSummariesChanged();
    }
}
