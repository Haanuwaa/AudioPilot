using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators;

/// <summary>Detects endpoint availability transitions from completed audio snapshots without polling or treating startup as a connection.</summary>
internal sealed class RoutineDeviceAvailabilityCoordinator(Logger logger)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (string Configuration, bool Available)> _states = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioRoutine> _pending = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsCurrentConfiguration(AudioRoutine trigger)
    {
        lock (_sync)
            return _configured.TryGetValue(trigger.RuntimeTriggerKey, out string? expected) && expected == Configuration(trigger);
    }

    private static string Configuration(AudioRoutine trigger) => RoutineTrigger.Capture(trigger).ConfigurationKey + "\u001e" + AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(trigger);

    internal void Complete(AudioRoutine trigger)
    {
        lock (_sync)
            if (_pending.TryGetValue(trigger.RuntimeTriggerKey, out var current) && ReferenceEquals(current, trigger))
                _pending.Remove(trigger.RuntimeTriggerKey);
    }

    internal IReadOnlyList<AudioRoutine> Observe(IEnumerable<AudioRoutine> configured, Func<bool, IReadOnlyList<CycleDevice>> getActiveDevices, bool evaluateTransitions)
    {
        AudioRoutine[] triggers = [.. configured.Where(static routine => routine.Enabled && routine.HasExecutionTarget).SelectMany(static routine => routine.ExpandAutomaticTriggers(static trigger => trigger.Kind == RoutineTriggerKind.DeviceAvailability))
            .Where(static trigger => trigger.Enabled && trigger.HasExecutionTarget && trigger.TriggerKind == RoutineTriggerKind.DeviceAvailability && trigger.TriggerDevice is { IsValid: true } && Enum.IsDefined(trigger.DeviceTransition))];
        lock (_sync)
        {
            if (!evaluateTransitions)
            {
                _configured.Clear();
                foreach (AudioRoutine trigger in triggers) _configured[trigger.RuntimeTriggerKey] = Configuration(trigger);
                foreach (string old in _states.Keys.Where(key => !_configured.ContainsKey(key)).ToArray()) { _states.Remove(old); _pending.Remove(old); }
                foreach (string key in _pending.Keys.Where(key => !_configured.TryGetValue(key, out var current) || current != Configuration(_pending[key])).ToArray()) _pending.Remove(key);
            }
            if (triggers.Length == 0) return [];
            try
            {
                IReadOnlyList<CycleDevice> output = triggers.Any(static trigger => trigger.TriggerDevice!.Playback) ? getActiveDevices(true) : [];
                IReadOnlyList<CycleDevice> input = triggers.Any(static trigger => !trigger.TriggerDevice!.Playback) ? getActiveDevices(false) : [];
                foreach (AudioRoutine trigger in triggers)
                {
                    var device = trigger.TriggerDevice!;
                    string configuration = Configuration(trigger);
                    if (evaluateTransitions && (!_configured.TryGetValue(trigger.RuntimeTriggerKey, out string? currentConfiguration) || currentConfiguration != configuration)) continue;
                    bool? observed = device.GetAvailability(device.Playback ? output : input);
                    if (!observed.HasValue) { _pending.Remove(trigger.RuntimeTriggerKey); continue; }
                    bool available = observed.Value;
                    bool existing = _states.TryGetValue(trigger.RuntimeTriggerKey, out var previous) && previous.Configuration == configuration;
                    if (!evaluateTransitions && existing) continue;
                    if (!existing || previous.Available != available) _pending.Remove(trigger.RuntimeTriggerKey);
                    _states[trigger.RuntimeTriggerKey] = (configuration, available);
                    if (!evaluateTransitions || !existing || previous.Available == available) continue;
                    bool matches = trigger.DeviceTransition == DeviceAvailabilityTransition.Both ||
                        (available ? trigger.DeviceTransition == DeviceAvailabilityTransition.Connected : trigger.DeviceTransition == DeviceAvailabilityTransition.Disconnected);
                    if (!matches) continue;
                    logger.Info("RoutineDeviceAvailability", () => $"routine-device-transition | routineId={LogPrivacy.Id(trigger.Id)} trigger={LogPrivacy.Id(trigger.RuntimeTriggerKey)} available={available} device={LogPrivacy.Id(device.Id)}");
                    _pending[trigger.RuntimeTriggerKey] = trigger;
                }
            }
            catch (Exception ex)
            {
                logger.Warning("RoutineDeviceAvailability", "routine-device-snapshot-failed", nameof(Observe), ex);
                return [];
            }
            return evaluateTransitions ? [.. _pending.Values] : [];
        }
    }
}
