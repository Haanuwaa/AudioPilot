using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private async Task<RoutineExecutionResult> ExecuteRoutineForResolvedProcessAsync(AudioRoutine routine, int processId, bool showOverlay, string executionSource, string? correlatedOperationId = null, bool trackLifetime = true, CancellationToken cancellationToken = default)
        {
            AudioRoutine snapshot = routine.Clone();
            if (!trackLifetime && snapshot.Triggers.Count > 0 && string.IsNullOrEmpty(snapshot.TriggerIdentity))
                snapshot.TriggerIdentity = snapshot.Triggers[0].Id;
            try
            {
                return await _routineLifetime.RunActivationAsync(snapshot, processId,
                    token => ExecuteRoutineActivationAsync(snapshot, processId, showOverlay, executionSource, correlatedOperationId, trackLifetime, token), isManual: executionSource is "hotkey" or "tray" or "cli" or "manual", cancellationToken: cancellationToken);
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

            if (executionSource == "device-availability" && !_routineDeviceAvailability.IsCurrentConfiguration(routine))
                return BuildSkippedRoutineExecutionResult();
            RoutineProcessSnapshot? processIdentity = processId > 0
                ? _routineProcessSnapshotProvider.TryCapture(processId) : null;
            if (executionSource is not ("hotkey" or "tray" or "cli" or "manual") &&
                _routineLifetime.TryJoinActiveRoutine(routine, processId, processIdentity, trackLifetime, cancellationToken))
            {
                _logger.Debug("AppViewModel", () => $"routine-trigger-joined | routineId={LogPrivacy.Id(routine.Id)} trigger={LogPrivacy.Id(routine.RuntimeTriggerKey)} source={executionSource}");
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
                            () => ExecuteRoutineAsync(targetRoutine, shouldShowOverlay, applicationProcessId: targetRoutine.SwitchOutputPerApp ? routingHint : rootProcessId, executionSource: source, correlatedOperationId: correlatedOperationId, cancellationToken: cancellationToken, captureAudioRestoration: trackLifetime && targetRoutine.RestorePreviousAudioOnDeactivate && targetRoutine.IsStatefulTrigger),
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
                            executed.RoutingProcessId is int routedPid ? _routineLifetime.FindLease(RoutineApplicationRouting.CreateRoutineAppOutputLeaseKey(target.Id, routedPid)) : null, cancellationToken);
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
            try
            {
                await InvokeOnDispatcherAsync(() => ExecuteRoutineFromHotkeyAsync(routine));
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
            RoutineCycleResult cycle = await _routineHotkeyCycles.RunAsync(group, GetPersistedRoutineSnapshot(), async (candidate, token) =>
            {
                var resolution = await Task.Run(() =>
                {
                    bool found = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(candidate, _routineProcessSnapshotProvider, out int? pid, out _, out _);
                    return (found, pid);
                }, token);
                if (!resolution.found)
                {
                    SetRoutineLastRunState(candidate, RoutineLastRunState.Skipped, "Skipped (app not running)");
                    return new RoutineExecutionResult(false, null, null, Skipped: true, SkipCode: "routine-target-app-not-running");
                }
                return await ExecuteRoutineForResolvedProcessAsync(candidate, resolution.pid ?? 0, showOverlay: false,
                    executionSource: "hotkey", trackLifetime: false, cancellationToken: token);
            }, ShutdownToken);
            _logger.Debug("AppViewModel", () => $"routine-cycle-result | group={LogPrivacy.Label(group)} code={cycle.Code} routineId={LogPrivacy.Id(cycle.Routine?.Id)}");
            if (cycle.Code == "routine-cycle-busy") return;
            if (cycle.Routine is { } selected)
                _overlay.Show(cycle.Execution.Success ? OverlayDeviceKind.Output : OverlayDeviceKind.Error,
                    cycle.Execution.Success ? selected.Name : "Routine failed",
                    cycle.Execution.Success ? selected.TargetSummary : cycle.Execution.OutputFailureDetail ?? cycle.Execution.InputFailureDetail ??
                        cycle.Execution.CommunicationsFailureDetail ?? cycle.Execution.MuteFailureDetail ?? selected.Name,
                    icon: cycle.Execution.Success ? OverlayIcon.None : OverlayIcon.Warning);
            else _overlay.Show(OverlayDeviceKind.Error, "No eligible routines", group);
        }

        private async Task RunRoutineManuallyAsync(AudioRoutine routine, string source)
        {
            AudioRoutine snapshot = routine.Clone();
            var (success, processId) = await Task.Run(() =>
            {
                bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
                    snapshot, _routineProcessSnapshotProvider, out int? resolvedProcessId, out _, out _);
                return (resolved, resolvedProcessId);
            }, ShutdownToken);
            if (!success)
            {
                SetRoutineLastRunState(routine, RoutineLastRunState.Skipped, "Skipped (app not running)");
                _overlay.Show(OverlayDeviceKind.Error, "Application not running",
                    CliRoutineExecutionPolicy.GetApplicationDisplayName(routine.TargetAppPath));
                _logger.Info("AppViewModel", () => $"routine-manual-run-skipped | routineId={LogPrivacy.Id(routine.Id)} source={source} reason=app-not-running");
                return;
            }
            await ExecuteRoutineForResolvedProcessAsync(snapshot, processId ?? 0, showOverlay: true,
                executionSource: source, cancellationToken: ShutdownToken, trackLifetime: false);
        }

        internal async Task<RoutineExecutionResult> ExecuteRoutineAsync(AudioRoutine routine, bool showOverlay, int? applicationProcessId = null, string executionSource = "manual", string? correlatedOperationId = null, bool captureAudioRestoration = false, CancellationToken cancellationToken = default)
        {
            _logger.Debug("AppViewModel",
                () => $"routine-execution-started | {BuildRoutineExecutionLogContext(routine, executionSource, showOverlay, applicationProcessId)}{BuildRoutineExecutionCorrelationLogContext(correlatedOperationId)}");
            routine = routine.Clone();
            if (routine.SwitchOutputPerApp && applicationProcessId is not > 0)
            {
                var resolution = await Task.Run(() =>
                {
                    bool found = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, _routineProcessSnapshotProvider,
                        out int? pid, out _, out _);
                    return (found, pid);
                }, cancellationToken);
                if (!resolution.found)
                {
                    if (showOverlay) _overlay.Show(OverlayDeviceKind.Error, "Application not running",
                        CliRoutineExecutionPolicy.GetApplicationDisplayName(routine.TargetAppPath));
                    return CompleteRoutineExecution(routine, executionSource, showOverlay, null,
                        new(false, null, null, Skipped: true,
                            OutputSucceeded: routine.HasOutputTarget ? false : null,
                            InputSucceeded: routine.HasInputTarget ? false : null,
                            OutputFailureDetail: routine.HasOutputTarget ? "Target application is not running." : null,
                            InputFailureDetail: routine.HasInputTarget ? "Target application is not running." : null), correlatedOperationId);
                }
                applicationProcessId = resolution.pid;
            }
            RoutineExecutionResult finalResult = await CreateRoutineExecutionService().ExecuteAsync(
                routine, new RoutineExecutionOptions(_preserveAudioLevelsBackingField, _muteMicBackingField, _muteSoundBackingField, _deafenBackingField, CaptureAudioRestoration: captureAudioRestoration), applicationProcessId, cancellationToken);
            try
            {
                if (finalResult.SkipCode != null)
                {
                    if (showOverlay) _overlay.Show(OverlayDeviceKind.Error, "Routine skipped", finalResult.SkipReason ?? string.Empty);
                    return CompleteRoutineExecution(routine, executionSource, showOverlay, applicationProcessId, finalResult, correlatedOperationId);
                }
                if (routine.SwitchOutputPerApp && applicationProcessId is > 0 && finalResult.HasPerAppRoutingContinuation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RegisterRoutineAppOutputLease(routine, applicationProcessId.Value, finalResult.AppOutputApplied, finalResult.AppInputApplied,
                        finalResult.Success && !finalResult.AwaitingAppCompletion, _routineProcessSnapshotProvider.TryCapture(applicationProcessId.Value), cancellationToken);
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

                if (showOverlay && (finalResult.MuteFailureDetail ?? finalResult.CommunicationsFailureDetail) is { } muteFailure)
                {
                    _overlay.Show(OverlayDeviceKind.Error, "Routine action unavailable", muteFailure);
                }
                else if (completionDecision.ShowFailureOverlay)
                {
                    ShowRoutineFailureOverlay(completionDecision.FailureOverlayPlan);
                }

                if (showOverlay && finalResult.Success && routine.HasCommunicationsTarget && !finalResult.AwaitingAppCompletion)
                {
                    _overlay.Show(OverlayDeviceKind.Output, routine.Name, routine.TargetSummary, icon: OverlayIcon.None);
                }
                else if (completionDecision.ShowSuccessOverlay)
                {
                    ShowRoutineSuccessOverlay(completionDecision.SuccessOverlayPlan);
                }
                else if (showOverlay && finalResult.Success && routine.HasMuteTarget && !finalResult.AwaitingAppCompletion)
                {
                    _overlay.Show(OverlayDeviceKind.Output, routine.Name, routine.TargetSummary, icon: OverlayIcon.None);
                }

                return CompleteRoutineExecution(
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

        private RoutineExecutionService CreateRoutineExecutionService()
        {
            RoutineExecutionOperations operations = RoutineExecutionOperationsForTests ?? RoutineExecutionOperations.Create(_audio, GetRoutineBluetoothReconnectCoordinator,
                BluetoothReconnectOptions.FromSettings(CurrentSettings ?? new Settings()), _logger, RoutineReconnectPostAttemptDelayAsyncForTests, _routineProcessSnapshotProvider, GetRoutineMuteBlockReason);
            if (ApplyRoutineAbsoluteVolumeOverrideForTests is { } applyVolume)
                operations = operations with { ApplyVolume = applyVolume, ApplyOwnedVolume = null };
            return new RoutineExecutionService(operations, _logger);
        }

        private BluetoothReconnectCoordinator GetRoutineBluetoothReconnectCoordinator()
        {
            _routineBluetoothReconnectCoordinator ??= new BluetoothReconnectCoordinator(new BluetoothReconnectService(), _logger);
            return _routineBluetoothReconnectCoordinator;
        }

        internal static string BuildRoutineExecutionLogContext(AudioRoutine routine, string executionSource, bool showOverlay, int? applicationProcessId)
        {
            ArgumentNullException.ThrowIfNull(routine);

            string routineId = string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id;
            string applicationProcessValue = FormatRoutineLogProcessId(applicationProcessId);
            return $"routineId={FormatRoutineLogIdentifier(routineId)} routineName={FormatRoutineLogLabel(routine.Name)} source={NormalizeRoutineLogValue(executionSource)} triggerKind={routine.TriggerKind} showOverlay={showOverlay} applicationProcessId={applicationProcessValue} hasOutputTarget={routine.HasOutputTarget} hasInputTarget={routine.HasInputTarget} hasMasterVolumeTarget={routine.HasMasterVolumeTarget} hasMicVolumeTarget={routine.HasMicVolumeTarget} switchOutputPerApp={routine.SwitchOutputPerApp}";
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
