using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal enum RoutineAppStartMonitorAction
    {
        None,
        Start,
        Stop,
    }

    internal readonly record struct RoutineAppStartMonitorDecision(
        RoutineAppStartMonitorAction Action,
        bool ShouldMonitor);

    internal readonly record struct RoutineAppStartProcessWorkload(
        RoutineProcessSnapshot Snapshot,
        IReadOnlyList<AppViewModel.RoutineAppStartMatch> Matches,
        bool RequiresProcessSnapshotCapture);

    internal enum RoutineAppStartMatchExecutionAction
    {
        Execute,
        SkipExistingActiveLease,
    }

    internal readonly record struct RoutineAppStartMatchExecutionPlan(
        AppViewModel.RoutineAppStartMatch Match,
        RoutineAppStartMatchExecutionAction Action);

    internal readonly record struct RoutineAppOutputLeaseRefreshPreparation(
        int PreviousLeaseCount,
        Dictionary<string, AppViewModel.RoutineAppOutputLease> ReconciledLeases,
        IReadOnlyList<AppViewModel.RoutineAppOutputLease> ActiveLeases,
        IReadOnlyList<AppViewModel.RoutineAppOutputLease> RemovedLeases);

    internal static class AppRoutineAppStartCoordinator
    {
        /// <summary>
        /// Decides whether the app-start monitor should be running based on watched routines, active leases, and
        /// stateful sessions that still depend on process lifecycle updates.
        /// </summary>
        internal static RoutineAppStartMonitorDecision ResolveMonitorDecision(
            bool monitoringEnabled,
            bool isCleaningUp,
            int watchedRoutineCount,
            int activeLeaseCount,
            bool hasActiveAppStartStatefulSessions,
            bool isRunning)
        {
            bool shouldMonitor = monitoringEnabled &&
                !isCleaningUp &&
                (watchedRoutineCount > 0 || activeLeaseCount > 0 || hasActiveAppStartStatefulSessions);

            if (!shouldMonitor)
            {
                return new RoutineAppStartMonitorDecision(
                    isRunning ? RoutineAppStartMonitorAction.Stop : RoutineAppStartMonitorAction.None,
                    ShouldMonitor: false);
            }

            return new RoutineAppStartMonitorDecision(
                !isRunning ? RoutineAppStartMonitorAction.Start : RoutineAppStartMonitorAction.None,
                ShouldMonitor: true);
        }

        /// <summary>
        /// Reconciles persisted app-output leases against the current watched routines and process snapshot set while
        /// preserving the previous lease count for change diagnostics.
        /// </summary>
        internal static RoutineAppOutputLeaseRefreshPreparation PrepareLeaseRefresh(
            IReadOnlyDictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            IReadOnlyList<RoutineProcessSnapshot> processSnapshots,
            DateTime? nowUtc = null)
        {
            return PrepareLeaseRefresh(
                currentLeases,
                watchedRoutines,
                AppViewModel.CreateRoutineAppStartSnapshotSet(processSnapshots),
                nowUtc);
        }

        internal static RoutineAppOutputLeaseRefreshPreparation PrepareLeaseRefresh(
            IReadOnlyDictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            AppViewModel.RoutineAppStartSnapshotSet processSnapshotSet,
            DateTime? nowUtc = null)
        {
            int previousLeaseCount = currentLeases.Count;
            Dictionary<string, AppViewModel.RoutineAppOutputLease> reconciledLeases = AppViewModel.ReconcileRoutineAppOutputLeases(
                currentLeases,
                watchedRoutines,
                processSnapshotSet,
                nowUtc);
            List<AppViewModel.RoutineAppOutputLease> removedLeases =
            [
                .. currentLeases
                    .Where(entry => !reconciledLeases.ContainsKey(entry.Key))
                    .Select(static entry => entry.Value.Clone())
            ];

            return new RoutineAppOutputLeaseRefreshPreparation(
                previousLeaseCount,
                reconciledLeases,
                [.. reconciledLeases.Values.Select(static lease => lease.Clone())],
                removedLeases);
        }

        /// <summary>
        /// Captures the work needed for a started-process notification only when the process still resolves to a
        /// snapshot and at least one watched routine matches it.
        /// </summary>
        /// <remarks>
        /// The returned workload also records whether the caller should capture a broader process snapshot set for
        /// lease de-duplication, which avoids that extra work when no matched routine needs it.
        /// </remarks>
        internal static async Task<RoutineAppStartProcessWorkload?> PrepareStartedProcessWorkloadAsync(
            int processId,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            int activeLeaseCount,
            Func<int, RoutineProcessSnapshot?> tryCaptureSnapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(watchedRoutines);
            ArgumentNullException.ThrowIfNull(tryCaptureSnapshot);

            RoutineProcessSnapshot? snapshot = await Task.Run(() => tryCaptureSnapshot(processId), cancellationToken);
            if (!snapshot.HasValue)
            {
                return null;
            }

            IReadOnlyList<AppViewModel.RoutineAppStartMatch> matches = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(
                watchedRoutines,
                snapshot.Value);
            if (matches.Count == 0)
            {
                return null;
            }

            return new RoutineAppStartProcessWorkload(
                snapshot.Value,
                matches,
                AppViewModel.ShouldCaptureProcessSnapshotsForStartedMatches(matches, activeLeaseCount));
        }

        internal static IReadOnlyList<RoutineAppStartMatchExecutionPlan> PlanStartedMatchExecutions(
            IReadOnlyList<AppViewModel.RoutineAppStartMatch> matches,
            RoutineProcessSnapshot processSnapshot,
            IReadOnlyList<AppViewModel.RoutineAppOutputLease> activeLeases,
            IReadOnlyList<RoutineProcessSnapshot> processSnapshots)
        {
            return PlanStartedMatchExecutions(
                matches,
                processSnapshot,
                activeLeases,
                AppViewModel.CreateRoutineAppStartSnapshotSet(processSnapshots));
        }

        internal static IReadOnlyList<RoutineAppStartMatchExecutionPlan> PlanStartedMatchExecutions(
            IReadOnlyList<AppViewModel.RoutineAppStartMatch> matches,
            RoutineProcessSnapshot processSnapshot,
            IReadOnlyList<AppViewModel.RoutineAppOutputLease> activeLeases,
            AppViewModel.RoutineAppStartSnapshotSet processSnapshotSet)
        {
            ArgumentNullException.ThrowIfNull(matches);
            ArgumentNullException.ThrowIfNull(activeLeases);

            var plans = new List<RoutineAppStartMatchExecutionPlan>(matches.Count);
            foreach (AppViewModel.RoutineAppStartMatch match in matches)
            {
                bool shouldSkip = AppViewModel.ShouldSkipRoutineAppStartMatchForExistingLease(
                    match,
                    processSnapshot,
                    activeLeases,
                    processSnapshotSet);

                plans.Add(new RoutineAppStartMatchExecutionPlan(
                    match,
                    shouldSkip ? RoutineAppStartMatchExecutionAction.SkipExistingActiveLease : RoutineAppStartMatchExecutionAction.Execute));
            }

            return plans;
        }

        /// <summary>
        /// Applies outstanding per-process lease targets and emits the completion overlay only after the lease has
        /// satisfied all required output and input work.
        /// </summary>
        /// <remarks>
        /// Each lease tracks applied process ids separately for output and input so the method can retry incomplete
        /// halves without replaying already successful work on later refreshes.
        /// </remarks>
        internal static async Task ExecuteLeaseApplicationsAsync(
            IReadOnlyList<AppViewModel.RoutineAppOutputLease> activeLeases,
            IReadOnlyList<RoutineProcessSnapshot> processSnapshots,
            IReadOnlyList<AudioSessionSnapshot> sessionSnapshots,
            Func<int, string, IReadOnlyList<RoutineProcessSnapshot>, IReadOnlyList<AudioSessionSnapshot>, IReadOnlyList<uint>> collectCandidateProcessIds,
            Func<AppViewModel.RoutineAppOutputLease, uint, Task<bool>> tryApplyOutputAsync,
            Func<AppViewModel.RoutineAppOutputLease, uint, Task<bool>> tryApplyInputAsync,
            Func<AppViewModel.RoutineAppOutputLease, string?, string?, Task> showCompletionOverlayAsync,
            Action<AppViewModel.RoutineAppOutputLease, uint, bool> markLeaseProcessApplied,
            Action<AppViewModel.RoutineAppOutputLease> markLeaseOverlayShown,
            Action<AppViewModel.RoutineAppOutputLease> markLeaseCompleted,
            Logger logger,
            CancellationToken cancellationToken)
        {
            foreach (AppViewModel.RoutineAppOutputLease lease in activeLeases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<uint> candidateProcessIds = collectCandidateProcessIds(
                    lease.RootProcessId,
                    lease.TriggerAppPath,
                    processSnapshots,
                    sessionSnapshots);

                foreach (uint processId in candidateProcessIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool outputAppliedForProcess = false;
                    bool inputAppliedForProcess = false;

                    if (!string.IsNullOrWhiteSpace(lease.OutputDeviceId) && !lease.AppliedOutputProcessIds.Contains(processId))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        outputAppliedForProcess = await tryApplyOutputAsync(lease, processId);
                        if (outputAppliedForProcess)
                        {
                            markLeaseProcessApplied(lease, processId, true);
                            lease.AppliedOutputProcessIds.Add(processId);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(lease.InputDeviceId) && !lease.AppliedInputProcessIds.Contains(processId))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        inputAppliedForProcess = await tryApplyInputAsync(lease, processId);
                        if (inputAppliedForProcess)
                        {
                            markLeaseProcessApplied(lease, processId, false);
                            lease.AppliedInputProcessIds.Add(processId);
                        }
                    }

                    if (outputAppliedForProcess || inputAppliedForProcess)
                    {
                        logger.Info(
                            "AppViewModel",
                            () => $"routine-application-lease-process-applied | {AppViewModel.BuildRoutineAppOutputLeaseLogContext(lease)} processId={AppViewModel.FormatRoutineLogProcessId((int)processId)} outputApplied={outputAppliedForProcess} inputApplied={inputAppliedForProcess}");
                    }

                    if (!lease.CompletionOverlayShown && HasLeaseCompleted(lease) && (outputAppliedForProcess || inputAppliedForProcess))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await showCompletionOverlayAsync(
                            lease,
                            outputAppliedForProcess ? lease.OutputDeviceName : null,
                            inputAppliedForProcess ? lease.InputDeviceName : null);
                        markLeaseOverlayShown(lease);
                        markLeaseCompleted(lease);
                        lease.CompletionOverlayShown = true;
                        logger.Info(
                            "AppViewModel",
                            () => $"routine-application-lease-completed | {AppViewModel.BuildRoutineAppOutputLeaseLogContext(lease)}");
                    }
                }
            }
        }

        internal static bool HasPendingLeaseApplications(AppViewModel.RoutineAppOutputLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);

            return !HasLeaseCompleted(lease);
        }

        private static bool HasLeaseCompleted(AppViewModel.RoutineAppOutputLease lease)
        {
            bool outputCompleted = string.IsNullOrWhiteSpace(lease.OutputDeviceId) || lease.AppliedOutputProcessIds.Count > 0;
            bool inputCompleted = string.IsNullOrWhiteSpace(lease.InputDeviceId) || lease.AppliedInputProcessIds.Count > 0;
            return outputCompleted && inputCompleted;
        }

        internal static bool DoesLiveLeaseMatchExpectedSnapshot(
            AppViewModel.RoutineAppOutputLease currentLease,
            AppViewModel.RoutineAppOutputLease expectedLease)
        {
            ArgumentNullException.ThrowIfNull(currentLease);
            ArgumentNullException.ThrowIfNull(expectedLease);

            return string.Equals(currentLease.LeaseKey, expectedLease.LeaseKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(currentLease.TriggerAppPath, expectedLease.TriggerAppPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(currentLease.OutputDeviceId, expectedLease.OutputDeviceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(currentLease.InputDeviceId, expectedLease.InputDeviceId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
