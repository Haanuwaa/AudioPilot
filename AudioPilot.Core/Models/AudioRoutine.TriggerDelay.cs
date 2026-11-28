using System.Text.Json.Serialization;

namespace AudioPilot.Models;

public sealed partial class AudioRoutine
{
    public const int MaxTriggerDelaySeconds = 60;

    private int _triggerDelaySeconds;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TriggerDelaySeconds
    {
        get => _triggerDelaySeconds;
        set
        {
            int normalized = Math.Clamp(value, 0, MaxTriggerDelaySeconds);
            if (!SetField(ref _triggerDelaySeconds, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(HasTriggerDelay));
            OnTriggerSummariesChanged();
        }
    }

    [JsonIgnore]
    public bool HasTriggerDelay => TriggerDelaySeconds > 0;
}
