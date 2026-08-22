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
    private long _configurationVersion;
    private long _observationVersion;
    private long _outputObservation;
    private long _inputObservation;

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
        long configurationVersion;
        long observationVersion;
        lock (_sync)
        {
            if (!evaluateTransitions)
            {
                _configurationVersion++;
                _configured.Clear();
                foreach (AudioRoutine trigger in triggers) _configured[trigger.RuntimeTriggerKey] = Configuration(trigger);
                foreach (string old in _states.Keys.Where(key => !_configured.ContainsKey(key)).ToArray()) { _states.Remove(old); _pending.Remove(old); }
                foreach (string key in _pending.Keys.Where(key => !_configured.TryGetValue(key, out var current) || current != Configuration(_pending[key])).ToArray()) _pending.Remove(key);
            }
            if (triggers.Length == 0) return [];
            configurationVersion = _configurationVersion;
            observationVersion = ++_observationVersion;
        }

        IReadOnlyList<CycleDevice>? output = Read(true);
        IReadOnlyList<CycleDevice>? input = Read(false);
        lock (_sync)
        {
            if (_configurationVersion != configurationVersion) return [];
            bool outputCurrent = output != null && observationVersion > _outputObservation;
            bool inputCurrent = input != null && observationVersion > _inputObservation;
            if (outputCurrent) _outputObservation = observationVersion;
            if (inputCurrent) _inputObservation = observationVersion;
            foreach (AudioRoutine trigger in triggers)
            {
                var device = trigger.TriggerDevice!;
                if (device.Playback ? !outputCurrent : !inputCurrent) continue;
                string configuration = Configuration(trigger);
                if (!_configured.TryGetValue(trigger.RuntimeTriggerKey, out string? currentConfiguration) || currentConfiguration != configuration) continue;
                bool? observed = device.GetAvailability((device.Playback ? output : input)!);
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
            return evaluateTransitions ? [.. _pending.Values.Where(trigger => trigger.TriggerDevice!.Playback ? outputCurrent : inputCurrent)] : [];
        }

        IReadOnlyList<CycleDevice>? Read(bool playback)
        {
            if (!triggers.Any(trigger => trigger.TriggerDevice!.Playback == playback)) return null;
            try { return [.. getActiveDevices(playback)]; }
            catch (Exception ex)
            {
                logger.Warning("RoutineDeviceAvailability", $"routine-device-snapshot-failed | direction={(playback ? "output" : "input")}", nameof(Observe), ex);
                return null;
            }
        }
    }
}
