using AudioPilot.Cli;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private async Task<RoutineExecutionResult> ExecuteRoutineForResolvedProcessAsync(AudioRoutine routine, int processId, bool showOverlay, string executionSource, string? correlatedOperationId = null, bool trackLifetime = true, CancellationToken cancellationToken = default)
        {
            if (IsRuntimeShutdownRequested || IsUpdateAdmissionBlocked) return BuildSkippedRoutineExecutionResult();
            AudioRoutine snapshot = routine.Clone();
            bool isManual = IsManualRoutineSource(executionSource);
            if (_routineRuntime.AutomaticPaused && !isManual)
                return BuildSkippedRoutineExecutionResult();
            if (!trackLifetime && snapshot.Triggers.Count > 0 && string.IsNullOrEmpty(snapshot.TriggerIdentity))
                snapshot.TriggerIdentity = snapshot.Triggers[0].Id;
            try
            {
                RoutineExecutionResult result = await _routineRuntime.Lifetime.RunActivationAsync(snapshot, processId,
                    token => executionSource == "application-exit" && !_routineRuntime.ApplicationExits.IsCurrent(routine)
                        ? Task.FromResult(BuildSkippedRoutineExecutionResult())
                        : ExecuteRoutineActivationAsync(snapshot, processId, showOverlay, executionSource, correlatedOperationId, trackLifetime, token), isManual: isManual, cancellationToken: cancellationToken);
                if (snapshot.PlaySoundAfterApplying && executionSource != "cli" && result.CanPlayCompletionSound)
                    QueueRoutineCue(snapshot.Id, isManual, () => !cancellationToken.IsCancellationRequested);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("AppViewModel", () => $"routine-activation-cancelled | routineId={LogPrivacy.Id(snapshot.Id)} source={executionSource}");
                return BuildSkippedRoutineExecutionResult();
            }
        }

        private async Task<RoutineExecutionResult> ExecuteRoutineActivationAsync(AudioRoutine routine, int processId, bool showOverlay, string executionSource, string? correlatedOperationId = null, bool trackLifetime = true, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (executionSource == "device-availability" && (!_routineRuntime.Availability.IsCurrentConfiguration(routine) || !_routineRuntime.Availability.IsPending(routine)))
                return BuildSkippedRoutineExecutionResult();
            if (executionSource == "network" && routine.TriggerDelaySeconds > 0 && !IsNetworkTriggerStillSatisfied(routine))
                return BuildSkippedRoutineExecutionResult();
            RoutineProcessSnapshot? processIdentity = processId > 0
                ? _routineRuntime.ProcessSnapshots.TryCapture(processId) : null;
            bool extendApplicationRouting = executionSource == "application-launch" && routine.RouteToApplication &&
                string.Equals(Helpers.RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TargetAppPath),
                    Helpers.RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TriggerAppPath), StringComparison.OrdinalIgnoreCase);
            if (extendApplicationRouting && processIdentity is { } started && _routineRuntime.Lifetime.LeaseCount > 0)
            {
                var processes = await Task.Run(() => CaptureProcessSnapshots(RoutineProcessSnapshotCaptureOptions.Full), cancellationToken);
                extendApplicationRouting = !RoutineApplicationRouting.ShouldSkipRoutineAppStartMatchForExistingLease(
                    new(routine, processId), started, _routineRuntime.Lifetime.GetLeases(), processes);
            }
            bool joined = false;
            if (!IsManualRoutineSource(executionSource))
            {
                RoutineExecutionService? requirements = routine.Conditions.HasRequirements ? CreateRoutineExecutionService() : null;
                bool Join() => _routineRuntime.Lifetime.TryJoinActiveRoutine(routine, processId, processIdentity, trackLifetime, extendApplicationRouting, cancellationToken,
                    isEligible: requirements == null ? null : () => requirements.CheckConditions(routine.Conditions, cancellationToken) == null);
                joined = requirements == null ? Join() : await Task.Run(Join, cancellationToken);
            }
            if (joined)
            {
                if (extendApplicationRouting)
                {
                    UpdateRoutineAppStartMonitorState();
                    QueueRoutineAppOutputLeaseRefresh();
                }
                _logger.Debug("AppViewModel", () => $"routine-trigger-joined | routineId={LogPrivacy.Id(routine.Id)} trigger={LogPrivacy.Id(routine.RuntimeTriggerKey)} source={executionSource} processId={FormatRoutineLogProcessId(processId)} extendRouting={extendApplicationRouting}");
                return BuildSkippedRoutineExecutionResult();
            }
            RoutineExecutionResult executed = default;
            bool restorationAdopted = false;
            try
            {
                int? routingHint = !trackLifetime || string.Equals(routine.TargetAppPath, routine.TriggerAppPath, StringComparison.OrdinalIgnoreCase)
                    ? processId > 0 ? processId : null : null;
                RoutineStatefulActivationExecutionResult activationResult = await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
                    routine,
                    routine.TriggerKind == RoutineTriggerKind.Application ? processId : null,
                    showOverlay,
                    executionSource,
                    _logger,
                    CaptureRoutineRestoreSnapshotIfNeeded,
                    async (targetRoutine, shouldShowOverlay, rootProcessId, source) =>
                    {
                        executed = await InvokeOnDispatcherAsync(
                            () => ExecuteRoutineAsync(targetRoutine, shouldShowOverlay, applicationProcessId: targetRoutine.RouteToApplication ? routingHint : rootProcessId, executionSource: source, correlatedOperationId: correlatedOperationId, cancellationToken: cancellationToken, captureAudioRestoration: trackLifetime && targetRoutine.RestorePreviousAudioOnDeactivate && targetRoutine.IsStatefulTrigger),
                            BuildSkippedRoutineExecutionResult());
                        return executed;
                    },
                    (target, pid, restore) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (executed.OwnsRoleRestoration && restore is { } oldSnapshot)
                            restore = oldSnapshot with
                            {
                                PreviousOutputDeviceId = "",
                                PreviousInputDeviceId = "",
                                PreviousOutputVolumePercent = target.HasOutputTarget ? null : oldSnapshot.PreviousOutputVolumePercent,
                                PreviousInputVolumePercent = target.HasInputTarget ? null : oldSnapshot.PreviousInputVolumePercent,
                            };
                        if (executed.OwnsVolumeRestoration && restore is { } volumeSnapshot)
                            restore = volumeSnapshot with { PreviousOutputVolumePercent = null, PreviousInputVolumePercent = null };
                        if (executed.AudioRestoration != null)
                            restore = (restore ?? new RoutineAudioRestoreSnapshot("", "", "", "")) with { AudioRestoration = executed.AudioRestoration };
                        RegisterRoutineStatefulSession(target, pid, restore, processIdentity,
                            executed.RoutingProcessId is int routedPid ? _routineRuntime.Lifetime.FindLease(RoutineApplicationRouting.CreateRoutineAppOutputLeaseKey(target.Id, routedPid)) : null, cancellationToken);
                        restorationAdopted = true;
                    },
                    BuildRoutineExecutionLogContext,
                    BuildRoutineExecutionResultLogContext, trackLifetime, cancellationToken);
                RoutineExecutionResult result = activationResult.Result;

                return result;
            }
            finally
            {
                if (!restorationAdopted && executed.AudioRestoration is { } restoration)
                    await Task.Run(() => restoration.Complete(true), CancellationToken.None);
            }
        }

        private async void OnRoutineTriggeredFromHotkey(AudioRoutine routine)
        {
            long generation = _hotkeys.CaptureGeneration;
            try
            {
                await InvokeOnDispatcherAsync(async () =>
                {
                    if (IsRuntimeShutdownRequested || IsUpdateAdmissionBlocked || _isCleaningUp || !_hotkeys.CanDispatch(generation)) return;
                    AudioRoutine? current;
                    lock (_settingsLock)
                    {
                        current = _cachedSettings?.Routines.Items.FirstOrDefault(candidate =>
                            candidate.Enabled && string.Equals(candidate.Id, routine.Id, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(candidate.HotkeyCycleGroup, routine.HotkeyCycleGroup, StringComparison.OrdinalIgnoreCase) &&
                            AreHotkeyStringsEquivalent(candidate.Hotkey, routine.Hotkey));
                    }
                    if (current != null && _hotkeyRegistrationCoordinator.IsCurrentRoutineHotkeyRegistered(current))
                        await ExecuteRoutineFromHotkeyAsync(current);
                });
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "routine-hotkey-dispatch-failed", nameof(OnRoutineTriggeredFromHotkey), ex);
            }
        }

        private async Task ExecuteRoutineFromHotkeyAsync(AudioRoutine routine)
        {
            if (!routine.Enabled)
            {
                return;
            }

            if (!string.IsNullOrEmpty(routine.HotkeyCycleGroup))
            {
                await CycleRoutineHotkeyGroupAsync(routine.HotkeyCycleGroup);
                return;
            }
            await RunRoutineManuallyAsync(routine, "hotkey");
        }

        private readonly RoutineHotkeyCycleService _routineHotkeyCycles = new();

        private async Task CycleRoutineHotkeyGroupAsync(string group)
        {
            bool executionAttempted = false;
            var skippedCodes = new HashSet<string>(StringComparer.Ordinal);
            RoutineCycleResult cycle = await _routineHotkeyCycles.RunAsync(group, GetPersistedRoutineSnapshot(), async (candidate, token) =>
            {
                var resolution = await Task.Run(() =>
                {
                    bool found = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(candidate, _routineRuntime.ProcessSnapshots, out int? pid, out string? code, out string? message);
                    return (found, pid, code, message);
                }, token);
                if (!resolution.found)
                {
                    if (resolution.code != null) skippedCodes.Add(resolution.code);
                    SetRoutineLastRunState(candidate, RoutineLastRunState.Skipped,
                        resolution.code == CliRoutineExecutionPolicy.TargetAppUnavailableCode ? "Skipped (app state unavailable)" : "Skipped (app not running)",
                        skipCode: resolution.code);
                    return new RoutineExecutionResult(false, null, null, Skipped: true, SkipCode: resolution.code, SkipReason: resolution.message);
                }
                executionAttempted = true;
                return await ExecuteRoutineForResolvedProcessAsync(candidate, resolution.pid ?? 0, showOverlay: false,
                    executionSource: "hotkey", trackLifetime: false, cancellationToken: token);
            }, ShutdownToken);
            _logger.Debug("AppViewModel", () => $"routine-cycle-result | group={LogPrivacy.Label(group)} code={cycle.Code} routineId={LogPrivacy.Id(cycle.Routine?.Id)}");
            if (cycle.Code == "routine-cycle-busy") return;
            if (cycle.Code == "routine-cycle-no-eligible-routines" && !executionAttempted)
                RecordExecutionHistory(new($"routine-cycle:{Guid.NewGuid():N}", DateTimeOffset.UtcNow, ExecutionHistoryKind.Routine,
                    "hotkey", "routine-cycle", false, true, "No eligible routines in the hotkey group.",
                    "Group members are disabled or their target application is not available.", DiagCode: cycle.Code,
                    Details: new Dictionary<string, string> { ["skipCodes"] = string.Join(",", skippedCodes.Order(StringComparer.Ordinal)) }));
            if (cycle.Routine is { } selected)
            {
                if (cycle.Execution.OutputSucceeded == true && !cycle.Execution.AwaitingAppCompletion && !string.IsNullOrWhiteSpace(cycle.Execution.OutputDeviceName))
                    MarkSwitchOverlayShown(output: true);
                if (cycle.Execution.InputSucceeded == true && !string.IsNullOrWhiteSpace(cycle.Execution.InputDeviceName))
                    MarkSwitchOverlayShown(output: false);
                _overlay.Show(cycle.Execution.Success ? OverlayDeviceKind.Output : OverlayDeviceKind.Error,
                    cycle.Execution.Success ? selected.Name : "Routine failed",
                    cycle.Execution.Success ? selected.TargetSummary : cycle.Execution.OutputFailureDetail ?? cycle.Execution.InputFailureDetail ??
                        cycle.Execution.CommunicationsFailureDetail ?? cycle.Execution.MuteFailureDetail ?? cycle.Execution.InputMonitoringFailureDetail ?? cycle.Execution.AppAudioFailureDetail ?? selected.Name,
                    icon: cycle.Execution.Success ? OverlayIcon.None : OverlayIcon.Warning);
                if (!cycle.Execution.Success && !_overlay.IsOverlayEnabled())
                    ShowManualRoutineFailure(selected.Name);
            }
            else _overlay.Show(OverlayDeviceKind.Error, "No eligible routines", group);
        }

        internal bool CanRunRoutineManually(AudioRoutine? routine) => routine is { Enabled: true } && Routines.Contains(routine)
            && !IsRuntimeShutdownRequested && !_isCleaningUp && !IsResettingSettings && !_isApplyingSettings && !_isSaving && !IsSavingRoutines;

        private async Task RunRoutineManuallyAsync(AudioRoutine routine, string source)
        {
            AudioRoutine snapshot = routine.Clone();
            var (success, processId, errorCode, errorMessage) = await Task.Run(() =>
            {
                bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
                    snapshot, _routineRuntime.ProcessSnapshots, out int? resolvedProcessId, out string? code, out string? message);
                return (resolved, resolvedProcessId, code, message);
            }, ShutdownToken);
            if (!success)
            {
                bool unknown = errorCode == CliRoutineExecutionPolicy.TargetAppUnavailableCode;
                SetRoutineLastRunState(routine, RoutineLastRunState.Skipped, unknown ? "Skipped (app state unavailable)" : "Skipped (app not running)",
                    skipCode: errorCode);
                RecordExecutionHistory(ExecutionHistoryEntry.RoutineSkipped(snapshot, source,
                    errorCode ?? "routine-target-app-not-running", errorMessage ?? "Target application is not running."));
                _overlay.Show(OverlayDeviceKind.Error, unknown ? "Application state unavailable" : "Application not running",
                    CliRoutineExecutionPolicy.GetApplicationDisplayName(routine.TargetAppPath));
                _logger.Info("AppViewModel", () => $"routine-manual-run-skipped | routineId={LogPrivacy.Id(routine.Id)} source={source} reason={errorCode}");
                return;
            }
            await ExecuteRoutineForResolvedProcessAsync(snapshot, processId ?? 0, showOverlay: true,
                executionSource: source, cancellationToken: ShutdownToken, trackLifetime: false);
        }

        internal async Task<RoutineExecutionResult> ExecuteRoutineAsync(AudioRoutine routine, bool showOverlay, int? applicationProcessId = null, string executionSource = "manual", string? correlatedOperationId = null, bool captureAudioRestoration = false, bool isTestRun = false, CancellationToken cancellationToken = default)
        {
            bool isManual = IsManualRoutineSource(executionSource);
            _logger.Debug("AppViewModel",
                () => $"routine-execution-started | {BuildRoutineExecutionLogContext(routine, executionSource, showOverlay, applicationProcessId)}{BuildRoutineExecutionCorrelationLogContext(correlatedOperationId)}");
            routine = routine.Clone();
            if (routine.RouteToApplication && applicationProcessId is not > 0)
            {
                var resolution = await Task.Run(() =>
                {
                    bool found = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, _routineRuntime.ProcessSnapshots,
                        out int? pid, out string? code, out string? message);
                    return (found, pid, code, message);
                }, cancellationToken);
                if (!resolution.found)
                {
                    if (showOverlay && isManual) _overlay.Show(OverlayDeviceKind.Error,
                        resolution.code == CliRoutineExecutionPolicy.TargetAppUnavailableCode ? "Application state unavailable" : "Application not running",
                        CliRoutineExecutionPolicy.GetApplicationDisplayName(routine.TargetAppPath));
                    RoutineExecutionResult notRunningResult = new(false, null, null, Skipped: true,
                        OutputSucceeded: routine.HasOutputTarget ? false : null,
                        InputSucceeded: routine.HasInputTarget ? false : null,
                        OutputFailureDetail: routine.HasOutputTarget ? resolution.message : null,
                        InputFailureDetail: routine.HasInputTarget ? resolution.message : null,
                        AppAudioSucceeded: routine.HasAppAudioTarget ? false : null,
                        AppAudioFailureDetail: routine.HasAppAudioTarget ? resolution.message : null,
                        SkipCode: resolution.code, SkipReason: resolution.message);
                    return isTestRun ? notRunningResult : CompleteRoutineExecution(routine, executionSource, showOverlay, null, notRunningResult, correlatedOperationId);
                }
                applicationProcessId = resolution.pid;
            }
            RoutineExecutionResult finalResult = await CreateRoutineExecutionService().ExecuteAsync(
                routine, new RoutineExecutionOptions(_preserveAudioLevelsBackingField, _muteMicBackingField, _muteSoundBackingField, _deafenBackingField, AllowDeferredRouting: !isTestRun, CaptureAudioRestoration: captureAudioRestoration), applicationProcessId, cancellationToken);
            try
            {
                if (finalResult.SkipCode != null)
                {
                    if (showOverlay && isManual) _overlay.Show(OverlayDeviceKind.Error, "Routine skipped", finalResult.SkipReason ?? string.Empty);
                    return isTestRun ? finalResult : CompleteRoutineExecution(routine, executionSource, showOverlay, applicationProcessId, finalResult, correlatedOperationId);
                }
                if (!isTestRun && routine.RouteToApplication && applicationProcessId is > 0 && finalResult.HasPerAppRoutingContinuation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pendingSound = routine.PlaySoundAfterApplying && executionSource != "cli" && finalResult.Success && finalResult.AwaitingAppCompletion
                        ? new RoutineDeferredSound(finalResult.AudioChanged) : null;
                    RegisterRoutineAppOutputLease(routine, applicationProcessId.Value, finalResult.AppOutputApplied, finalResult.AppInputApplied,
                        finalResult.Success && !finalResult.AwaitingAppCompletion, _routineRuntime.ProcessSnapshots.TryCapture(applicationProcessId.Value),
                        isManual, pendingSound, finalResult.AppAudioApplied, captureAudioRestoration, finalResult.AppliedAppAudioProcessIds, cancellationToken);
                    QueueRoutineAppOutputLeaseRefresh();
                }
                if (showOverlay)
                {
                    if (finalResult.OutputSucceeded == true && !finalResult.AwaitingAppCompletion && !string.IsNullOrWhiteSpace(finalResult.OutputDeviceName))
                        MarkSwitchOverlayShown(output: true);
                    if (finalResult.InputSucceeded == true && !string.IsNullOrWhiteSpace(finalResult.InputDeviceName))
                        MarkSwitchOverlayShown(output: false);
                }
                AppViewModelRoutineCompletionDecisionHelper.RoutineCompletionDecision completionDecision = AppViewModelRoutineCompletionDecisionHelper.Decide(
                    showOverlay, routine.Name, routine.OutputDeviceName, routine.InputDeviceName, finalResult);

                bool showFailureFeedback = showOverlay && (isManual || (_shell.IsWindowVisible && !finalResult.IsExpectedAutomaticSkip));
                string? actionFailure = finalResult.MuteFailureDetail ?? finalResult.CommunicationsFailureDetail ?? finalResult.InputMonitoringFailureDetail ?? (finalResult.AppAudioSucceeded == false ? finalResult.AppAudioFailureDetail : null);
                if (showFailureFeedback && actionFailure != null)
                {
                    _overlay.Show(OverlayDeviceKind.Error, "Routine action unavailable", actionFailure);
                }
                else if (showFailureFeedback && completionDecision.ShowFailureOverlay)
                {
                    ShowRoutineFailureOverlay(completionDecision.FailureOverlayPlan);
                }
                if (showOverlay && isManual && executionSource != "cli" && !finalResult.Success && !finalResult.Skipped
                    && (!_overlay.IsOverlayEnabled() || (actionFailure == null && !completionDecision.ShowFailureOverlay)))
                {
                    ShowManualRoutineFailure(routine.Name);
                }

                if (showOverlay && finalResult.Success && routine.HasCommunicationsTarget && !finalResult.AwaitingAppCompletion)
                {
                    _overlay.Show(OverlayDeviceKind.Output, routine.Name, routine.TargetSummary, icon: OverlayIcon.None);
                }
                else if (completionDecision.ShowSuccessOverlay)
                {
                    ShowRoutineSuccessOverlay(completionDecision.SuccessOverlayPlan);
                }
                else if (showOverlay && finalResult.Success && (routine.HasMuteTarget || routine.HasInputMonitoringTarget || routine.HasAppAudioTarget) && !finalResult.AwaitingAppCompletion)
                {
                    _overlay.Show(OverlayDeviceKind.Output, routine.Name, routine.TargetSummary, icon: OverlayIcon.None);
                }

                return isTestRun
                    ? finalResult
                    : CompleteRoutineExecution(
                        routine,
                        executionSource,
                        showOverlay,
                        applicationProcessId,
                        finalResult,
                        correlatedOperationId);
            }
            catch
            {
                if (finalResult.AudioRestoration is { } restoration)
                    await Task.Run(() => restoration.Complete(true), CancellationToken.None);
                throw;
            }
        }

        private static bool IsManualRoutineSource(string source) => source is "hotkey" or "tray" or "cli" or "manual";

        private void ShowManualRoutineFailure(string routineName)
        {
            if (_isCleaningUp || IsRuntimeShutdownRequested || IsUpdateAdmissionBlocked) return;
            RunBackgroundWork(token =>
                _dialogs.ShowAsync(AppDialogRequest.Acknowledge(new LocalizedText("Dialog_RoutineActionsFailed", routineName),
                    DialogText.Captions.Error, AppDialogKind.Error) with
                {
                    CaptionResource = new("Overlay_RoutineActionUnavailable"),
                }, token), "show-routine-failure-dialog");
        }

        private void ShowRoutineSuccessOverlay(AppViewModelRoutineOverlayHelper.RoutineSuccessOverlayPlan plan)
        {
            if (plan.ShowCombined)
            {
                _overlay.ShowRoutine(
                    plan.Header,
                    plan.OutputDeviceName,
                    plan.InputDeviceName);
                return;
            }

            _overlay.Show(
                plan.Kind,
                plan.Header,
                plan.DeviceName ?? string.Empty);
        }

        private RoutineExecutionResult CompleteRoutineExecution(AudioRoutine routine, string executionSource, bool showOverlay, int? applicationProcessId, RoutineExecutionResult result, string? correlatedOperationId = null)
        {
            string executionContext = BuildRoutineExecutionLogContext(routine, executionSource, showOverlay, applicationProcessId);
            string correlationContext = BuildRoutineExecutionCorrelationLogContext(correlatedOperationId);
            string resultContext = BuildRoutineExecutionResultLogContext(result);
            string eventName = GetRoutineCompletionEventName(result);

            UpdateRoutineLastRunState(routine, result);
            RecordRoutineExecutionHistory(routine, executionSource, result, correlatedOperationId);
            if (!_isCleaningUp)
            {
                // A completed activation can outlive the saved automatic configuration.
                bool enabled;
                lock (_settingsLock)
                    enabled = _cachedSettings?.Routines.Items.Any(current =>
                        string.Equals(current.Id, routine.Id, StringComparison.OrdinalIgnoreCase) && current.Enabled && current.Triggers.Count > 0) == true;
                _shell.ReportRoutineResult(routine.Id, enabled, result.Success,
                    result.IsExpectedAutomaticSkip || (result.Success && result.AwaitingAppCompletion),
                    !IsManualRoutineSource(executionSource));
            }

            if (result.Skipped)
            {
                _logger.Debug("AppViewModel", () => $"{eventName} | {executionContext}{correlationContext} {resultContext}");
                return result;
            }

            if (!result.Success)
            {
                _logger.Warning("AppViewModel", () => $"{eventName} | {executionContext}{correlationContext} {resultContext}");
                return result;
            }

            _logger.Debug("AppViewModel", () => $"{eventName} | {executionContext}{correlationContext} {resultContext}");
            return result;
        }

        private void ShowRoutineFailureOverlay(AppViewModelRoutineOverlayHelper.RoutineFailureOverlayPlan plan)
        {
            if (plan.IsPartial)
            {
                _overlay.ShowRoutinePartial(
                    plan.Header,
                    plan.SuccessfulOutputName,
                    plan.SuccessfulInputName,
                    plan.FailedOutputName,
                    plan.FailedInputName);
                return;
            }

            _overlay.Show(plan.Kind, plan.Header, plan.DeviceName ?? string.Empty);
        }

        internal Func<bool>? IsMicrophoneHoldControlling { get; set; }

        private string? GetRoutineMuteBlockReason(bool playback, bool muted)
        {
            if (!muted && Deafen) return "Unmute is blocked while deafen is active.";
            if (!playback && IsMicrophoneHoldControlling?.Invoke() == true)
                return "Microphone mute is controlled by push-to-talk or a held microphone shortcut.";
            return null;
        }

        private bool IsNetworkTriggerStillSatisfied(AudioRoutine routine)
        {
            if (routine.TriggerKind != RoutineTriggerKind.Network) return true;
            IReadOnlyCollection<string>? connected = RoutineExecutionOperationsForTests?.GetConnectedNetworks?.Invoke()
                ?? RoutineReadOperations.Create(_audio, _logger, _routineRuntime.ProcessSnapshots).GetConnectedNetworks?.Invoke();
            if (connected == null) return true;
            bool matchesTarget = string.IsNullOrWhiteSpace(routine.TriggerNetworkName)
                ? connected.Count > 0
                : connected.Contains(routine.TriggerNetworkName, StringComparer.OrdinalIgnoreCase);
            return routine.NetworkTriggerDirection switch
            {
                NetworkTriggerDirection.Connect => matchesTarget,
                NetworkTriggerDirection.Disconnect => !matchesTarget,
                _ => true,
            };
        }

        private RoutineExecutionService CreateRoutineExecutionService()
        {
            Settings settings = CurrentSettings ?? new Settings();
            RoutineExecutionOperations operations = RoutineExecutionOperationsForTests ?? RoutineExecutionOperations.Create(_audio, GetRoutineBluetoothReconnectCoordinator,
                BluetoothReconnectOptions.FromSettings(settings), _logger, processSnapshots: _routineRuntime.ProcessSnapshots, muteGuard: GetRoutineMuteBlockReason,
                getMonitorOutput: () =>
                {
                    var listen = (CurrentSettings ?? settings).Hotkeys.Listen;
                    return (listen.MonitorOutputDeviceId, listen.MonitorOutputDeviceName, listen.MonitorOutputDeviceStableId);
                });
            if (ApplyRoutineAbsoluteVolumeOverrideForTests is { } applyVolume)
                operations = operations with { ApplyVolume = applyVolume, ApplyOwnedVolume = null };
            return new RoutineExecutionService(operations, _logger);
        }

        private BluetoothReconnectCoordinator GetRoutineBluetoothReconnectCoordinator()
        {
            _routineBluetoothReconnectCoordinator ??= _routineBluetoothReconnectCoordinatorFactory?.Invoke()
                ?? new BluetoothReconnectCoordinator(new BluetoothReconnectService(), _logger);
            return _routineBluetoothReconnectCoordinator;
        }

        internal static string BuildRoutineExecutionLogContext(AudioRoutine routine, string executionSource, bool showOverlay, int? applicationProcessId)
        {
            ArgumentNullException.ThrowIfNull(routine);

            string routineId = string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id;
            string applicationProcessValue = FormatRoutineLogProcessId(applicationProcessId);
            return $"routineId={FormatRoutineLogIdentifier(routineId)} routineName={FormatRoutineLogLabel(routine.Name)} source={NormalizeRoutineLogValue(executionSource)} triggerKind={routine.TriggerKind} triggerDelaySeconds={routine.TriggerDelaySeconds} showOverlay={showOverlay} applicationProcessId={applicationProcessValue} hasOutputTarget={routine.HasOutputTarget} hasInputTarget={routine.HasInputTarget} hasMasterVolumeTarget={routine.HasMasterVolumeTarget} hasMicVolumeTarget={routine.HasMicVolumeTarget} hasInputMonitoringTarget={routine.HasInputMonitoringTarget} hasAppAudioTarget={routine.HasAppAudioTarget} routeToApplication={routine.RouteToApplication}";
        }

        internal static string BuildRoutineExecutionResultLogContext(RoutineExecutionResult result)
        {
            string elapsedMs = result.ElapsedMs.HasValue ? result.ElapsedMs.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "none";
            return $"success={result.Success} skipped={result.Skipped} skipCode={NormalizeRoutineLogValue(result.SkipCode)} awaitingAppCompletion={result.AwaitingAppCompletion} appOutputApplied={result.AppOutputApplied} appInputApplied={result.AppInputApplied} outputSucceeded={FormatRoutineLogBool(result.OutputSucceeded)} inputSucceeded={FormatRoutineLogBool(result.InputSucceeded)} masterVolumeSucceeded={FormatRoutineLogBool(result.MasterVolumeSucceeded)} micVolumeSucceeded={FormatRoutineLogBool(result.MicVolumeSucceeded)} outputReconnectAttempted={result.OutputReconnectAttempted} outputReconnectSucceeded={result.OutputReconnectSucceeded} inputReconnectAttempted={result.InputReconnectAttempted} inputReconnectSucceeded={result.InputReconnectSucceeded} outputDevice={FormatRoutineLogDevice(result.OutputDeviceName)} inputDevice={FormatRoutineLogDevice(result.InputDeviceName)} outputFailureDetail={NormalizeRoutineLogValue(result.OutputFailureDetail)} inputFailureDetail={NormalizeRoutineLogValue(result.InputFailureDetail)} outputMuteSucceeded={FormatRoutineLogBool(result.OutputMuteSucceeded)} inputMuteSucceeded={FormatRoutineLogBool(result.InputMuteSucceeded)} communicationsOutputSucceeded={FormatRoutineLogBool(result.CommunicationsOutputSucceeded)} communicationsInputSucceeded={FormatRoutineLogBool(result.CommunicationsInputSucceeded)} communicationsFailureDetail={NormalizeRoutineLogValue(result.CommunicationsFailureDetail)} muteFailureDetail={NormalizeRoutineLogValue(result.MuteFailureDetail)} elapsedMs={elapsedMs}";
        }

        internal static string BuildRoutineExecutionCorrelationLogContext(string? correlatedOperationId)
        {
            return string.IsNullOrWhiteSpace(correlatedOperationId)
                ? string.Empty
                : $" opId={NormalizeRoutineLogValue(correlatedOperationId)}";
        }

        internal static string GetRoutineCompletionEventName(RoutineExecutionResult result)
        {
            if (result.Skipped)
            {
                return "routine-execution-skipped";
            }

            if (!result.Success)
            {
                return result.HasPartialSuccess
                    ? "routine-execution-partial-failure"
                    : "routine-execution-failed";
            }

            return result.AwaitingAppCompletion
                ? "routine-execution-awaiting-app-completion"
                : "routine-execution-completed";
        }

        internal static string CreateRoutineOperationId(string prefix)
        {
            string normalizedPrefix = NormalizeRoutineLogValue(prefix);
            return $"{normalizedPrefix}:{Guid.NewGuid():N}";
        }

        internal static string NormalizeRoutineLogValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "none";
            }

            string normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Replace('|', '/');
        }

        internal static string FormatRoutineLogIdentifier(string? value)
        {
            string normalized = NormalizeRoutineLogValue(value);
            return string.Equals(normalized, "none", StringComparison.Ordinal)
                ? normalized
                : LogPrivacy.Id(normalized);
        }

        internal static string FormatRoutineLogProcessId(int? processId)
        {
            return processId is > 0
                ? LogPrivacy.Id(processId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : "none";
        }

        internal static string FormatRoutineLogLabel(string? value)
        {
            string normalized = NormalizeRoutineLogValue(value);
            return string.Equals(normalized, "none", StringComparison.Ordinal)
                ? normalized
                : LogPrivacy.Label(normalized);
        }

        internal static string FormatRoutineLogDevice(string? value)
        {
            string normalized = NormalizeRoutineLogValue(value);
            return string.Equals(normalized, "none", StringComparison.Ordinal)
                ? normalized
                : LogPrivacy.Device(normalized);
        }

        internal static string FormatRoutineLogSession(string? value)
        {
            string normalized = NormalizeRoutineLogValue(value);
            return string.Equals(normalized, "none", StringComparison.Ordinal)
                ? normalized
                : LogPrivacy.Session(normalized);
        }

        internal static string FormatRoutineLogBool(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "none";
        }
    }
}
