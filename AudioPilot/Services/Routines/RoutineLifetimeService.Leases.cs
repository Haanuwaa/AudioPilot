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
    private readonly HashSet<RoutineAppOutputLease> _routingRestorations = [];

    internal bool HasRoutingOwnership { get { lock (_sync) return _leases.Count > 0 || _routingRestorations.Count > 0; } }

    internal void MarkManualRouting(uint processId, bool output, IReadOnlyList<RoutineProcessSnapshot> processes)
    {
        var byPid = processes.GroupBy(process => process.ProcessId).ToDictionary(group => group.Key, group => group.First());
        lock (_sync)
        {
            foreach (var lease in _leases.Values.Concat(_routingRestorations))
            {
                bool affected = lease.RootProcessId == processId || lease.AppliedOutputProcessIds.Contains(processId) || lease.AppliedInputProcessIds.Contains(processId)
                    || RoutineApplicationRouting.IsRoutineAppOutputCandidateProcess(checked((int)processId), lease.RootProcessId, byPid, byPid.ContainsKey(lease.RootProcessId));
                if (!affected) continue;
                if (output) lease.OutputManuallyOverridden = true;
                else lease.InputManuallyOverridden = true;
                lease.Generation = Guid.NewGuid();
            }
            RecalculateLeaseCounts();
        }
    }

    internal void CompleteRoutingRestoration(RoutineAppOutputLease lease) { lock (_sync) _routingRestorations.Remove(lease); }

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

    internal IReadOnlyList<(RoutineAppOutputLease Lease, bool ResetOutput, bool ResetInput)> ReleaseSessionRouting(RoutineStatefulSession session, bool shouldRestore)
    {
        if (session.RoutingLease is not { } expected) return [];
        lock (_sync)
        {
            if (_sessions.Values.Any(active => string.Equals(active.RoutineId, session.RoutineId, StringComparison.OrdinalIgnoreCase))) return [];
            List<(RoutineAppOutputLease Lease, bool ResetOutput, bool ResetInput)> plans = [];
            foreach (RoutineAppOutputLease current in _leases.Values.ToArray())
            {
                if (current.AutomaticActivationSequence != session.ActivationSequence ||
                    !string.Equals(current.RoutineId, session.RoutineId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.TriggerAppPath, expected.TriggerAppPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.OutputDeviceId, expected.OutputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.InputDeviceId, expected.InputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.OutputDeviceStableId, expected.OutputDeviceStableId, StringComparison.Ordinal) ||
                    !string.Equals(current.InputDeviceStableId, expected.InputDeviceStableId, StringComparison.Ordinal) ||
                    (current.RootProcessId == expected.RootProcessId && expected.ProcessIdentity is { } expectedIdentity &&
                        current.ProcessIdentity is { } currentIdentity && !IsSameProcess(expectedIdentity, currentIdentity))) continue;
                _leases.Remove(current.LeaseKey);
                bool output = shouldRestore && !current.OutputManuallyOverridden && !string.IsNullOrWhiteSpace(current.OutputDeviceId);
                bool input = shouldRestore && !current.InputManuallyOverridden && !string.IsNullOrWhiteSpace(current.InputDeviceId);
                foreach (var other in _leases.Values)
                {
                    if (other.RootProcessId != current.RootProcessId) continue;
                    if (!string.IsNullOrWhiteSpace(other.OutputDeviceId)) output = false;
                    if (!string.IsNullOrWhiteSpace(other.InputDeviceId)) input = false;
                }
                var restoring = current.Clone();
                if (output || input) _routingRestorations.Add(restoring);
                plans.Add((restoring, output, input));
            }
            RecalculateLeaseCounts();
            return plans;
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
            bool outputChanged = previous != null && (!string.Equals(previous.OutputDeviceId, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.OutputDeviceStableId, routine.OutputDeviceStableId, StringComparison.Ordinal));
            bool inputChanged = previous != null && (!string.Equals(previous.InputDeviceId, routine.InputDeviceId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.InputDeviceStableId, routine.InputDeviceStableId, StringComparison.Ordinal));
            var lease = new RoutineAppOutputLease(key, routineId, routine.Name, processId, trigger,
                routine.OutputDeviceId, routine.OutputDeviceName, routine.InputDeviceId, routine.InputDeviceName)
            {
                OutputDeviceStableId = routine.OutputDeviceStableId,
                InputDeviceStableId = routine.InputDeviceStableId,
                CompletionOverlayShown = overlayShown,
                ProcessIdentity = processIdentity,
                CreatedUtc = previous?.CreatedUtc ?? DateTime.UtcNow,
            };
            if (previous != null)
            {
                if (!outputChanged && outputApplied && !previous.OutputManuallyOverridden) lease.AppliedOutputProcessIds.UnionWith(previous.AppliedOutputProcessIds);
                if (!inputChanged && inputApplied && !previous.InputManuallyOverridden) lease.AppliedInputProcessIds.UnionWith(previous.AppliedInputProcessIds);
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

    internal RoutineAppOutputLease? RemoveLease(string key, Guid? expectedGeneration = null, bool retainForRestore = false)
    {
        lock (_sync)
        {
            if (!_leases.TryGetValue(key, out RoutineAppOutputLease? lease) ||
                (expectedGeneration.HasValue && expectedGeneration != lease.Generation)) return null;
            _leases.Remove(key);
            RecalculateLeaseCounts();
            var removed = lease.Clone();
            if (retainForRestore) _routingRestorations.Add(removed);
            return removed;
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
