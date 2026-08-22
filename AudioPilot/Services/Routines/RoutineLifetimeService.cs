using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

/// <summary>Owns stateful activations and serializes their audio changes with restoration.</summary>
internal sealed partial class RoutineLifetimeService
{
    private sealed record Activation(AudioRoutine Routine, int ProcessId, CancellationTokenSource Cancellation, bool IsManual);

    internal readonly record struct ActivationEnd(IReadOnlyList<Deactivation> Sessions, RoutineAppOutputLease? Lease);

    internal readonly record struct Deactivation(RoutineStatefulSession Session, long LatestSequence, long Revision);

    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private readonly Dictionary<string, RoutineStatefulSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Activation> _activations = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _configuredRoutines;
    private long _sequence;
    private long _revision;
    private bool _stopping;
    private IProcessLifecycleMonitor? _processMonitor;
    private Action<int>? _onStarted;
    private Action<int>? _onStopped;

    internal IReadOnlyList<RoutineStatefulSession> Sessions
    {
        get { lock (_sync) return [.. _sessions.Values]; }
    }

    internal Dictionary<string, RoutineStatefulSession> SessionsForTests => _sessions;

    internal RoutineStatefulSession Register(AudioRoutine routine, int? processId, RoutineAudioRestoreSnapshot? snapshot, RoutineProcessSnapshot? processIdentity = null, RoutineAppOutputLease? routingLease = null, CancellationToken cancellationToken = default)
    {
        RoutineAudioRestoration? displaced = null;
        RoutineStatefulSession session;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping) throw new OperationCanceledException("Routine lifetimes are stopping.", cancellationToken);
            session = AppRoutineStatefulCoordinator.CreateSession(routine, processId, ++_sequence, snapshot, processIdentity, routingLease);
            if (_sessions.TryGetValue(session.SessionKey, out var previous)) displaced = previous.RestoreSnapshot?.AudioRestoration;
            _sessions[session.SessionKey] = session;
            if (displaced != null && _sessions.Values.Any(other => ReferenceEquals(other.RestoreSnapshot?.AudioRestoration, displaced))) displaced = null;
            _revision++;
        }
        displaced?.Dispose();
        return session;
    }

    /// <summary>Joins an existing OR activation without applying audio again or replacing its original restoration state.</summary>
    internal bool TryJoinActiveRoutine(AudioRoutine routine, int? processId, RoutineProcessSnapshot? identity, bool trackLifetime, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string actions = AppRoutineStatefulCoordinator.CreateRoutineActionFingerprint(routine);
            RoutineStatefulSession? active = _sessions.Values.FirstOrDefault(session =>
                string.Equals(session.RoutineId, routine.Id, StringComparison.OrdinalIgnoreCase) && session.ActionFingerprint == actions);
            if (active == null) return false;
            if (trackLifetime && routine.IsStatefulTrigger)
            {
                string key = AppRoutineStatefulCoordinator.CreateRoutineStatefulSessionKey(routine, processId);
                if (!_sessions.ContainsKey(key))
                    _sessions[key] = AppRoutineStatefulCoordinator.CreateSession(routine, processId, active.ActivationSequence,
                        active.RestoreSnapshot, identity, active.RoutingLease);
            }
            return true;
        }
    }

    internal async Task<T> RunActivationAsync<T>(AudioRoutine routine, int processId, Func<CancellationToken, Task<T>> execute, bool isManual = false, CancellationToken cancellationToken = default)
    {
        AudioRoutine snapshot = routine.Clone();
        string key = AppRoutineStatefulCoordinator.CreateRoutineStatefulSessionKey(snapshot, processId) + (isManual ? "|manual" : string.Empty);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activation = new Activation(snapshot, processId, cancellation, isManual);
        lock (_sync)
        {
            if (_stopping || (_configuredRoutines != null && routine.IsStatefulTrigger &&
                (!_configuredRoutines.TryGetValue(routine.RuntimeTriggerKey, out string? fingerprint) ||
                fingerprint != AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(snapshot))))
                throw new OperationCanceledException("The routine activation is no longer current.", cancellationToken);
            if (_activations.TryGetValue(key, out Activation? previous))
                previous.Cancellation.Cancel();
            _activations[key] = activation;
        }

        bool acquired = false;
        try
        {
            await _audioGate.WaitAsync(cancellation.Token);
            acquired = true;
            cancellation.Token.ThrowIfCancellationRequested();
            return await execute(cancellation.Token);
        }
        finally
        {
            if (acquired)
                _audioGate.Release();
            lock (_sync)
            {
                if (_activations.TryGetValue(key, out Activation? current) && ReferenceEquals(current, activation))
                    _activations.Remove(key);
            }
        }
    }

    internal ActivationEnd CancelActivation(AudioRoutine routine, int processId)
    {
        string key = AppRoutineStatefulCoordinator.CreateRoutineStatefulSessionKey(routine, processId);
        lock (_sync)
        {
            _claims.Remove(RoutineApplicationRouting.CreateRoutineAppOutputLeaseKey(routine.RuntimeTriggerKey, processId));
            if (_activations.TryGetValue(key, out Activation? activation))
                activation.Cancellation.Cancel();
            return new(CaptureDeactivations(session => string.Equals(session.SessionKey, key, StringComparison.OrdinalIgnoreCase)),
                FindLease(RoutineApplicationRouting.CreateRoutineAppOutputLeaseKey(routine.Id, processId)));
        }
    }

    internal void AttachProcessMonitor(IProcessLifecycleMonitor monitor, Action<int> started, Action<int> stopped)
    {
        lock (_sync)
        {
            if (_stopping) return;
            if (_processMonitor != null)
            {
                _processMonitor.ProcessStarted -= _onStarted;
                _processMonitor.ProcessStopped -= _onStopped;
            }
            _processMonitor = monitor;
            _onStarted = started;
            _onStopped = processId => { CancelProcess(processId); stopped(processId); };
            monitor.ProcessStarted += _onStarted;
            monitor.ProcessStopped += _onStopped;
        }
    }

    internal void CancelTrigger(RoutineTriggerKind kind)
    {
        lock (_sync)
            foreach (Activation activation in _activations.Values.ToArray())
                if (!activation.IsManual && activation.Routine.TriggerKind == kind)
                    activation.Cancellation.Cancel();
    }

    internal void CancelProcess(int processId)
    {
        lock (_sync)
        {
            foreach (Activation activation in _activations.Values.ToArray())
                if (activation.Routine.TriggerKind == RoutineTriggerKind.Application && activation.ProcessId == processId)
                    activation.Cancellation.Cancel();
        }
    }

    internal IReadOnlyList<Deactivation> Synchronize(IReadOnlyList<AudioRoutine> applications, IReadOnlyList<AudioRoutine> steam)
    {
        lock (_sync)
        {
            _configuredRoutines = applications.Concat(steam).Where(static routine => routine.Enabled)
                .GroupBy(static routine => routine.RuntimeTriggerKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(group.Last()), StringComparer.OrdinalIgnoreCase);
            SynchronizeClaims(applications);
            foreach (Activation activation in _activations.Values.ToArray())
            {
                if (!activation.Routine.IsStatefulTrigger) continue;
                IReadOnlyList<AudioRoutine> routines = activation.Routine.TriggerKind == RoutineTriggerKind.Application ? applications : steam;
                if (!routines.Any(routine => routine.Enabled &&
                    string.Equals(routine.RuntimeTriggerKey, activation.Routine.RuntimeTriggerKey, StringComparison.OrdinalIgnoreCase) &&
                    AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine) ==
                    AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(activation.Routine)))
                    activation.Cancellation.Cancel();
            }
            HashSet<string> keys = [.. AppRoutineStatefulCoordinator.GetInvalidRoutineStatefulSessionKeys(_sessions, applications, steam)];
            return CaptureDeactivations(session => keys.Contains(session.SessionKey));
        }
    }

    internal IReadOnlyList<Deactivation> CaptureEnded(IReadOnlyList<RoutineProcessSnapshot> snapshots)
    {
        lock (_sync)
        {
            HashSet<string> keys = [.. AppRoutineStatefulCoordinator.GetEndedAppStartSessionKeys(_sessions, snapshots)];
            return CaptureDeactivations(session => keys.Contains(session.SessionKey));
        }
    }

    internal IReadOnlyList<Deactivation> CaptureDeactivations(Func<RoutineStatefulSession, bool> predicate)
    {
        lock (_sync)
        {
            long latest = AppRoutineStatefulCoordinator.GetLatestActivationSequence(_sessions.Values);
            return [.. _sessions.Values.Where(predicate).OrderByDescending(static session => session.ActivationSequence)
                .Select(session => new Deactivation(session, latest, _revision))];
        }
    }

    internal async Task DeactivateAsync(Deactivation deactivation, Func<RoutineStatefulSession, bool, Task> apply)
    {
        await _audioGate.WaitAsync();
        try
        {
            // Keep the audio gate until native cleanup and the caller's teardown both finish. Decide ownership
            // inside the background work so a queued deactivation cannot use an earlier session snapshot.
            bool? shouldRestore = await Task.Run<bool?>(() =>
            {
                bool restore;
                RoutineAudioRestoration? muteRestoration;
                lock (_sync)
                {
                    RoutineStatefulSession expected = deactivation.Session;
                    if (!_sessions.TryGetValue(expected.SessionKey, out RoutineStatefulSession? current) || !ReferenceEquals(current, expected))
                        return null;
                    restore = !_sessions.Values.Any(other => !ReferenceEquals(other, expected) && string.Equals(other.RoutineId, expected.RoutineId, StringComparison.OrdinalIgnoreCase)) && expected.RestorePreviousAudioOnDeactivate && expected.ActivationSequence == deactivation.LatestSequence &&
                        deactivation.Revision == _revision;
                    _sessions.Remove(expected.SessionKey);
                    muteRestoration = expected.RestoreSnapshot?.AudioRestoration;
                    if (muteRestoration != null && _sessions.Values.Any(other => ReferenceEquals(other.RestoreSnapshot?.AudioRestoration, muteRestoration)))
                        muteRestoration = null;
                }
                muteRestoration?.Complete(restore);
                return restore;
            });
            if (shouldRestore.HasValue)
                await apply(deactivation.Session, shouldRestore.Value);
        }
        finally { _audioGate.Release(); }
    }

    internal static bool IsSameProcess(RoutineProcessSnapshot expected, RoutineProcessSnapshot current)
    {
        return expected.ProcessId == current.ProcessId &&
            string.Equals(expected.ExecutablePath, current.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            (!expected.StartTimeUtcTicks.HasValue || !current.StartTimeUtcTicks.HasValue || expected.StartTimeUtcTicks == current.StartTimeUtcTicks);
    }

    internal async Task DrainAsync()
    {
        await _audioGate.WaitAsync();
        _audioGate.Release();
    }

    internal async Task<bool> ApplyRoutingAsync(Func<bool> isCurrent, Func<Task<bool>> apply)
    {
        await _audioGate.WaitAsync();
        try
        {
            lock (_sync)
                if (_stopping) return false;
            return isCurrent() && await apply();
        }
        finally { _audioGate.Release(); }
    }

    internal void Stop()
    {
        lock (_sync)
        {
            _stopping = true;
            _claims.Clear();
            if (_processMonitor != null)
            {
                _processMonitor.ProcessStarted -= _onStarted;
                _processMonitor.ProcessStopped -= _onStopped;
                _processMonitor = null;
                _onStarted = null;
                _onStopped = null;
            }
            foreach (Activation activation in _activations.Values.ToArray())
                activation.Cancellation.Cancel();
        }
    }
}
