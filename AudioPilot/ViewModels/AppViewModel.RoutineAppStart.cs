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
        private readonly Lock _routineAppStartMonitorLock = new();
        private List<AudioRoutine> _appStartTriggeredRoutines = [];
        private List<AudioRoutine> _applicationRoutingRoutines = [];
        private CancellationTokenSource? _routineAppOutputLeaseRefreshDebounceCts;
        private int _pendingRoutineAppOutputLeaseSignals;
        private bool _routineAppStartMonitorRunning;
        private bool _routineAppStartMonitoringEnabled;
        private bool _routineAppOutputLeaseOutputSessionMonitoringAcquired;
        private bool _routineAppOutputLeaseInputSessionMonitoringAcquired;
        private string _routineAppStartMonitorStatus = "inactive";

        private void InitializeRoutineAppStartInfrastructure()
        {
            _routineLifetime.AttachProcessMonitor(_routineAppProcessMonitor, OnRoutineAppProcessStarted, OnRoutineAppProcessStopped);
        }

        internal void EnableRoutineAppStartMonitoring()
        {
            _routineAppStartMonitoringEnabled = true;
            RefreshRoutineRuntimeTriggers();
        }

        private void RefreshRoutineRuntimeTriggers()
        {
            IReadOnlyList<AudioRoutine> configured = GetPersistedRoutineSnapshot();
            _routineDeviceAvailability.Observe(configured, GetRoutineAvailabilityDevices, evaluateTransitions: false);
            List<AudioRoutine> routines = [.. configured.Where(static routine => routine.Enabled)
                .SelectMany(static routine => routine.ExpandAutomaticTriggers(static trigger => trigger.Kind is RoutineTriggerKind.Application or RoutineTriggerKind.SteamBigPicture))];
            List<AudioRoutine> watchedRoutines =
            [
                .. routines.Where(static routine =>
                    routine.Enabled &&
                    routine.HasApplicationTrigger &&
                    RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath))
            ];
            List<AudioRoutine> steamBigPictureRoutines =
            [
                .. routines.Where(static routine =>
                    routine.Enabled &&
                    routine.TriggerKind == RoutineTriggerKind.SteamBigPicture &&
                    routine.HasExecutionTarget)
            ];
            List<AudioRoutine> routingRoutines = [.. configured.Where(static routine => routine.Enabled && routine.SwitchOutputPerApp)];
            IReadOnlyList<RoutineLifetimeService.Deactivation> invalidSessions;

            lock (_routineAppStartMonitorLock)
            {
                _appStartTriggeredRoutines = watchedRoutines;
                _steamBigPictureTriggeredRoutines = steamBigPictureRoutines;
                _applicationRoutingRoutines = routingRoutines;
                _routineLifetime.SynchronizeLeases(routingRoutines);
                invalidSessions = _routineLifetime.Synchronize(watchedRoutines, steamBigPictureRoutines);
            }

            UpdateRoutineAppStartMonitorState();
            UpdateSteamBigPictureMonitorState();

            if (invalidSessions.Count == 0)
            {
                return;
            }

            RunBackgroundWork(async cancellationToken =>
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
            bool isRunning;
            int watchedRoutineCount;
            int activeLeaseCount;
            bool hasActiveAppStartStatefulSessions;
            lock (_routineAppStartMonitorLock)
            {
                watchedRoutineCount = _appStartTriggeredRoutines.Count;
                activeLeaseCount = _routineLifetime.LeaseCount;
                hasActiveAppStartStatefulSessions = _routineLifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.Application);
                isRunning = _routineAppStartMonitorRunning;
            }

            UpdateRoutineAppOutputLeaseSessionMonitoringState();

            RoutineAppStartMonitorDecision decision = AppRoutineAppStartCoordinator.ResolveMonitorDecision(
                _routineAppStartMonitoringEnabled,
                _isCleaningUp,
                watchedRoutineCount,
                activeLeaseCount,
                hasActiveAppStartStatefulSessions,
                isRunning);

            if (decision.Action == RoutineAppStartMonitorAction.Stop)
            {
                _routineAppProcessMonitor.Stop();
                lock (_routineAppStartMonitorLock)
                {
                    _routineAppStartMonitorRunning = false;
                    _routineAppStartMonitorStatus = "inactive";
                }
                _logger.Info("AppViewModel", "routine-application-trigger-monitor-inactive | reason=no-watched-routines");
                return;
            }

            if (decision.Action == RoutineAppStartMonitorAction.Start)
            {
                ProcessLifecycleMonitorStartResult startResult = _routineAppProcessMonitor.Start();
                lock (_routineAppStartMonitorLock)
                {
                    _routineAppStartMonitorRunning = startResult.Success;
                    _routineAppStartMonitorStatus = startResult.Status;
                }

                if (startResult.Success)
                {
                    _logger.Info("AppViewModel", () => $"routine-application-trigger-monitor-active | watcher={_routineAppProcessMonitor.GetType().Name} triggerCount={_appStartTriggeredRoutines.Count} leaseCount={_routineLifetime.LeaseCount}");
                }
                else
                {
                    string failureReason = string.IsNullOrWhiteSpace(startResult.FailureReason) ? "unknown" : startResult.FailureReason;
                    _logger.Warning("AppViewModel", () => $"routine-application-trigger-monitor-inactive | reason={failureReason} watchedRoutineCount={_appStartTriggeredRoutines.Count} leaseCount={_routineLifetime.LeaseCount}");
                    _logger.Warning("AppViewModel", () => $"routine-application-trigger-monitor-start-failed | reason={failureReason} appStartRoutinesAvailable=false");
                }
            }
        }

        private void UpdateRoutineAppOutputLeaseSessionMonitoringState()
        {
            if (_audio == null)
            {
                return;
            }

            bool acquireOutput;
            bool releaseOutput;
            bool acquireInput;
            bool releaseInput;

            lock (_routineAppStartMonitorLock)
            {
                bool shouldMonitorOutput = !_isCleaningUp && _routineLifetime.PendingLeaseCounts.Output > 0;
                bool shouldMonitorInput = !_isCleaningUp && _routineLifetime.PendingLeaseCounts.Input > 0;

                acquireOutput = shouldMonitorOutput && !_routineAppOutputLeaseOutputSessionMonitoringAcquired;
                releaseOutput = !shouldMonitorOutput && _routineAppOutputLeaseOutputSessionMonitoringAcquired;
                acquireInput = shouldMonitorInput && !_routineAppOutputLeaseInputSessionMonitoringAcquired;
                releaseInput = !shouldMonitorInput && _routineAppOutputLeaseInputSessionMonitoringAcquired;

                _routineAppOutputLeaseOutputSessionMonitoringAcquired = shouldMonitorOutput;
                _routineAppOutputLeaseInputSessionMonitoringAcquired = shouldMonitorInput;
            }

            if (acquireOutput)
            {
                _audio.AcquireSessionMonitoring(AudioMixerMode.Output);
            }
            else if (releaseOutput)
            {
                _audio.ReleaseSessionMonitoring(AudioMixerMode.Output);
            }

            if (acquireInput)
            {
                _audio.AcquireSessionMonitoring(AudioMixerMode.Input);
            }
            else if (releaseInput)
            {
                _audio.ReleaseSessionMonitoring(AudioMixerMode.Input);
            }
        }

        private void OnRoutineAppProcessStarted(int processId)
        {
            if (_isCleaningUp || processId <= 0)
            {
                return;
            }

            RequestSteamBigPictureFallbackRevalidation();

            List<AudioRoutine> watchedRoutines;
            int activeLeaseCount;
            lock (_routineAppStartMonitorLock)
            {
                watchedRoutines = [.. _appStartTriggeredRoutines.Where(static r => r.ApplicationTriggerMode == ApplicationTriggerMode.AppLaunch)];
                activeLeaseCount = _routineLifetime.LeaseCount;
            }

            if (!_routineAppStartMonitoringEnabled || watchedRoutines.Count == 0)
            {
                return;
            }

            RunBackgroundWork(async cancellationToken =>
            {
                RoutineProcessSnapshotCaptureOptions startedSnapshotCaptureOptions = GetCaptureOptionsForTriggerTargets(
                    watchedRoutines.Select(static routine => routine.TriggerAppPath).Concat(_applicationRoutingRoutines.Select(static routine => routine.TargetAppPath)));
                RoutineAppStartProcessWorkload? workload = await AppRoutineAppStartCoordinator.PrepareStartedProcessWorkloadAsync(
                    processId,
                    watchedRoutines,
                    activeLeaseCount,
                    startedProcessId => TryCaptureProcessSnapshot(startedProcessId, startedSnapshotCaptureOptions),
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
                    lock (_routineAppStartMonitorLock)
                    {
                        activeLeases = _routineLifetime.GetLeases();
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
                    if (executionPlan.Action == RoutineAppStartMatchExecutionAction.SkipExistingActiveLease &&
                        !_routineLifetime.Sessions.Any(session => string.Equals(session.RoutineId, executionPlan.Match.Routine.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        skippedExistingLeaseCount++;
                        LogRoutineAppStartMatchSkipped(executionPlan.Match, "existing-active-lease", opId);
                        continue;
                    }

                    RoutineAppStartMatch match = executionPlan.Match;
                    if (!_routineLifetime.TryClaim(match.Routine, workload.Value.Snapshot, out RoutineLifetimeService.ActivationClaim? claim, out string claimFailureReason))
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
                        _routineLifetime.ReleaseClaim(claim!);
                    }
                }

                _logger.Info(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationTriggerBatch} | phase=completed opId={NormalizeRoutineLogValue(opId)} processId={FormatRoutineLogProcessId(processId)} matchCount={workload.Value.Matches.Count} executedCount={executedCount} skippedExistingLeaseCount={skippedExistingLeaseCount} skippedClaimCount={skippedClaimCount} processSnapshotCount={processSnapshots.Count}");
            }, nameof(OnRoutineAppProcessStarted));
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
            lock (_routineAppStartMonitorLock)
            {
                activeLeaseCount = _routineLifetime.LeaseCount;
                activeAppStartStatefulSessionCount = _routineLifetime.Sessions.Count(static session => session.TriggerKind == RoutineTriggerKind.Application);
            }

            if (!ShouldCaptureProcessSnapshotsForStoppedProcess(activeLeaseCount, activeAppStartStatefulSessionCount))
            {
                UpdateRoutineAppStartMonitorState();
                return;
            }

            RunBackgroundWork(async cancellationToken =>
            {
                List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                    () => CaptureProcessSnapshots(RoutineProcessSnapshotCaptureOptions.Full),
                    cancellationToken);
                IReadOnlyList<RoutineAppOutputLease> removedLeases;
                lock (_routineAppStartMonitorLock)
                {
                    RoutineAppOutputLeaseRefreshPreparation preparation = _routineLifetime.PrepareLeaseRefresh(_applicationRoutingRoutines, processSnapshots);
                    removedLeases = preparation.RemovedLeases;

                }

                CompletePendingAppAudioWaitForRemovedLeases(removedLeases);
                await DeactivateEndedAppStartSessionsAsync(processSnapshots, cancellationToken);

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
            RoutineLifetimeService.LeaseRegistration? registration = _routineLifetime.RegisterLease(
                routine, rootProcessId, outputApplied, inputApplied, completionOverlayShown, processIdentity, cancellationToken);
            if (registration is not { } result) return;
            string eventName = result.Created ? "routine-application-lease-created" : "routine-application-lease-updated";
            _logger.Info("AppViewModel", () => $"{eventName} | {BuildRoutineAppOutputLeaseLogContext(result.Lease)} outputTargetChanged={result.OutputChanged} inputTargetChanged={result.InputChanged} initialOutputApplied={outputApplied} initialInputApplied={inputApplied}");
            UpdateRoutineAppStartMonitorState();
        }

        private void QueueRoutineAppOutputLeaseRefresh()
        {
            if (_isCleaningUp)
            {
                return;
            }

            lock (_routineAppStartMonitorLock)
            {
                if (_routineLifetime.LeaseCount == 0)
                {
                    return;
                }
            }

            int queuedSignals = Interlocked.Increment(ref _pendingRoutineAppOutputLeaseSignals);
            CancellationTokenSource nextDebounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _routineAppOutputLeaseRefreshDebounceCts);
            RunBackgroundWork(async shutdownToken =>
            {
                await AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
                    nextDebounceCts,
                    ownedDebounce => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _routineAppOutputLeaseRefreshDebounceCts, ownedDebounce),
                    RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                    async linkedToken =>
                    {
                        int coalescedSignals = Interlocked.Exchange(ref _pendingRoutineAppOutputLeaseSignals, 0);
                        if (coalescedSignals <= 0)
                        {
                            coalescedSignals = queuedSignals;
                        }

                        string opId = $"app-start-lease-refresh:{Guid.NewGuid():N}";
                        await ApplyRoutineAppOutputLeasesAsync(opId, coalescedSignals, linkedToken);
                    },
                    shutdownToken);
            }, nameof(QueueRoutineAppOutputLeaseRefresh));
        }

        private async Task ApplyRoutineAppOutputLeasesAsync(string operationId, int coalescedSignals, CancellationToken cancellationToken)
        {
            _logger.Info(
                "AppViewModel",
                () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationLeaseRefresh} | phase=start opId={NormalizeRoutineLogValue(operationId)} coalescedSignals={coalescedSignals}");
            RoutineProcessSnapshotCaptureOptions captureOptions = GetCaptureOptionsForTriggerTargets(
                _applicationRoutingRoutines.Select(static routine => routine.TargetAppPath));
            List<RoutineAppStartProcessSnapshot> processSnapshots = await Task.Run(
                () => CaptureProcessSnapshots(captureOptions),
                cancellationToken);
            RoutineAppStartSnapshotSet processSnapshotSet = CreateRoutineAppStartSnapshotSet(processSnapshots);

            IReadOnlyList<RoutineAppOutputLease> activeLeases;
            IReadOnlyList<RoutineAppOutputLease> removedLeases;
            int previousLeaseCount;
            lock (_routineAppStartMonitorLock)
            {
                RoutineAppOutputLeaseRefreshPreparation preparation = _routineLifetime.PrepareLeaseRefresh(_applicationRoutingRoutines, processSnapshots);
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
                _routineLifetime.MarkLeaseProcessApplied,
                _routineLifetime.MarkLeaseOverlayShown,
                MarkRoutineAppOutputLeaseCompleted,
                _logger,
                cancellationToken);
        }

        private Task<bool> TryApplyRoutineAppOutputLeaseOutputAsync(RoutineAppOutputLease lease, uint processId)
        {
            return _routineLifetime.ApplyRoutingAsync(() => _routineLifetime.IsLeaseCurrent(lease), async () =>
            {
                ProcessAudioDeviceSwitchResult result = await _audio.SwitchApplicationOutputDeviceDetailedAsync(
                    processId, lease.OutputDeviceId, lease.OutputDeviceName, CreateRoutineAppStartOperationId("routine-app-output-lease"));
                return result.Result == ProcessAudioRoutingResult.Applied;
            });
        }

        private Task<bool> TryApplyRoutineAppOutputLeaseInputAsync(RoutineAppOutputLease lease, uint processId)
        {
            return _routineLifetime.ApplyRoutingAsync(() => _routineLifetime.IsLeaseCurrent(lease), async () =>
            {
                ProcessAudioDeviceSwitchResult result = await _audio.SwitchApplicationInputDeviceDetailedAsync(
                    processId, lease.InputDeviceId, lease.InputDeviceName, CreateRoutineAppStartOperationId("routine-app-input-lease"));
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
                if (!_routineLifetime.IsLeaseCurrent(lease)) return;
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
            if (!_routineLifetime.IsLeaseCurrent(expectedLease) || string.IsNullOrWhiteSpace(expectedLease.RoutineId))
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
            lock (_routineAppStartMonitorLock)
            {
                bool hasRemainingLease = _routineLifetime.GetLeases().Any(activeLease =>
                    !HasRoutineAppOutputLeaseCompleted(activeLease) &&
                    string.Equals(NormalizeRoutineId(activeLease.RoutineId), normalizedRoutineId, StringComparison.OrdinalIgnoreCase));
                if (hasRemainingLease)
                {
                    return;
                }

                bool hasRemainingSession = _routineLifetime.Sessions.Any(activeSession =>
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
            return _routineProcessSnapshotProvider.TryCapture(processId, options);
        }

        private List<RoutineAppStartProcessSnapshot> CaptureProcessSnapshots(
            RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
        {
            return _routineProcessSnapshotProvider.CaptureAll(options);
        }

        private static RoutineProcessSnapshotCaptureOptions GetCaptureOptionsForTriggerTarget(string? triggerTarget)
        {
            return RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerTarget)
                ? RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId
                : RoutineProcessSnapshotCaptureOptions.None;
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
