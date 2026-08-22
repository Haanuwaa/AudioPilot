using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AudioPilot.Models;

public sealed partial class AudioRoutine
{
    public const int MaxAutomaticTriggers = 16;
    private List<RoutineTrigger> _triggers = [];
    private RoutineTrigger? _manualTrigger;

    public List<RoutineTrigger> Triggers
    {
        get => _triggers;
        set
        {
            _triggers = value ?? [];
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatefulTriggers));
            OnTriggerSummariesChanged();
        }
    }

    private RoutineTrigger CurrentTrigger
    {
        get => _triggers.FirstOrDefault() ?? (_manualTrigger ??= new RoutineTrigger { Id = string.Empty, Kind = RoutineTriggerKind.Hotkey });
        set
        {
            if (value.Kind == RoutineTriggerKind.Hotkey)
            {
                if (_triggers.Count != 0) _triggers.RemoveAt(0);
                return;
            }
            if (string.IsNullOrEmpty(value.Id)) value = value with { Id = Guid.NewGuid().ToString("N") };
            if (_triggers.Count == 0) _triggers.Add(value);
            else _triggers[0] = value;
        }
    }

    private bool SetTriggerField<T>(T current, Action<T> assign, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        OnPropertyChanged(propertyName);
        return true;
    }

    [JsonIgnore]
    internal string TriggerIdentity { get; set; } = string.Empty;
    [JsonIgnore]
    internal string RuntimeTriggerKey => string.IsNullOrEmpty(TriggerIdentity) ? Id : $"{Id}/{TriggerIdentity}";
    [JsonIgnore]
    public bool HasStatefulTriggers => Triggers.Any(static trigger => trigger?.IsStateful == true);
    [JsonIgnore]
    internal string TriggerFingerprint => string.Join('\u001e', Triggers.Select(static trigger => trigger == null ? "null" : trigger.Id + "=" + trigger.ConfigurationKey + ":" + trigger.NotifyBeforeRun));

    internal IEnumerable<AudioRoutine> ExpandAutomaticTriggers(Func<RoutineTrigger, bool>? include = null)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RoutineTrigger? configured in Triggers.Take(MaxAutomaticTriggers))
        {
            if (configured == null || string.IsNullOrWhiteSpace(configured.Id) || !ids.Add(configured.Id) || (include != null && !include(configured))) continue;
            RoutineTrigger trigger = configured.Normalize();
            if (trigger.Validate(validatePattern: false) != null) continue;
            var projection = Clone(includeTriggers: false);
            projection.Triggers = [trigger.Copy()];
            projection.TriggerIdentity = trigger.Id;
            projection.RestorePreviousAudioOnDeactivate = RestorePreviousAudioOnDeactivate;
            yield return projection;
        }
    }

    internal string? ValidateTriggers()
    {
        if (Triggers.Count > MaxAutomaticTriggers)
            return $"A routine can have up to {MaxAutomaticTriggers} automatic triggers.";
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configurations = new HashSet<string>(StringComparer.Ordinal);
        foreach (RoutineTrigger trigger in Triggers)
        {
            if (trigger == null) return "Automatic triggers cannot contain null entries.";
            if (string.IsNullOrWhiteSpace(trigger.Id) || !ids.Add(trigger.Id)) return "Automatic triggers must have unique, nonempty IDs.";
            string? error = trigger.Validate();
            if (error != null) return error;
            if (!configurations.Add(trigger.ConfigurationKey)) return "Remove duplicate automatic triggers.";
        }
        return null;
    }
}
