using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Coordinators;

/// <summary>Tracks matching instances without running actions on startup, configuration changes, or uncertain discovery.</summary>
internal sealed class RoutineApplicationExitCoordinator
{
    internal sealed record StartedProcess(int ProcessId, long Generation, TaskCompletionSource Completion);
    private readonly Lock _sync = new();
    private Dictionary<string, AudioRoutine> _configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<int>?> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioRoutine> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, StartedProcess> _starting = [];
    private long _generation;
    private long _observationVersion;

    internal bool HasTriggers { get { lock (_sync) return _configured.Count > 0; } }
    internal bool NeedsBaseline { get { lock (_sync) return _active.Values.Any(static active => active == null); } }
    internal long Generation { get { lock (_sync) return _generation; } }
    internal long ObservationVersion { get { lock (_sync) return _observationVersion; } }

    internal void Configure(IEnumerable<AudioRoutine> routines)
    {
        var configured = routines.Where(static routine => routine.Enabled && routine.HasExecutionTarget && routine.ApplicationTriggerMode == ApplicationTriggerMode.AppExit)
            .ToDictionary(static routine => routine.RuntimeTriggerKey, StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            if (_configured.Count == configured.Count && configured.All(pair => _configured.TryGetValue(pair.Key, out var previous) && Fingerprint(previous) == Fingerprint(pair.Value))) return;
            _configured = configured;
            ResetCore();
        }
    }

    internal void Reset() { lock (_sync) ResetCore(); }
    private void ResetCore()
    {
        _generation++;
        _observationVersion++;
        _active.Clear();
        foreach (string key in _configured.Keys) _active[key] = null;
        _pending.Clear();
        _claimed.Clear();
        foreach (StartedProcess started in _starting.Values) started.Completion.TrySetResult();
        _starting.Clear();
    }

    internal void Seed(IReadOnlyList<RoutineProcessSnapshot> processes, long generation)
    {
        lock (_sync)
        {
            if (generation != _generation) return;
            foreach (var (key, routine) in _configured)
                if (_active[key] == null && RoutineApplicationRouting.GetApplicationPresence(routine.TriggerAppPath, processes).HasValue)
                    _active[key] = Matching(routine, processes);
        }
    }

    internal StartedProcess? BeginStart(int processId)
    {
        lock (_sync)
        {
            if (_configured.Count == 0) return null;
            var started = new StartedProcess(processId, _generation, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _starting[processId] = started;
            return started;
        }
    }

    internal void CompleteStart(StartedProcess? started, RoutineProcessSnapshot? process)
    {
        if (started == null) return;
        lock (_sync)
        {
            if (_starting.TryGetValue(started.ProcessId, out var current) && ReferenceEquals(current, started))
            {
                _starting.Remove(started.ProcessId);
                if (started.Generation == _generation && process is { } snapshot)
                    foreach (var (key, routine) in _configured)
                        if (RoutineApplicationRouting.IsRoutineAppDirectMatch(routine.TriggerAppPath, snapshot))
                        {
                            _observationVersion++;
                            (_active[key] ??= []).Add(snapshot.ProcessId);
                            _pending.Remove(key);
                            _claimed.Remove(key);
                        }
            }
            started.Completion.TrySetResult();
        }
    }

    internal Task PendingStart(int processId) { lock (_sync) return _starting.TryGetValue(processId, out var started) ? started.Completion.Task : Task.CompletedTask; }
    internal bool ShouldObserveStop(int processId)
    {
        lock (_sync) return _starting.ContainsKey(processId) || _active.Values.Any(active => active == null || active.Contains(processId));
    }

    internal (IReadOnlyList<AudioRoutine> Routines, bool Retry) ObserveStop(int processId, IReadOnlyList<RoutineProcessSnapshot> processes,
        long? generation = null, long? observationVersion = null)
    {
        lock (_sync)
        {
            if (generation.HasValue && generation != _generation) return ([], false);
            if (observationVersion.HasValue && observationVersion != _observationVersion) return ([], true);
            if (_starting.Keys.Any(pid => !processes.Any(process => process.ProcessId == pid))) return ([], true);
            bool retry = false;
            foreach (var (key, routine) in _configured)
            {
                if (_active[key]?.Contains(processId) != true) continue;
                bool? present = RoutineApplicationRouting.GetApplicationPresence(routine.TriggerAppPath, processes);
                if (!present.HasValue) { retry = true; continue; }
                _active[key] = Matching(routine, processes);
                if (!present.Value && !_pending.ContainsKey(key)) _pending[key] = routine.Clone();
            }
            return ([.. _pending.Values], retry);
        }
    }

    internal bool IsCurrent(AudioRoutine routine)
    {
        lock (_sync) return _pending.TryGetValue(routine.RuntimeTriggerKey, out var current) && ReferenceEquals(current, routine)
            && _configured.TryGetValue(routine.RuntimeTriggerKey, out var configured) && Fingerprint(configured) == Fingerprint(routine)
            && _active[routine.RuntimeTriggerKey] is { Count: 0 };
    }
    internal void Complete(AudioRoutine routine)
    {
        lock (_sync) if (_pending.TryGetValue(routine.RuntimeTriggerKey, out var current) && ReferenceEquals(current, routine))
        {
            _pending.Remove(routine.RuntimeTriggerKey);
            _claimed.Remove(routine.RuntimeTriggerKey);
        }
    }

    internal bool TryClaim(AudioRoutine routine)
    {
        lock (_sync) return _pending.TryGetValue(routine.RuntimeTriggerKey, out var current) && ReferenceEquals(current, routine)
            && _claimed.Add(routine.RuntimeTriggerKey);
    }

    internal bool ForgetUnverifiedStop(int processId, long generation, long observationVersion)
    {
        lock (_sync)
        {
            if (generation != _generation || observationVersion != _observationVersion) return false;
            foreach (var active in _active.Values) active?.Remove(processId);
            return true;
        }
    }

    private static HashSet<int> Matching(AudioRoutine routine, IReadOnlyList<RoutineProcessSnapshot> processes) =>
        [.. processes.Where(process => RoutineApplicationRouting.IsRoutineAppDirectMatch(routine.TriggerAppPath, process)).Select(static process => process.ProcessId)];
    private static string Fingerprint(AudioRoutine routine) => AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine);
}
