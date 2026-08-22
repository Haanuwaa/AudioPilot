using System.IO;
using AudioPilot.Helpers;
using AudioPilot.Models;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.Services.Routines;

internal static class RoutineApplicationRouting
{
    private static readonly TimeSpan RoutineAppOutputLeaseStartupGracePeriod = TimeSpan.FromSeconds(5);
    internal static IReadOnlyList<RoutineAppStartMatch> EvaluateRoutineAppStartMatchesForProcess(
        IReadOnlyList<AudioRoutine> watchedRoutines,
        RoutineAppStartProcessSnapshot processSnapshot)
    {
        var matches = new List<RoutineAppStartMatch>();

        foreach (AudioRoutine routine in watchedRoutines)
        {
            if (RoutineTriggerPathHelper.LooksLikeExecutablePath(routine.TriggerAppPath))
            {
                string normalizedExecutablePath = RoutineTriggerPathHelper.NormalizeExecutablePath(processSnapshot.ExecutablePath);
                if (string.IsNullOrWhiteSpace(normalizedExecutablePath) ||
                    !RoutineTriggerPathHelper.IsExecutableProcessMatch(
                        normalizedExecutablePath,
                        routine.TriggerAppPath,
                        Path.GetFileNameWithoutExtension(normalizedExecutablePath)))
                {
                    continue;
                }
            }
            else if (RoutineTriggerPathHelper.LooksLikePackagedAppId(routine.TriggerAppPath))
            {
                if (!RoutineTriggerPathHelper.IsPackagedAppMatch(routine.TriggerAppPath, processSnapshot.AppUserModelId) &&
                    !RoutineTriggerPathHelper.IsPackagedAppExecutablePathMatch(routine.TriggerAppPath, processSnapshot.ExecutablePath))
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            matches.Add(new RoutineAppStartMatch(routine, processSnapshot.ProcessId));
        }

        return matches;
    }

    internal static bool ShouldCaptureProcessSnapshotsForStartedMatches(
        IReadOnlyList<RoutineAppStartMatch> matches,
        int activeLeaseCount)
    {
        ArgumentNullException.ThrowIfNull(matches);

        return activeLeaseCount > 0 && matches.Any(static match => match.Routine.SwitchOutputPerApp);
    }

    internal static bool ShouldCaptureProcessSnapshotsForStoppedProcess(
        int activeLeaseCount,
        int activeAppStartStatefulSessionCount)
    {
        return activeLeaseCount > 0 || activeAppStartStatefulSessionCount > 0;
    }

    internal static Dictionary<string, RoutineAppOutputLease> SynchronizeRoutineAppOutputLeasesWithWatchedRoutines(
        IReadOnlyDictionary<string, RoutineAppOutputLease> currentLeases,
        IReadOnlyList<AudioRoutine> watchedRoutines)
    {
        Dictionary<string, AudioRoutine> routinesById = watchedRoutines
            .Where(static routine =>
                routine.Enabled &&
                routine.SwitchOutputPerApp &&
                RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TargetAppPath) &&
                (!string.IsNullOrWhiteSpace(routine.OutputDeviceId) || !string.IsNullOrWhiteSpace(routine.InputDeviceId)))
            .GroupBy(static routine => string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var synchronized = new Dictionary<string, RoutineAppOutputLease>(StringComparer.OrdinalIgnoreCase);
        foreach ((string leaseKey, RoutineAppOutputLease lease) in currentLeases)
        {
            if (!routinesById.TryGetValue(lease.RoutineId, out AudioRoutine? routine))
            {
                continue;
            }

            string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TargetAppPath);
            if (string.IsNullOrWhiteSpace(normalizedTriggerPath) || !string.Equals(lease.TriggerAppPath, normalizedTriggerPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RoutineAppOutputLease updatedLease = lease.Clone();
            bool outputTargetChanged = !string.Equals(updatedLease.OutputDeviceId, routine.OutputDeviceId, StringComparison.OrdinalIgnoreCase);
            bool inputTargetChanged = !string.Equals(updatedLease.InputDeviceId, routine.InputDeviceId, StringComparison.OrdinalIgnoreCase);
            if (outputTargetChanged || inputTargetChanged || !string.Equals(updatedLease.TriggerAppPath, normalizedTriggerPath, StringComparison.OrdinalIgnoreCase))
                updatedLease.Generation = Guid.NewGuid();
            updatedLease.RoutineName = routine.Name;
            updatedLease.TriggerAppPath = normalizedTriggerPath;
            updatedLease.OutputDeviceId = routine.OutputDeviceId;
            updatedLease.OutputDeviceName = routine.OutputDeviceName;
            updatedLease.InputDeviceId = routine.InputDeviceId;
            updatedLease.InputDeviceName = routine.InputDeviceName;
            if (outputTargetChanged)
            {
                updatedLease.AppliedOutputProcessIds.Clear();
            }

            if (inputTargetChanged)
            {
                updatedLease.AppliedInputProcessIds.Clear();
            }

            if (outputTargetChanged || inputTargetChanged)
            {
                updatedLease.CompletionOverlayShown = false;
            }

            synchronized[leaseKey] = updatedLease;
        }

        return synchronized;
    }

    internal static Dictionary<string, RoutineAppOutputLease> ReconcileRoutineAppOutputLeases(
        IReadOnlyDictionary<string, RoutineAppOutputLease> currentLeases,
        IReadOnlyList<AudioRoutine> watchedRoutines,
        IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots,
        DateTime? nowUtc = null)
    {
        return ReconcileRoutineAppOutputLeases(
            currentLeases,
            watchedRoutines,
            CreateRoutineAppStartSnapshotSet(processSnapshots),
            nowUtc);
    }

    internal static Dictionary<string, RoutineAppOutputLease> ReconcileRoutineAppOutputLeases(
        IReadOnlyDictionary<string, RoutineAppOutputLease> currentLeases,
        IReadOnlyList<AudioRoutine> watchedRoutines,
        RoutineAppStartSnapshotSet processSnapshotSet,
        DateTime? nowUtc = null)
    {
        Dictionary<string, RoutineAppOutputLease> synchronized = SynchronizeRoutineAppOutputLeasesWithWatchedRoutines(currentLeases, watchedRoutines);
        var reconciled = new Dictionary<string, RoutineAppOutputLease>(StringComparer.OrdinalIgnoreCase);
        DateTime comparisonUtc = nowUtc ?? DateTime.UtcNow;

        foreach ((string leaseKey, RoutineAppOutputLease lease) in synchronized)
        {
            if (lease.ProcessIdentity is RoutineAppStartProcessSnapshot identity &&
                processSnapshotSet.SnapshotsByPid.TryGetValue(lease.RootProcessId, out RoutineAppStartProcessSnapshot current) &&
                !RoutineLifetimeService.IsSameProcess(identity, current)) continue;

            if (IsRoutineAppOutputLeaseAlive(lease.RootProcessId, lease.TriggerAppPath, processSnapshotSet) ||
                ShouldRetainPendingRoutineAppOutputLeaseDuringStartupGrace(lease, comparisonUtc))
            {
                reconciled[leaseKey] = lease;
            }
        }

        return reconciled;
    }

    internal static bool ShouldRetainPendingRoutineAppOutputLeaseDuringStartupGrace(RoutineAppOutputLease lease, DateTime nowUtc)
    {
        return !HasRoutineAppOutputLeaseCompleted(lease) &&
            nowUtc - lease.CreatedUtc <= RoutineAppOutputLeaseStartupGracePeriod;
    }

    internal static bool IsRoutineAppOutputLeaseAlive(
        int rootProcessId,
        string triggerAppPath,
        IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
    {
        return IsRoutineAppOutputLeaseAlive(
            rootProcessId,
            triggerAppPath,
            CreateRoutineAppStartSnapshotSet(processSnapshots));
    }

    internal static bool IsRoutineAppOutputLeaseAlive(
        int rootProcessId,
        string triggerAppPath,
        RoutineAppStartSnapshotSet processSnapshotSet)
    {
        string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(triggerAppPath);
        if (rootProcessId <= 0 || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(normalizedTriggerPath))
        {
            return false;
        }

        bool rootProcessAlive = DoesRoutineAppLeaseRootMatchTriggerTarget(
            rootProcessId,
            normalizedTriggerPath,
            processSnapshotSet.SnapshotsByPid);

        return processSnapshotSet.Snapshots.Any(snapshot =>
            IsRoutineAppOutputCandidateProcess(
                snapshot.ProcessId,
                rootProcessId,
                processSnapshotSet.SnapshotsByPid,
                rootProcessAlive));
    }

    internal static IReadOnlyList<uint> CollectRoutineAppOutputCandidateProcessIds(
        int rootProcessId,
        string triggerAppPath,
        IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots,
        IReadOnlyList<AudioSessionSnapshot> sessionSnapshots)
    {
        return CollectRoutineAppOutputCandidateProcessIds(
            rootProcessId,
            triggerAppPath,
            CreateRoutineAppStartSnapshotSet(processSnapshots),
            sessionSnapshots);
    }

    internal static IReadOnlyList<uint> CollectRoutineAppOutputCandidateProcessIds(
        int rootProcessId,
        string triggerAppPath,
        RoutineAppStartSnapshotSet processSnapshotSet,
        IReadOnlyList<AudioSessionSnapshot> sessionSnapshots)
    {
        string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(triggerAppPath);
        if (rootProcessId <= 0 || !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(normalizedTriggerPath))
        {
            return [];
        }

        bool rootProcessAlive = DoesRoutineAppLeaseRootMatchTriggerTarget(
            rootProcessId,
            normalizedTriggerPath,
            processSnapshotSet.SnapshotsByPid);
        var candidateProcessIds = new HashSet<uint>();
        if (rootProcessAlive)
        {
            candidateProcessIds.Add((uint)rootProcessId);
        }

        foreach (RoutineAppStartProcessSnapshot snapshot in processSnapshotSet.Snapshots)
        {
            if (IsRoutineAppOutputCandidateProcess(snapshot.ProcessId, rootProcessId, processSnapshotSet.SnapshotsByPid, rootProcessAlive))
            {
                candidateProcessIds.Add((uint)snapshot.ProcessId);
            }
        }

        foreach (AudioSessionSnapshot sessionSnapshot in sessionSnapshots)
        {
            if (!sessionSnapshot.ProcessId.HasValue || sessionSnapshot.ProcessId.Value == 0 || sessionSnapshot.ProcessId.Value > int.MaxValue)
            {
                continue;
            }

            int sessionProcessId = (int)sessionSnapshot.ProcessId.Value;
            if (IsRoutineAppOutputCandidateProcess(
                sessionProcessId,
                rootProcessId,
                processSnapshotSet.SnapshotsByPid,
                rootProcessAlive))
            {
                candidateProcessIds.Add((uint)sessionProcessId);
            }
        }

        return [.. candidateProcessIds.OrderBy(static processId => processId)];
    }

    internal static RoutineAppStartSnapshotSet CreateRoutineAppStartSnapshotSet(
        IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
    {
        ArgumentNullException.ThrowIfNull(processSnapshots);

        var snapshotsByPid = new Dictionary<int, RoutineAppStartProcessSnapshot>();
        foreach (RoutineAppStartProcessSnapshot snapshot in processSnapshots)
        {
            if (snapshot.ProcessId > 0)
            {
                snapshotsByPid[snapshot.ProcessId] = snapshot;
            }
        }

        return new RoutineAppStartSnapshotSet(processSnapshots, snapshotsByPid);
    }

    internal static int? FindRunningRoutineTriggerProcessId(
        AudioRoutine routine,
        IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
    {
        if (routine == null || !routine.HasApplicationTrigger)
        {
            return null;
        }

        if (RoutineTriggerPathHelper.LooksLikeExecutablePath(routine.TriggerAppPath))
        {
            string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeExecutablePath(routine.TriggerAppPath);
            if (string.IsNullOrWhiteSpace(normalizedTriggerPath))
            {
                return null;
            }

            return processSnapshots
                .Where(snapshot =>
                    snapshot.ProcessId > 0 &&
                    RoutineTriggerPathHelper.IsExecutableProcessMatch(
                        snapshot.ExecutablePath,
                        normalizedTriggerPath,
                        Path.GetFileNameWithoutExtension(snapshot.ExecutablePath)))
                .OrderBy(static snapshot => snapshot.ProcessId)
                .Select(static snapshot => (int?)snapshot.ProcessId)
                .FirstOrDefault();
        }

        if (RoutineTriggerPathHelper.LooksLikePackagedAppId(routine.TriggerAppPath))
        {
            return processSnapshots
                .Where(snapshot =>
                    snapshot.ProcessId > 0 &&
                    (RoutineTriggerPathHelper.IsPackagedAppMatch(routine.TriggerAppPath, snapshot.AppUserModelId) ||
                        RoutineTriggerPathHelper.IsPackagedAppExecutablePathMatch(routine.TriggerAppPath, snapshot.ExecutablePath)))
                .OrderBy(static snapshot => snapshot.ProcessId)
                .Select(static snapshot => (int?)snapshot.ProcessId)
                .FirstOrDefault();
        }

        return null;
    }

    internal static IReadOnlyList<int> FindRunningProcessIdsForTriggerTarget(
        string triggerTarget,
        IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
    {
        string normalizedTriggerTarget = RoutineTriggerPathHelper.NormalizeTriggerTarget(triggerTarget);
        if (!RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(normalizedTriggerTarget))
        {
            return [];
        }

        return
        [
            .. processSnapshots
                .Where(snapshot => snapshot.ProcessId > 0 && IsRoutineAppDirectMatch(normalizedTriggerTarget, snapshot))
                .OrderBy(static snapshot => snapshot.ProcessId)
                .Select(static snapshot => snapshot.ProcessId)
        ];
    }

    internal static bool IsRoutineAppOutputCandidateProcess(
        int processId,
        int rootProcessId,
        Dictionary<int, RoutineAppStartProcessSnapshot> snapshotsByPid,
        bool rootProcessAlive)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (processId == rootProcessId)
        {
            return rootProcessAlive;
        }

        if (!rootProcessAlive)
        {
            return false;
        }

        return IsDescendantProcess(processId, rootProcessId, snapshotsByPid);
    }

    internal static bool DoesRoutineAppLeaseRootMatchTriggerTarget(
        int rootProcessId,
        string triggerAppPath,
        Dictionary<int, RoutineAppStartProcessSnapshot> snapshotsByPid)
    {
        if (rootProcessId <= 0 || !snapshotsByPid.TryGetValue(rootProcessId, out RoutineAppStartProcessSnapshot rootSnapshot))
        {
            return false;
        }

        return IsRoutineAppDirectMatch(triggerAppPath, rootSnapshot);
    }

    internal static bool IsRoutineAppDirectMatch(
        string triggerTarget,
        RoutineAppStartProcessSnapshot processSnapshot)
    {
        if (RoutineTriggerPathHelper.LooksLikeExecutablePath(triggerTarget))
        {
            return RoutineTriggerPathHelper.IsExecutableProcessMatch(
                processSnapshot.ExecutablePath,
                triggerTarget,
                Path.GetFileNameWithoutExtension(processSnapshot.ExecutablePath));
        }

        if (RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerTarget))
        {
            return RoutineTriggerPathHelper.IsPackagedAppMatch(triggerTarget, processSnapshot.AppUserModelId) ||
                RoutineTriggerPathHelper.IsPackagedAppExecutablePathMatch(triggerTarget, processSnapshot.ExecutablePath);
        }

        return false;
    }

    internal static bool IsDescendantProcess(
        int processId,
        int rootProcessId,
        Dictionary<int, RoutineAppStartProcessSnapshot> snapshotsByPid)
    {
        var visitedProcessIds = new HashSet<int> { processId };
        int currentProcessId = processId;

        for (int depth = 0; depth < 12; depth++)
        {
            int parentProcessId;
            if (snapshotsByPid.TryGetValue(currentProcessId, out RoutineAppStartProcessSnapshot snapshot) && snapshot.ParentProcessId is > 0)
            {
                parentProcessId = snapshot.ParentProcessId.Value;
            }
            else
            {
                parentProcessId = AudioDeviceHelper.GetParentPid(currentProcessId);
            }

            if (parentProcessId <= 0)
            {
                return false;
            }

            if (parentProcessId == rootProcessId)
            {
                return true;
            }

            if (!visitedProcessIds.Add(parentProcessId))
            {
                return false;
            }

            currentProcessId = parentProcessId;
        }

        return false;
    }

    internal static bool HasRoutineAppOutputLeaseCompleted(RoutineAppOutputLease lease)
    {
        bool outputCompleted = !IsRoutineAppOutputLeaseOutputPending(lease);
        bool inputCompleted = !IsRoutineAppOutputLeaseInputPending(lease);
        return outputCompleted && inputCompleted;
    }

    internal static void ApplyInitialLeaseProcessState(RoutineAppOutputLease lease, uint rootProcessId, bool outputApplied, bool inputApplied)
    {
        if (outputApplied && !string.IsNullOrWhiteSpace(lease.OutputDeviceId))
        {
            lease.AppliedOutputProcessIds.Add(rootProcessId);
        }

        if (inputApplied && !string.IsNullOrWhiteSpace(lease.InputDeviceId))
        {
            lease.AppliedInputProcessIds.Add(rootProcessId);
        }
    }

    internal static bool ShouldSkipRoutineAppStartMatchForExistingLease(
        RoutineAppStartMatch match,
        RoutineAppStartProcessSnapshot processSnapshot,
        IReadOnlyList<RoutineAppOutputLease> activeLeases,
        IReadOnlyList<RoutineAppStartProcessSnapshot> processSnapshots)
    {
        return ShouldSkipRoutineAppStartMatchForExistingLease(
            match,
            processSnapshot,
            activeLeases,
            CreateRoutineAppStartSnapshotSet(processSnapshots));
    }

    internal static bool ShouldSkipRoutineAppStartMatchForExistingLease(
        RoutineAppStartMatch match,
        RoutineAppStartProcessSnapshot processSnapshot,
        IReadOnlyList<RoutineAppOutputLease> activeLeases,
        RoutineAppStartSnapshotSet processSnapshotSet)
    {
        if (!match.Routine.SwitchOutputPerApp || processSnapshot.ProcessId <= 0)
        {
            return false;
        }

        if (activeLeases.Count == 0)
        {
            return false;
        }

        foreach (RoutineAppOutputLease lease in activeLeases)
        {
            if (!string.Equals(lease.RoutineId, match.Routine.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lease.ProcessIdentity is RoutineAppStartProcessSnapshot identity &&
                processSnapshotSet.SnapshotsByPid.TryGetValue(lease.RootProcessId, out RoutineAppStartProcessSnapshot current) &&
                !RoutineLifetimeService.IsSameProcess(identity, current)) continue;
            bool rootProcessAlive = DoesRoutineAppLeaseRootMatchTriggerTarget(
                lease.RootProcessId,
                RoutineTriggerPathHelper.NormalizeTriggerTarget(lease.TriggerAppPath),
                processSnapshotSet.SnapshotsByPid);
            if (IsRoutineAppOutputCandidateProcess(
                processSnapshot.ProcessId,
                lease.RootProcessId,
                processSnapshotSet.SnapshotsByPid,
                rootProcessAlive))
            {
                return true;
            }
        }

        return false;
    }

    internal static string CreateRoutineAppOutputLeaseKey(string routineId, int rootProcessId)
    {
        string normalizedRoutineId = NormalizeRoutineAppStartRoutineId(routineId);
        return $"{normalizedRoutineId}:{rootProcessId}";
    }

    internal static string NormalizeRoutineAppStartRoutineId(string? routineId)
    {
        return string.IsNullOrWhiteSpace(routineId) ? "unknown" : routineId;
    }

    internal static bool IsRoutineAppOutputLeaseOutputPending(RoutineAppOutputLease lease)
    {
        return !string.IsNullOrWhiteSpace(lease.OutputDeviceId) && lease.AppliedOutputProcessIds.Count == 0;
    }

    internal static bool IsRoutineAppOutputLeaseInputPending(RoutineAppOutputLease lease)
    {
        return !string.IsNullOrWhiteSpace(lease.InputDeviceId) && lease.AppliedInputProcessIds.Count == 0;
    }

}
