using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using static AudioPilot.Services.Routines.RoutineApplicationRouting;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal void EnableRoutineAppStartMonitoring()
        {
            _routineRuntime.EnableMonitoring();
            RefreshRoutineRuntimeTriggers();
        }

        private void RefreshRoutineRuntimeTriggers()
        {
            _shell.RetainRoutineFailures(GetPersistedRoutineSnapshot().Where(static routine => routine.Enabled && routine.NotifyOnAutomaticFailure).Select(static routine => routine.Id));
            IReadOnlyList<RoutineLifetimeService.Deactivation> invalidSessions = _routineRuntime.ApplyConfiguration();

            UpdateRoutineAppStartMonitorState();
            UpdateSteamBigPictureMonitorState();
            QueueRoutineExitBaselineRefresh();

            if (invalidSessions.Count == 0)
            {
                return;
            }

            _routineRuntime.QueueWork(async cancellationToken =>
            {
                foreach (RoutineLifetimeService.Deactivation deactivation in invalidSessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DeactivateRoutineStatefulSessionAsync(deactivation);
                }
            }, nameof(RefreshRoutineRuntimeTriggers));
        }

        private void UpdateRoutineAppStartMonitorState()
        {
            _routineRuntime.UpdateRoutingSessionMonitoring();
            _routineRuntime.UpdateProcessMonitoring();
        }

        private void OnRoutineAppProcessStarted(int processId)
        {
            if (_isCleaningUp || processId <= 0 || _routineRuntime.AutomaticPaused)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            List<AudioRoutine> watchedRoutines;
            int activeLeaseCount;
            lock (_routineRuntime.Sync)
            {
                watchedRoutines = [.. _routineRuntime.ApplicationRoutines.Where(static r => r.ApplicationTriggerMode == ApplicationTriggerMode.AppLaunch)];
                activeLeaseCount = _routineRuntime.Lifetime.LeaseCount;
            }

            if (!_routineRuntime.MonitoringEnabled || (watchedRoutines.Count == 0 && !_routineRuntime.ApplicationExits.HasTriggers))
            {
                return;
            }

            long automationGeneration = _routineRuntime.AutomationGeneration;
            var exitStart = _routineRuntime.ApplicationExits.BeginStart(processId);
            bool queued = _routineRuntime.QueueWork(async cancellationToken =>
            {
                RoutineProcessSnapshotCaptureOptions startedSnapshotCaptureOptions = GetCaptureOptionsForTriggerTargets(
                    watchedRoutines.Select(static routine => routine.TriggerAppPath).Concat(_routineRuntime.RoutingRoutines.Select(static routine => routine.TargetAppPath))
                        .Concat(_routineRuntime.ApplicationRoutines.Where(static routine => routine.ApplicationTriggerMode == ApplicationTriggerMode.AppExit).Select(static routine => routine.TriggerAppPath)));
                RoutineAppStartProcessSnapshot? exitSnapshot = null;
                try
                {
                    if (_routineRuntime.AutomaticPaused || automationGeneration != _routineRuntime.AutomationGeneration) return;
                    if (exitStart != null) exitSnapshot = await Task.Run(() => TryCaptureProcessSnapshot(processId, startedSnapshotCaptureOptions), cancellationToken);
                }
                finally { _routineRuntime.ApplicationExits.CompleteStart(exitStart, exitSnapshot); }
                if (watchedRoutines.Count == 0) return;
                RoutineAppStartProcessWorkload? workload = await AppRoutineAppStartCoordinator.PrepareStartedProcessWorkloadAsync(
                    processId,
                    watchedRoutines,
                    activeLeaseCount,
                    startedProcessId => exitStart != null ? exitSnapshot : TryCaptureProcessSnapshot(startedProcessId, startedSnapshotCaptureOptions),
                    cancellationToken);
                if (!workload.HasValue)
                {
                    return;
                }

                string opId = CreateRoutineAppStartOperationId("application-trigger-routine");
                _logger.Info(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationTriggerBatch} | phase=start opId={NormalizeRoutineLogValue(opId)} processId={FormatRoutineLogProcessId(processId)} matchCount={workload.Value.Matches.Count} requiresProcessSnapshotCapture={workload.Value.RequiresProcessSnapshotCapture} activeLeaseCount={activeLeaseCount}");

                List<RoutineAppStartProcessSnapshot> processSnapshots = [];
                RoutineAppStartSnapshotSet? processSnapshotSet = null;
                IReadOnlyList<RoutineAppOutputLease> activeLeases = [];
                if (workload.Value.RequiresProcessSnapshotCapture)
                {
                    processSnapshots = await Task.Run(
                        () => CaptureProcessSnapshots(RoutineProcessSnapshotCaptureOptions.Full),
                        cancellationToken);
                    processSnapshotSet = CreateRoutineAppStartSnapshotSet(processSnapshots);
                    lock (_routineRuntime.Sync)
                    {
                        activeLeases = _routineRuntime.Lifetime.GetLeases();
                    }
                }

                IReadOnlyList<RoutineAppStartMatchExecutionPlan> executionPlans = processSnapshotSet.HasValue
                    ? AppRoutineAppStartCoordinator.PlanStartedMatchExecutions(
                        workload.Value.Matches,
                        workload.Value.Snapshot,
                        activeLeases,
                        processSnapshotSet.Value)
                    : AppRoutineAppStartCoordinator.PlanStartedMatchExecutions(
                        workload.Value.Matches,
                        workload.Value.Snapshot,
                        activeLeases,
                        processSnapshots);

                int executedCount = 0;
                int skippedExistingLeaseCount = 0;
                int skippedClaimCount = 0;

                foreach (RoutineAppStartMatchExecutionPlan executionPlan in executionPlans)
                {
                    if (_routineRuntime.AutomaticPaused || automationGeneration != _routineRuntime.AutomationGeneration) return;
                    if (executionPlan.Action == RoutineAppStartMatchExecutionAction.SkipExistingActiveLease &&
                        !_routineRuntime.Lifetime.Sessions.Any(session => string.Equals(session.RoutineId, executionPlan.Match.Routine.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        skippedExistingLeaseCount++;
                        LogRoutineAppStartMatchSkipped(executionPlan.Match, "existing-active-lease", opId);
                        continue;
                    }

                    RoutineAppStartMatch match = executionPlan.Match;
                    if (!_routineRuntime.Lifetime.TryClaim(match.Routine, workload.Value.Snapshot, out RoutineLifetimeService.ActivationClaim? claim, out string claimFailureReason))
                    {
                        skippedClaimCount++;
                        LogRoutineAppStartMatchSkipped(match, claimFailureReason, opId);
                        continue;
                    }

                    try
                    {
                        executedCount++;
                        await ExecuteRoutineFromAppStartAsync(match, cancellationToken, opId);
                    }
                    finally
                    {
                        _routineRuntime.Lifetime.ReleaseClaim(claim!);
                    }
                }

                _logger.Info(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationTriggerBatch} | phase=completed opId={NormalizeRoutineLogValue(opId)} processId={FormatRoutineLogProcessId(processId)} matchCount={workload.Value.Matches.Count} executedCount={executedCount} skippedExistingLeaseCount={skippedExistingLeaseCount} skippedClaimCount={skippedClaimCount} processSnapshotCount={processSnapshots.Count}");
            }, nameof(OnRoutineAppProcessStarted));
            if (!queued) _routineRuntime.ApplicationExits.CompleteStart(exitStart, null);
        }

        private void LogRoutineAppStartMatchSkipped(RoutineAppStartMatch match, string reason, string? correlatedOperationId = null)
        {
            _logger.Info(
                "AppViewModel",
                () => $"routine-application-trigger-match-skipped | processId={FormatRoutineLogProcessId(match.ProcessId)} reason={reason} {BuildRoutineExecutionLogContext(match.Routine, "application-launch", showOverlay: true, applicationProcessId: match.ProcessId)}{BuildRoutineExecutionCorrelationLogContext(correlatedOperationId)}");
        }

        private void OnRoutineAppProcessStopped(int processId)
        {
            if (_isCleaningUp || processId <= 0)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            int activeLeaseCount;
            int activeAppStartStatefulSessionCount;
            lock (_routineRuntime.Sync)
            {
                activeLeaseCount = _routineRuntime.Lifetime.LeaseCount;
                activeAppStartStatefulSessionCount = _routineRuntime.Lifetime.Sessions.Count(static session => session.TriggerKind == RoutineTriggerKind.Application);
            }

            var exitContext = (Configuration: _routineRuntime.ApplicationExits.Generation, Automation: _routineRuntime.AutomationGeneration);
            bool observeExit = !_routineRuntime.AutomaticPaused && _routineRuntime.ApplicationExits.ShouldObserveStop(processId);
            if (!ShouldCaptureProcessSnapshotsForStoppedProcess(activeLeaseCount, activeAppStartStatefulSessionCount) && !observeExit)
            {
                UpdateRoutineAppStartMonitorState();
                return;
            }

            Task exitStartCompletion = _routineRuntime.ApplicationExits.PendingStart(processId);
            Task exitBaseline = Volatile.Read(ref _routineExitBaseline);
            _routineRuntime.QueueWork(async cancellationToken =>
            {
                if (observeExit)
                {
                    await Task.WhenAll(exitStartCompletion, exitBaseline).WaitAsync(cancellationToken);
                }
                var options = activeLeaseCount > 0 || activeAppStartStatefulSessionCount > 0 ? RoutineProcessSnapshotCaptureOptions.Full
                    : GetCaptureOptionsForTriggerTargets(_routineRuntime.ApplicationRoutines.Where(static routine => routine.ApplicationTriggerMode == ApplicationTriggerMode.AppExit).Select(static routine => routine.TriggerAppPath));
                long exitObservationVersion = _routineRuntime.ApplicationExits.ObservationVersion;
                List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                    () => CaptureProcessSnapshots(options),
                    cancellationToken);
                IReadOnlyList<RoutineAppOutputLease> removedLeases;
                lock (_routineRuntime.Sync)
                {
                    RoutineAppOutputLeaseRefreshPreparation preparation = _routineRuntime.Lifetime.PrepareLeaseRefresh(_routineRuntime.RoutingRoutines, processSnapshots);
                    removedLeases = preparation.RemovedLeases;

                }

                CompletePendingAppAudioWaitForRemovedLeases(removedLeases);
                await DeactivateEndedAppStartSessionsAsync(processSnapshots, cancellationToken);
                if (observeExit) await ExecuteRoutineExitTriggersAsync(processId, processSnapshots, exitContext, exitObservationVersion, cancellationToken);

                UpdateRoutineAppStartMonitorState();
            }, nameof(OnRoutineAppProcessStopped));
        }

        private async Task ExecuteRoutineFromAppStartAsync(RoutineAppStartMatch match, CancellationToken cancellationToken, string? correlatedOperationId = null)
        {
            AudioRoutine routine = match.Routine;
            if (!routine.Enabled)
            {
                return;
            }

            if (!routine.HasExecutionTarget)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteRoutineForResolvedProcessAsync(
                routine,
                match.ProcessId,
                showOverlay: true,
                executionSource: "application-launch",
                correlatedOperationId: correlatedOperationId,
                cancellationToken: cancellationToken);
        }

        private void RegisterRoutineAppOutputLease(AudioRoutine routine, int rootProcessId, bool outputApplied, bool inputApplied, bool completionOverlayShown, RoutineAppStartProcessSnapshot? processIdentity = null, CancellationToken cancellationToken = default)
        {
            RoutineLifetimeService.LeaseRegistration? registration = _routineRuntime.Lifetime.RegisterLease(
                routine, rootProcessId, outputApplied, inputApplied, completionOverlayShown, processIdentity, cancellationToken);
            if (registration is not { } result) return;
            string eventName = result.Created ? "routine-application-lease-created" : "routine-application-lease-updated";
            _logger.Info("AppViewModel", () => $"{eventName} | {BuildRoutineAppOutputLeaseLogContext(result.Lease)} outputTargetChanged={result.OutputChanged} inputTargetChanged={result.InputChanged} initialOutputApplied={outputApplied} initialInputApplied={inputApplied}");
            UpdateRoutineAppStartMonitorState();
        }

        private void QueueRoutineAppOutputLeaseRefresh()
        {
            _routineRuntime.QueueRoutingRefresh(ApplyRoutineAppOutputLeasesAsync);
        }

        private async Task ApplyRoutineAppOutputLeasesAsync(string operationId, int coalescedSignals, CancellationToken cancellationToken)
        {
            _logger.Info(
                "AppViewModel",
                () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationLeaseRefresh} | phase=start opId={NormalizeRoutineLogValue(operationId)} coalescedSignals={coalescedSignals}");
            RoutineProcessSnapshotCaptureOptions captureOptions = GetCaptureOptionsForTriggerTargets(
                _routineRuntime.RoutingRoutines.Select(static routine => routine.TargetAppPath));
            List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                () => CaptureProcessSnapshots(captureOptions),
                cancellationToken);
            RoutineAppStartSnapshotSet processSnapshotSet = CreateRoutineAppStartSnapshotSet(processSnapshots);

            IReadOnlyList<RoutineAppOutputLease> activeLeases;
            IReadOnlyList<RoutineAppOutputLease> removedLeases;
            int previousLeaseCount;
            lock (_routineRuntime.Sync)
            {
                RoutineAppOutputLeaseRefreshPreparation preparation = _routineRuntime.Lifetime.PrepareLeaseRefresh(_routineRuntime.RoutingRoutines, processSnapshots);
                previousLeaseCount = preparation.PreviousLeaseCount;

                activeLeases = preparation.ActiveLeases;
                removedLeases = preparation.RemovedLeases;
            }

            _logger.Info(
                "AppViewModel",
                () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationLeaseRefresh} | phase=completed opId={NormalizeRoutineLogValue(operationId)} coalescedSignals={coalescedSignals} previousLeaseCount={previousLeaseCount} activeLeaseCount={activeLeases.Count} processSnapshotCount={processSnapshots.Count}");

            CompletePendingAppAudioWaitForRemovedLeases(removedLeases);
            UpdateRoutineAppStartMonitorState();

            if (activeLeases.Count == 0)
            {
                return;
            }

            List<RoutineAppOutputLease> pendingLeases = [];
            for (int index = 0; index < activeLeases.Count; index++)
            {
                RoutineAppOutputLease lease = activeLeases[index];
                if (AppRoutineAppStartCoordinator.HasPendingLeaseApplications(lease))
                {
                    pendingLeases.Add(lease);
                }
            }

            if (pendingLeases.Count == 0)
            {
                _logger.Info(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationLeaseRefresh} | phase=skipped-no-pending-work opId={NormalizeRoutineLogValue(operationId)} coalescedSignals={coalescedSignals} activeLeaseCount={activeLeases.Count}");
                return;
            }

            IReadOnlyList<AudioSessionSnapshot> sessionSnapshots = await _audio.GetAllAudioSessionSnapshotsAsync(cancellationToken: cancellationToken);

            await AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
                pendingLeases,
                processSnapshots,
                sessionSnapshots,
                (rootProcessId, triggerAppPath, _, currentSessionSnapshots) => CollectRoutineAppOutputCandidateProcessIds(
                    rootProcessId,
                    triggerAppPath,
                    processSnapshotSet,
                    currentSessionSnapshots),
                TryApplyRoutineAppOutputLeaseOutputAsync,
                TryApplyRoutineAppOutputLeaseInputAsync,
                ShowRoutineAppOutputLeaseOverlayAsync,
                _routineRuntime.Lifetime.MarkLeaseProcessApplied,
                _routineRuntime.Lifetime.MarkLeaseOverlayShown,
                MarkRoutineAppOutputLeaseCompleted,
                _logger,
                cancellationToken);
        }

        private Task<bool> TryApplyRoutineAppOutputLeaseOutputAsync(RoutineAppOutputLease lease, uint processId)
        {
            return _routineRuntime.Lifetime.ApplyRoutingAsync(() => _routineRuntime.Lifetime.IsLeaseCurrent(lease), async () =>
            {
                CycleDevice? target = lease.ResolveTarget(true, GetRoutineAvailabilityDevices(true));
                if (target == null) return false;
                ProcessAudioDeviceSwitchResult result = await _audio.SwitchApplicationOutputDeviceDetailedAsync(
                    processId, target.Id, target.Name, CreateRoutineAppStartOperationId("routine-app-output-lease"));
                return result.Result == ProcessAudioRoutingResult.Applied;
            });
        }

        private Task<bool> TryApplyRoutineAppOutputLeaseInputAsync(RoutineAppOutputLease lease, uint processId)
        {
            return _routineRuntime.Lifetime.ApplyRoutingAsync(() => _routineRuntime.Lifetime.IsLeaseCurrent(lease), async () =>
            {
                CycleDevice? target = lease.ResolveTarget(false, GetRoutineAvailabilityDevices(false));
                if (target == null) return false;
                ProcessAudioDeviceSwitchResult result = await _audio.SwitchApplicationInputDeviceDetailedAsync(
                    processId, target.Id, target.Name, CreateRoutineAppStartOperationId("routine-app-input-lease"));
                return result.Result == ProcessAudioRoutingResult.Applied;
            });
        }

        internal static string BuildRoutineAppOutputLeaseLogContext(RoutineAppOutputLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);

            return $"leaseKey={FormatRoutineLogIdentifier(lease.LeaseKey)} routineId={FormatRoutineLogIdentifier(lease.RoutineId)} routineName={FormatRoutineLogLabel(lease.RoutineName)} rootProcessId={FormatRoutineLogProcessId(lease.RootProcessId)} hasOutputTarget={!string.IsNullOrWhiteSpace(lease.OutputDeviceId)} hasInputTarget={!string.IsNullOrWhiteSpace(lease.InputDeviceId)} completionOverlayShown={lease.CompletionOverlayShown} appliedOutputProcessCount={lease.AppliedOutputProcessIds.Count} appliedInputProcessCount={lease.AppliedInputProcessIds.Count}";
        }

        private async Task ShowRoutineAppOutputLeaseOverlayAsync(RoutineAppOutputLease lease, string? appliedOutputDeviceName, string? appliedInputDeviceName)
        {
            string? outputDeviceName = AppViewModelRoutineOverlayHelper.ResolveRoutineOverlayDeviceName(
                !string.IsNullOrWhiteSpace(lease.OutputDeviceId),
                appliedOutputDeviceName,
                lease.OutputDeviceName,
                fallbackLabel: "Output device");
            string? inputDeviceName = AppViewModelRoutineOverlayHelper.ResolveRoutineOverlayDeviceName(
                !string.IsNullOrWhiteSpace(lease.InputDeviceId),
                appliedInputDeviceName,
                lease.InputDeviceName,
                fallbackLabel: "Input device");

            if (string.IsNullOrWhiteSpace(outputDeviceName) && string.IsNullOrWhiteSpace(inputDeviceName))
            {
                return;
            }

            await InvokeOnDispatcherAsync(() =>
            {
                if (!_routineRuntime.Lifetime.IsLeaseCurrent(lease)) return;
                if (!AppViewModelRoutineOverlayHelper.TryBuildRoutineSuccessOverlayPlan(
                        lease.RoutineName,
                        outputDeviceName,
                        inputDeviceName,
                        out AppViewModelRoutineOverlayHelper.RoutineSuccessOverlayPlan plan))
                {
                    return;
                }

                if (plan.ShowCombined)
                {
                    _overlay.ShowRoutine(plan.Header, plan.OutputDeviceName!, plan.InputDeviceName!);
                    return;
                }

                _overlay.Show(plan.Kind, plan.Header, plan.DeviceName ?? string.Empty);
            });
        }

        private static string CreateRoutineAppStartOperationId(string prefix)
        {
            return $"{prefix}:{Guid.NewGuid():N}";
        }

        private void MarkRoutineAppOutputLeaseCompleted(RoutineAppOutputLease expectedLease)
        {
            if (!_routineRuntime.Lifetime.IsLeaseCurrent(expectedLease) || string.IsNullOrWhiteSpace(expectedLease.RoutineId))
            {
                return;
            }

            RoutineRuntimeState? runtimeState = GetRoutineRuntimeStateSnapshot(NormalizeRoutineId(expectedLease.RoutineId));
            if (runtimeState?.LastRunState != RoutineLastRunState.WaitingForApp)
            {
                return;
            }

            SetRoutineLastRunState(expectedLease.RoutineId, RoutineLastRunState.Succeeded);
        }

        private void CompletePendingAppAudioWaitForRemovedLeases(IReadOnlyList<RoutineAppOutputLease> removedLeases)
        {
            if (removedLeases.Count == 0)
            {
                return;
            }

            foreach (RoutineAppOutputLease lease in removedLeases)
            {
                CompletePendingAppAudioWaitForRemovedLease(lease);
            }
        }

        private void CompletePendingAppAudioWaitForRemovedLease(RoutineAppOutputLease lease)
        {
            if (string.IsNullOrWhiteSpace(lease.RoutineId) || HasRoutineAppOutputLeaseCompleted(lease))
            {
                return;
            }

            string normalizedRoutineId = NormalizeRoutineId(lease.RoutineId);
            lock (_routineRuntime.Sync)
            {
                bool hasRemainingLease = _routineRuntime.Lifetime.GetLeases().Any(activeLease =>
                    !HasRoutineAppOutputLeaseCompleted(activeLease) &&
                    string.Equals(NormalizeRoutineId(activeLease.RoutineId), normalizedRoutineId, StringComparison.OrdinalIgnoreCase));
                if (hasRemainingLease)
                {
                    return;
                }

                bool hasRemainingSession = _routineRuntime.Lifetime.Sessions.Any(activeSession =>
                    activeSession.TriggerKind == RoutineTriggerKind.Application &&
                    string.Equals(NormalizeRoutineId(activeSession.RoutineId), normalizedRoutineId, StringComparison.OrdinalIgnoreCase));
                if (hasRemainingSession)
                {
                    return;
                }
            }

            RoutineRuntimeState? runtimeState = GetRoutineRuntimeStateSnapshot(normalizedRoutineId);
            if (runtimeState?.LastRunState != RoutineLastRunState.WaitingForApp)
            {
                return;
            }

            _logger.Info(
                "AppViewModel",
                () => $"routine-application-lease-removed-before-audio | {BuildRoutineAppOutputLeaseLogContext(lease)}");
            SetRoutineLastRunState(
                lease.RoutineId,
                RoutineLastRunState.Skipped,
                "App closed before audio appeared");
        }

        private RoutineAppStartProcessSnapshot? TryCaptureProcessSnapshot(
            int processId,
            RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId)
        {
            return _routineRuntime.ProcessSnapshots.TryCapture(processId, options);
        }

        private List<RoutineAppStartProcessSnapshot> CaptureProcessSnapshots(
            RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
        {
            return _routineRuntime.ProcessSnapshots.CaptureAll(options);
        }

        private static RoutineProcessSnapshotCaptureOptions GetCaptureOptionsForTriggerTargets(IEnumerable<string> triggerTargets)
        {
            var options = RoutineProcessSnapshotCaptureOptions.None;
            foreach (string? triggerTarget in triggerTargets)
            {
                if (RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerTarget))
                {
                    options |= RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId;
                }
            }

            return options;
        }
    }
}
