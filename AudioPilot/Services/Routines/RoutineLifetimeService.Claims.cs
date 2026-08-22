using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using static AudioPilot.Services.Routines.RoutineApplicationRouting;

namespace AudioPilot.Services.Routines;

internal sealed partial class RoutineLifetimeService
{
    internal sealed record ActivationClaim(string Key, AudioRoutine Routine, RoutineProcessSnapshot Process);

    private readonly Dictionary<string, ActivationClaim> _claims = new(StringComparer.OrdinalIgnoreCase);

    internal bool TryClaim(AudioRoutine routine, RoutineProcessSnapshot process, out ActivationClaim? claim, out string failureReason)
    {
        string key = CreateRoutineAppOutputLeaseKey(routine.RuntimeTriggerKey, process.ProcessId);
        string leaseKey = CreateRoutineAppOutputLeaseKey(routine.Id, process.ProcessId);
        claim = null;
        failureReason = string.Empty;
        lock (_sync)
        {
            if (_stopping)
            {
                failureReason = "shutdown";
                return false;
            }
            if (!_sessions.Values.Any(session => string.Equals(session.RoutineId, routine.Id, StringComparison.OrdinalIgnoreCase)) &&
                _leases.TryGetValue(leaseKey, out RoutineAppOutputLease? existing))
            {
                if (IsRoutineAppDirectMatch(RoutineTriggerPathHelper.NormalizeTriggerTarget(existing.TriggerAppPath), process) &&
                    (existing.ProcessIdentity is not RoutineProcessSnapshot identity || IsSameProcess(identity, process)))
                {
                    failureReason = "existing-active-lease";
                    return false;
                }
                RemoveLease(leaseKey);
            }
            if (_claims.TryGetValue(key, out ActivationClaim? pending) && IsSameProcess(pending.Process, process))
            {
                failureReason = "claim-unavailable";
                return false;
            }
            claim = new(key, routine.Clone(), process);
            _claims[key] = claim;
            return true;
        }
    }

    internal void ReleaseClaim(ActivationClaim claim)
    {
        lock (_sync)
            if (_claims.TryGetValue(claim.Key, out ActivationClaim? current) && ReferenceEquals(current, claim))
                _claims.Remove(claim.Key);
    }

    private void SynchronizeClaims(IReadOnlyList<AudioRoutine> routines)
    {
        foreach (ActivationClaim claim in _claims.Values.ToArray())
            if (!routines.Any(routine => routine.Enabled && string.Equals(routine.RuntimeTriggerKey, claim.Routine.RuntimeTriggerKey, StringComparison.OrdinalIgnoreCase) &&
                AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine) ==
                AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(claim.Routine)))
                _claims.Remove(claim.Key);
    }
}
