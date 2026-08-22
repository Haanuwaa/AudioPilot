using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using static AudioPilot.Services.Routines.RoutineApplicationRouting;

namespace AudioPilot.Services.Routines;

internal sealed partial class RoutineLifetimeService
{
    internal readonly record struct LeaseRegistration(RoutineAppOutputLease Lease, bool Created, bool OutputChanged, bool InputChanged);

    private Dictionary<string, RoutineAppOutputLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingOutput;
    private int _pendingInput;

    internal Dictionary<string, RoutineAppOutputLease> LeasesForTests => _leases;

    internal int LeaseCount { get { lock (_sync) return _leases.Count; } }

    internal (int Output, int Input) PendingLeaseCounts { get { lock (_sync) return (_pendingOutput, _pendingInput); } }

    internal IReadOnlyList<RoutineAppOutputLease> GetLeases()
    {
        lock (_sync) return [.. _leases.Values.Select(static lease => lease.Clone())];
    }

    internal void SynchronizeLeases(IReadOnlyList<AudioRoutine> routines)
    {
        lock (_sync)
        {
            _leases = SynchronizeRoutineAppOutputLeasesWithWatchedRoutines(_leases, routines);
            RecalculateLeaseCounts();
        }
    }

    internal RoutineAppOutputLeaseRefreshPreparation PrepareLeaseRefresh(IReadOnlyList<AudioRoutine> routines, IReadOnlyList<RoutineProcessSnapshot> snapshots)
    {
        lock (_sync)
        {
            RoutineAppOutputLeaseRefreshPreparation result = AppRoutineAppStartCoordinator.PrepareLeaseRefresh(_leases, routines, snapshots, DateTime.UtcNow);
            _leases = result.ReconciledLeases;
            RecalculateLeaseCounts();
            return result;
        }
    }

    internal (RoutineAppOutputLease Lease, bool ResetOutput, bool ResetInput)? ReleaseSessionRouting(RoutineStatefulSession session, bool shouldRestore)
    {
        if (session.RoutingLease is not { } expected) return null;
        lock (_sync)
        {
            if (!_leases.TryGetValue(expected.LeaseKey, out var current) ||
                !string.Equals(current.TriggerAppPath, expected.TriggerAppPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.OutputDeviceId, expected.OutputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.InputDeviceId, expected.InputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                (expected.ProcessIdentity is { } expectedIdentity && current.ProcessIdentity is { } currentIdentity && !IsSameProcess(expectedIdentity, currentIdentity)) ||
                _sessions.Values.Any(active => string.Equals(active.RoutingLease?.LeaseKey, current.LeaseKey, StringComparison.OrdinalIgnoreCase))) return null;
            _leases.Remove(current.LeaseKey);
            RecalculateLeaseCounts();
            bool output = shouldRestore && !string.IsNullOrWhiteSpace(current.OutputDeviceId);
            bool input = shouldRestore && !string.IsNullOrWhiteSpace(current.InputDeviceId);
            foreach (var other in _leases.Values)
            {
                if (other.RootProcessId != current.RootProcessId) continue;
                if (!string.IsNullOrWhiteSpace(other.OutputDeviceId)) output = false;
                if (!string.IsNullOrWhiteSpace(other.InputDeviceId)) input = false;
            }
            return (current.Clone(), output, input);
        }
    }

    internal LeaseRegistration? RegisterLease(AudioRoutine routine, int processId, bool outputApplied, bool inputApplied, bool overlayShown, RoutineProcessSnapshot? processIdentity = null, CancellationToken cancellationToken = default)
    {
        string trigger = RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TargetAppPath);
        if (processId <= 0 || string.IsNullOrWhiteSpace(trigger) || (!routine.HasOutputTarget && !routine.HasInputTarget))
            return null;
        string routineId = NormalizeRoutineAppStartRoutineId(routine.Id);
        string key = CreateRoutineAppOutputLeaseKey(routineId, processId);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping) return null;
            bool created = !_leases.TryGetValue(key, out RoutineAppOutputLease? previous);
            bool outputChanged = previous != null && !string.Equals(previous.OutputDeviceId, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase);
            bool inputChanged = previous != null && !string.Equals(previous.InputDeviceId, routine.InputDeviceId, StringComparison.OrdinalIgnoreCase);
            var lease = new RoutineAppOutputLease(key, routineId, routine.Name, processId, trigger,
                routine.OutputDeviceId, routine.OutputDeviceName, routine.InputDeviceId, routine.InputDeviceName)
            {
                CompletionOverlayShown = overlayShown,
                ProcessIdentity = processIdentity,
                CreatedUtc = previous?.CreatedUtc ?? DateTime.UtcNow,
            };
            if (previous != null)
            {
                if (!outputChanged && outputApplied) lease.AppliedOutputProcessIds.UnionWith(previous.AppliedOutputProcessIds);
                if (!inputChanged && inputApplied) lease.AppliedInputProcessIds.UnionWith(previous.AppliedInputProcessIds);
            }
            ApplyInitialLeaseProcessState(lease, (uint)processId, outputApplied, inputApplied);
            _leases[key] = lease;
            RecalculateLeaseCounts();
            return new(lease.Clone(), created, outputChanged, inputChanged);
        }
    }

    internal RoutineAppOutputLease? FindLease(string key)
    {
        lock (_sync) return _leases.TryGetValue(key, out RoutineAppOutputLease? lease) ? lease.Clone() : null;
    }

    internal RoutineAppOutputLease? RemoveLease(string key, Guid? expectedGeneration = null)
    {
        lock (_sync)
        {
            if (!_leases.TryGetValue(key, out RoutineAppOutputLease? lease) ||
                (expectedGeneration.HasValue && expectedGeneration != lease.Generation)) return null;
            _leases.Remove(key);
            RecalculateLeaseCounts();
            return lease.Clone();
        }
    }

    internal bool IsLeaseCurrent(RoutineAppOutputLease expected)
    {
        lock (_sync) return _leases.TryGetValue(expected.LeaseKey, out RoutineAppOutputLease? current) &&
            AppRoutineAppStartCoordinator.DoesLiveLeaseMatchExpectedSnapshot(current, expected);
    }

    internal void MarkLeaseProcessApplied(RoutineAppOutputLease expected, uint processId, bool output)
    {
        lock (_sync)
        {
            if (!IsLeaseCurrent(expected)) return;
            RoutineAppOutputLease lease = _leases[expected.LeaseKey];
            bool wasPending = output ? IsRoutineAppOutputLeaseOutputPending(lease) : IsRoutineAppOutputLeaseInputPending(lease);
            (output ? lease.AppliedOutputProcessIds : lease.AppliedInputProcessIds).Add(processId);
            if (wasPending)
            {
                if (output) _pendingOutput--;
                else _pendingInput--;
            }
        }
    }

    internal void MarkLeaseOverlayShown(RoutineAppOutputLease expected)
    {
        lock (_sync)
            if (IsLeaseCurrent(expected)) _leases[expected.LeaseKey].CompletionOverlayShown = true;
    }

    internal void RecalculateLeaseCounts()
    {
        lock (_sync)
        {
            _pendingOutput = _leases.Values.Count(IsRoutineAppOutputLeaseOutputPending);
            _pendingInput = _leases.Values.Count(IsRoutineAppOutputLeaseInputPending);
        }
    }
}
