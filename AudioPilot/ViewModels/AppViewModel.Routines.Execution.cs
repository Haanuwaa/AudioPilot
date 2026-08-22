using System.Diagnostics;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private async Task<RoutineExecutionResult> ExecuteRoutineForResolvedProcessAsync(AudioRoutine routine, int processId, bool showOverlay, string executionSource, string? correlatedOperationId = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RoutineStatefulActivationExecutionResult activationResult = await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
                routine,
                processId,
                showOverlay,
                executionSource,
                _logger,
                CaptureRoutineRestoreSnapshotIfNeeded,
                async (targetRoutine, shouldShowOverlay, rootProcessId, source) =>
                {
                    return await InvokeOnDispatcherAsync(
                        () => ExecuteRoutineAsync(targetRoutine, shouldShowOverlay, applicationProcessId: rootProcessId, executionSource: source, correlatedOperationId: correlatedOperationId, cancellationToken: cancellationToken),
                        BuildSkippedRoutineExecutionResult());
                },
                RegisterRoutineStatefulSession,
                BuildRoutineExecutionLogContext,
                BuildRoutineExecutionResultLogContext);
            RoutineExecutionResult result = activationResult.Result;

            if (!routine.SwitchOutputPerApp ||
                processId <= 0 ||
                (string.IsNullOrWhiteSpace(routine.OutputDeviceId) && string.IsNullOrWhiteSpace(routine.InputDeviceId)) ||
                !result.HasPerAppRoutingContinuation)
            {
                if (routine.SwitchOutputPerApp)
                {
                    _logger.Info(
                        "AppViewModel",
                        () => $"routine-application-lease-skipped | {BuildRoutineExecutionLogContext(routine, executionSource, showOverlay, processId)}{BuildRoutineExecutionCorrelationLogContext(correlatedOperationId)} continuation={result.HasPerAppRoutingContinuation} appOutputApplied={result.AppOutputApplied} appInputApplied={result.AppInputApplied}");
                }

                return result;
            }

            RegisterRoutineAppOutputLease(
                routine,
                processId,
                result.AppOutputApplied,
                result.AppInputApplied,
                result.Success && !result.AwaitingAppCompletion);
            QueueRoutineAppOutputLeaseRefresh();
            return result;
        }

        private async void OnRoutineTriggeredFromHotkey(AudioRoutine routine)
        {
            try
            {
                await InvokeOnDispatcherAsync(() => ExecuteRoutineFromHotkeyAsync(routine));
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

            await ExecuteRoutineAsync(routine, showOverlay: true, executionSource: "hotkey");
        }

        internal async Task<RoutineExecutionResult> ExecuteRoutineAsync(AudioRoutine routine, bool showOverlay, int? applicationProcessId = null, string executionSource = "manual", string? correlatedOperationId = null, CancellationToken cancellationToken = default)
        {
            long executionStartTimestamp = Stopwatch.GetTimestamp();

            _logger.Debug(
                "AppViewModel",
                () => $"routine-execution-started | {BuildRoutineExecutionLogContext(routine, executionSource, showOverlay, applicationProcessId)}{BuildRoutineExecutionCorrelationLogContext(correlatedOperationId)}");

            string? appliedOutputDeviceName = null;
            string? appliedInputDeviceName = null;
            bool awaitingAppCompletion = false;
            bool appOutputApplied = false;
            bool appInputApplied = false;
            bool? outputSucceeded = null;
            bool? inputSucceeded = null;
            bool? masterVolumeSucceeded = null;
            bool? micVolumeSucceeded = null;
            string? outputFailureDetail = null;
            string? inputFailureDetail = null;
            bool outputReconnectAttempted = false;
            bool outputReconnectSucceeded = false;
            bool inputReconnectAttempted = false;
            bool inputReconnectSucceeded = false;

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(routine.OutputDeviceId))
            {
                RoutineDeviceSwitchExecutionResult outputResult = await ExecuteRoutineOutputSwitchAsync(routine, applicationProcessId, cancellationToken);
                outputSucceeded = outputResult.Success;
                awaitingAppCompletion = outputResult.AwaitingAppCompletion;
                appOutputApplied = outputResult.AppRouteApplied;
                outputFailureDetail = outputResult.FailureDetail;
                outputReconnectAttempted = outputResult.ReconnectAttempted;
                outputReconnectSucceeded = outputResult.ReconnectSucceeded;

                if (outputResult.Success && !string.IsNullOrWhiteSpace(outputResult.DeviceName))
                {
                    appliedOutputDeviceName = outputResult.DeviceName;

                    if (showOverlay && !awaitingAppCompletion)
                    {
                        MarkSwitchOverlayShown(output: true);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(routine.InputDeviceId))
            {
                RoutineDeviceSwitchExecutionResult inputResult = await ExecuteRoutineInputSwitchAsync(routine, applicationProcessId, cancellationToken);
                awaitingAppCompletion = awaitingAppCompletion || inputResult.AwaitingAppCompletion;
                appInputApplied = inputResult.AppRouteApplied;

                inputSucceeded = inputResult.Success;
                inputFailureDetail = inputResult.FailureDetail;
                inputReconnectAttempted = inputResult.ReconnectAttempted;
                inputReconnectSucceeded = inputResult.ReconnectSucceeded;

                if (inputResult.Success && !string.IsNullOrWhiteSpace(inputResult.DeviceName))
                {
                    appliedInputDeviceName = inputResult.DeviceName;

                    if (showOverlay)
                    {
                        MarkSwitchOverlayShown(output: false);
                    }
                }
            }

            if (routine.MasterVolumePercent.HasValue)
            {
                masterVolumeSucceeded = ApplyRoutineAbsoluteVolume(
                    playback: true,
                    targetDeviceId: string.IsNullOrWhiteSpace(routine.OutputDeviceId) ? null : routine.OutputDeviceId,
                    targetPercent: routine.MasterVolumePercent.Value,
                    reason: CreateRoutineOperationId("routine-volume-master"));
            }

            if (routine.MicVolumePercent.HasValue)
            {
                micVolumeSucceeded = ApplyRoutineAbsoluteVolume(
                    playback: false,
                    targetDeviceId: string.IsNullOrWhiteSpace(routine.InputDeviceId) ? null : routine.InputDeviceId,
                    targetPercent: routine.MicVolumePercent.Value,
                    reason: CreateRoutineOperationId("routine-volume-recording"));
            }

            AppViewModelRoutineCompletionDecisionHelper.RoutineCompletionDecision completionDecision = AppViewModelRoutineCompletionDecisionHelper.Decide(
                showOverlay,
                routine.Name,
                routine.OutputDeviceName,
                routine.InputDeviceName,
                appliedOutputDeviceName,
                appliedInputDeviceName,
                awaitingAppCompletion,
                appOutputApplied,
                appInputApplied,
                outputSucceeded,
                inputSucceeded,
                masterVolumeSucceeded,
                micVolumeSucceeded,
                outputFailureDetail,
                inputFailureDetail);

            if (completionDecision.ShowFailureOverlay)
            {
                ShowRoutineFailureOverlay(completionDecision.FailureOverlayPlan);
            }

            if (completionDecision.ShowSuccessOverlay)
            {
                ShowRoutineSuccessOverlay(completionDecision.SuccessOverlayPlan);
            }

            RoutineExecutionResult finalResult = completionDecision.Result with
            {
                ElapsedMs = Stopwatch.GetElapsedTime(executionStartTimestamp).TotalMilliseconds,
                OutputReconnectAttempted = outputReconnectAttempted,
                OutputReconnectSucceeded = outputReconnectSucceeded,
                InputReconnectAttempted = inputReconnectAttempted,
                InputReconnectSucceeded = inputReconnectSucceeded,
            };

            return CompleteRoutineExecution(
                routine,
                executionSource,
                showOverlay,
                applicationProcessId,
                finalResult,
                correlatedOperationId);
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

        internal async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutineOutputSwitchAsync(AudioRoutine routine, int? applicationProcessId, CancellationToken cancellationToken = default)
        {
            AudioRoutine resolvedRoutine = ReconcileRoutineTargetForExecution(routine, playback: true);

            try
            {
                return await ExecuteRoutineSwitchWithReconnectAsync(
                    resolvedRoutine,
                    applicationProcessId,
                    resolvedRoutine.OutputDeviceId,
                    resolvedRoutine.OutputDeviceName,
                    BluetoothReconnectDeviceKind.Output,
                    configured => _audio.TryGetActivePlaybackCycleEntry(configured.Id, configured.Name),
                    executePerAppSwitchAsync: processId => ExecuteRoutinePerAppOutputSwitchAsync(resolvedRoutine, processId),
                    executeDirectSwitchAsync: () => ExecuteRoutineDirectOutputSwitchAsync(resolvedRoutine, applicationProcessId),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "routine-output-switch-failed", nameof(ExecuteRoutineOutputSwitchAsync), ex);
                return new RoutineDeviceSwitchExecutionResult(false, null, FailureDetail: BuildRoutineSwitchExceptionDetail("output", ex));
            }
        }

        internal async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutineInputSwitchAsync(AudioRoutine routine, int? applicationProcessId, CancellationToken cancellationToken = default)
        {
            AudioRoutine resolvedRoutine = ReconcileRoutineTargetForExecution(routine, playback: false);

            try
            {
                return await ExecuteRoutineSwitchWithReconnectAsync(
                    resolvedRoutine,
                    applicationProcessId,
                    resolvedRoutine.InputDeviceId,
                    resolvedRoutine.InputDeviceName,
                    BluetoothReconnectDeviceKind.Input,
                    configured => _audio.TryGetActiveRecordingCycleEntry(configured.Id, configured.Name),
                    executePerAppSwitchAsync: processId => ExecuteRoutinePerAppInputSwitchAsync(resolvedRoutine, processId),
                    executeDirectSwitchAsync: () => ExecuteRoutineDirectInputSwitchAsync(resolvedRoutine, applicationProcessId),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "routine-input-switch-failed", nameof(ExecuteRoutineInputSwitchAsync), ex);
                return new RoutineDeviceSwitchExecutionResult(false, null, FailureDetail: BuildRoutineSwitchExceptionDetail("input", ex));
            }
        }

        private async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutineSwitchWithReconnectAsync(
            AudioRoutine routine,
            int? applicationProcessId,
            string? reconnectDeviceId,
            string? reconnectDeviceName,
            BluetoothReconnectDeviceKind reconnectDeviceKind,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            Func<int, Task<RoutineDeviceSwitchExecutionResult>> executePerAppSwitchAsync,
            Func<Task<RoutineDeviceSwitchExecutionResult>> executeDirectSwitchAsync,
            CancellationToken cancellationToken = default)
        {
            string reconnectFlow = reconnectDeviceKind == BluetoothReconnectDeviceKind.Input ? "input" : "output";
            string reconnectOpId = CreateRoutineOperationId($"routine-{reconnectFlow}-reconnect");

            RoutineReconnectOutcome reconnectOutcome = await TryReconnectRoutineTargetAsync(
                reconnectDeviceId,
                reconnectDeviceName,
                reconnectDeviceKind,
                tryResolveActiveDevice,
                reconnectOpId,
                routine,
                applicationProcessId,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (routine.SwitchOutputPerApp && applicationProcessId is > 0)
            {
                return (await executePerAppSwitchAsync(applicationProcessId.Value)) with
                {
                    ReconnectAttempted = reconnectOutcome.Attempted,
                    ReconnectSucceeded = reconnectOutcome.Succeeded,
                };
            }

            return (await executeDirectSwitchAsync()) with
            {
                ReconnectAttempted = reconnectOutcome.Attempted,
                ReconnectSucceeded = reconnectOutcome.Succeeded,
            };
        }

        private async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutinePerAppOutputSwitchAsync(AudioRoutine routine, int applicationProcessId)
        {
            string opId = CreateRoutineOperationId("routine-app-output");
            return await ExecuteRoutinePerAppSwitchAsync(
                routine,
                flow: "output",
                opId,
                applicationProcessId,
                switchAsync: switchOpId => _audio.SwitchApplicationOutputDeviceDetailedAsync(
                    (uint)applicationProcessId,
                    routine.OutputDeviceId,
                    routine.OutputDeviceName,
                    opId: switchOpId));
        }

        private async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutinePerAppInputSwitchAsync(AudioRoutine routine, int applicationProcessId)
        {
            string opId = CreateRoutineOperationId("routine-app-input");
            return await ExecuteRoutinePerAppSwitchAsync(
                routine,
                flow: "input",
                opId,
                applicationProcessId,
                switchAsync: switchOpId => _audio.SwitchApplicationInputDeviceDetailedAsync(
                    (uint)applicationProcessId,
                    routine.InputDeviceId,
                    routine.InputDeviceName,
                    opId: switchOpId));
        }

        private async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutinePerAppSwitchAsync(
            AudioRoutine routine,
            string flow,
            string opId,
            int applicationProcessId,
            Func<string, ValueTask<ProcessAudioDeviceSwitchResult>> switchAsync)
        {
            _logger.Debug(
                "AppViewModel",
                () => BuildRoutineProcessSwitchStartedMessage(routine, flow, opId, applicationProcessId));

            ProcessAudioDeviceSwitchResult result = await switchAsync(opId);
            string? failureDetail = result.Result == ProcessAudioRoutingResult.Applied
                ? null
                : BuildPerAppRoutingFailureDetail(flow, result.Result);

            _logger.Debug(
                "AppViewModel",
                () => BuildRoutineProcessSwitchCompletedMessage(routine, flow, opId, applicationProcessId, result));

            return new RoutineDeviceSwitchExecutionResult(
                result.Success,
                result.DeviceName,
                result.Result == ProcessAudioRoutingResult.DeferredNoAudio,
                result.Result == ProcessAudioRoutingResult.Applied,
                failureDetail);
        }

        private async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutineDirectOutputSwitchAsync(AudioRoutine routine, int? applicationProcessId)
        {
            MMDevice? currentDefault = null;
            try
            {
                currentDefault = _audio.GetDefaultPlaybackDevice();
                AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecision outputSwitchDecision = AppViewModelRoutineOutputSwitchGuardHelper.Evaluate(
                    currentDefault?.ID,
                    currentDefault?.FriendlyName,
                    routine.OutputDeviceId);

                if (outputSwitchDecision.Kind == AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecisionKind.MissingDefaultDevice)
                {
                    _logger.Warning(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildOutputSkippedNoDefaultMessage(routine, applicationProcessId));
                    return outputSwitchDecision.Result;
                }

                if (outputSwitchDecision.Kind == AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecisionKind.AlreadyTarget)
                {
                    _logger.Debug(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildOutputAlreadyTargetMessage(routine, applicationProcessId, outputSwitchDecision.CurrentDeviceName));
                    return outputSwitchDecision.Result;
                }

                string opId = CreateRoutineOperationId("routine-output");
                (bool preserveAudioLevels, bool restoreMasterVolume, bool restoreMicVolume) = ResolveRoutinePostSwitchRestoreOptions(routine);
                return await ExecuteRoutineDirectSwitchAsync(
                    routine,
                    flow: "output",
                    opId,
                    applicationProcessId,
                    outputSwitchDecision.CurrentDeviceName,
                    switchAsync: switchOpId => _audio.SwitchAudioDeviceAsync(
                        outputSwitchDecision.CurrentDeviceId!,
                        routine.OutputDeviceId,
                        _muteMicBackingField,
                        _muteSoundBackingField,
                        _deafenBackingField,
                        preserveAudioLevels,
                        restoreMasterVolume,
                        restoreMicVolume,
                        opId: switchOpId));
            }
            finally
            {
                currentDefault?.Dispose();
            }
        }

        private (bool PreserveAudioLevels, bool RestoreMasterVolume, bool RestoreMicVolume) ResolveRoutinePostSwitchRestoreOptions(AudioRoutine routine)
        {
            ArgumentNullException.ThrowIfNull(routine);

            return (
                PreserveAudioLevels: ShouldPreserveAudioLevelsForRoutineDeviceSwitch(),
                RestoreMasterVolume: !routine.MasterVolumePercent.HasValue,
                RestoreMicVolume: !routine.MicVolumePercent.HasValue);
        }

        private bool ShouldPreserveAudioLevelsForRoutineDeviceSwitch()
        {
            return _preserveAudioLevelsBackingField;
        }

        private async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutineDirectInputSwitchAsync(AudioRoutine routine, int? applicationProcessId)
        {
            MMDevice? currentDefault = null;
            try
            {
                currentDefault = _audio.GetDefaultRecordingDevice();
                AppViewModelRoutineInputSwitchGuardHelper.InputSwitchDecision inputSwitchDecision = AppViewModelRoutineInputSwitchGuardHelper.Evaluate(
                    currentDefault?.ID,
                    currentDefault?.FriendlyName,
                    routine.InputDeviceId);

                string opId = CreateRoutineOperationId("routine-input");

                if (inputSwitchDecision.Kind == AppViewModelRoutineInputSwitchGuardHelper.InputSwitchDecisionKind.MissingDefaultDevice)
                {
                    _logger.Debug(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildStartedMessage(routine, "input", opId, applicationProcessId, currentDefault?.FriendlyName));
                    _logger.Warning(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildInputSkippedNoDefaultMessage(routine, applicationProcessId));
                    _logger.Debug(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildCompletedMessage(routine, "input", opId, applicationProcessId, success: false, deviceName: null));
                    return inputSwitchDecision.Result;
                }

                if (inputSwitchDecision.Kind == AppViewModelRoutineInputSwitchGuardHelper.InputSwitchDecisionKind.AlreadyTarget)
                {
                    _logger.Debug(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildStartedMessage(routine, "input", opId, applicationProcessId, inputSwitchDecision.CurrentDeviceName));
                    _logger.Debug(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildInputAlreadyTargetMessage(routine, applicationProcessId, inputSwitchDecision.CurrentDeviceName));
                    _logger.Debug(
                        "AppViewModel",
                        () => AppViewModelRoutineDirectSwitchLogHelper.BuildCompletedMessage(routine, "input", opId, applicationProcessId, success: true, inputSwitchDecision.CurrentDeviceName));
                    return inputSwitchDecision.Result;
                }

                return await ExecuteRoutineDirectSwitchAsync(
                    routine,
                    flow: "input",
                    opId,
                    applicationProcessId,
                    inputSwitchDecision.CurrentDeviceName,
                    switchAsync: switchOpId => _audio.SwitchInputDeviceToAsync(
                        routine.InputDeviceId,
                        routine.InputDeviceName,
                        preserveAudioLevels: ShouldPreserveAudioLevelsForRoutineDeviceSwitch(),
                        showOverlay: null,
                        opId: switchOpId));
            }
            finally
            {
                currentDefault?.Dispose();
            }
        }

        private async Task<RoutineDeviceSwitchExecutionResult> ExecuteRoutineDirectSwitchAsync(
            AudioRoutine routine,
            string flow,
            string opId,
            int? applicationProcessId,
            string? currentDeviceName,
            Func<string, ValueTask<(bool Success, string? DeviceName)>> switchAsync)
        {
            _logger.Debug(
                "AppViewModel",
                () => AppViewModelRoutineDirectSwitchLogHelper.BuildStartedMessage(routine, flow, opId, applicationProcessId, currentDeviceName));

            (bool success, string? deviceName) = await switchAsync(opId);

            _logger.Debug(
                "AppViewModel",
                () => AppViewModelRoutineDirectSwitchLogHelper.BuildCompletedMessage(routine, flow, opId, applicationProcessId, success, deviceName));

            return new RoutineDeviceSwitchExecutionResult(
                success,
                deviceName,
                AwaitingAppCompletion: false,
                AppRouteApplied: false,
                FailureDetail: success ? null : BuildDirectRoutineSwitchFailureDetail(flow));
        }

        private static string BuildPerAppRoutingFailureDetail(string flow, ProcessAudioRoutingResult result)
        {
            return result switch
            {
                ProcessAudioRoutingResult.DeferredNoAudio => $"Per-app {flow} routing is pending until the application produces audio.",
                _ => $"Failed to apply per-app {flow} routing.",
            };
        }

        private static string BuildDirectRoutineSwitchFailureDetail(string flow)
        {
            return string.Equals(flow, "input", StringComparison.OrdinalIgnoreCase)
                ? "Failed to switch the default input device."
                : "Failed to switch the default output device.";
        }

        private AudioRoutine ReconcileRoutineTargetForExecution(AudioRoutine routine, bool playback)
        {
            ArgumentNullException.ThrowIfNull(routine);

            AudioRoutine resolvedRoutine = routine.Clone();
            CycleDevice reconciledDevice = playback
                ? AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                    new CycleDevice { Id = routine.OutputDeviceId, Name = routine.OutputDeviceName },
                    _outputDevices)
                : AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                    new CycleDevice { Id = routine.InputDeviceId, Name = routine.InputDeviceName },
                    _inputDevices);

            if (playback)
            {
                resolvedRoutine.OutputDeviceId = reconciledDevice.Id;
                resolvedRoutine.OutputDeviceName = reconciledDevice.Name;
            }
            else
            {
                resolvedRoutine.InputDeviceId = reconciledDevice.Id;
                resolvedRoutine.InputDeviceName = reconciledDevice.Name;
            }

            return resolvedRoutine;
        }

        private static string BuildRoutineSwitchExceptionDetail(string flow, Exception exception)
        {
            return $"{char.ToUpperInvariant(flow[0])}{flow[1..]} switch threw {exception.GetType().Name}.";
        }

        private BluetoothReconnectCoordinator GetRoutineBluetoothReconnectCoordinator()
        {
            _routineBluetoothReconnectCoordinator ??= new BluetoothReconnectCoordinator(new BluetoothReconnectService(), _logger);
            return _routineBluetoothReconnectCoordinator;
        }

        private BluetoothReconnectOptions GetRoutineBluetoothReconnectOptions()
        {
            Settings effectiveSettings = CurrentSettings ?? new Settings();
            return BluetoothReconnectOptions.FromSettings(effectiveSettings);
        }

        private async Task<RoutineReconnectOutcome> TryReconnectRoutineTargetAsync(
            string? deviceId,
            string? deviceName,
            BluetoothReconnectDeviceKind deviceKind,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            string opId,
            AudioRoutine routine,
            int? applicationProcessId,
            CancellationToken cancellationToken = default)
        {
            AppViewModelRoutineReconnectDecisionHelper.RoutineReconnectDecision decision =
                AppViewModelRoutineReconnectDecisionHelper.Evaluate(deviceId, deviceName, alreadyActive: false);

            if (!decision.ShouldAttempt && decision.SkipReason == "no-target-device-id")
            {
                _logger.Debug(
                    "AppViewModel",
                    () => AppViewModelRoutineReconnectLogHelper.BuildSkippedMessage(routine, deviceKind, opId, applicationProcessId, decision.SkipReason!));
                return default;
            }

            CycleDevice configuredDevice = decision.ConfiguredDevice!;

            decision = AppViewModelRoutineReconnectDecisionHelper.Evaluate(deviceId, deviceName, alreadyActive: tryResolveActiveDevice(configuredDevice) != null);
            if (!decision.ShouldAttempt)
            {
                _logger.Debug(
                    "AppViewModel",
                    () => AppViewModelRoutineReconnectLogHelper.BuildSkippedMessage(routine, deviceKind, opId, applicationProcessId, decision.SkipReason!));
                return default;
            }

            _logger.Debug(
                "AppViewModel",
                () => AppViewModelRoutineReconnectLogHelper.BuildStartedMessage(routine, deviceKind, opId, applicationProcessId));

            HashSet<string> activeDeviceIds = GetRoutineActiveDeviceIds(deviceKind);

            BluetoothReconnectAttemptResult reconnectResult = await GetRoutineBluetoothReconnectCoordinator().TryReconnectDetailedAsync(
                [decision.ConfiguredDevice!],
                activeDeviceIds,
                deviceKind,
                GetRoutineBluetoothReconnectOptions(),
                opId,
                cancellationToken: cancellationToken);

            _logger.Debug(
                "AppViewModel",
                () => AppViewModelRoutineReconnectLogHelper.BuildCompletedMessage(routine, deviceKind, opId, applicationProcessId, reconnectResult));

            await WaitForRoutineReconnectPostAttemptRecheckAsync(
                reconnectResult,
                RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs,
                cancellationToken);

            return new RoutineReconnectOutcome(reconnectResult.Attempted, reconnectResult.Connected);
        }

        internal static Task WaitForRoutineReconnectPostAttemptRecheckAsync(BluetoothReconnectAttemptResult reconnectResult, int delayMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return reconnectResult.Attempted
                ? RoutineReconnectPostAttemptDelayAsyncForTests(delayMs, cancellationToken)
                : Task.CompletedTask;
        }

        private HashSet<string> GetRoutineActiveDeviceIds(BluetoothReconnectDeviceKind deviceKind)
        {
            List<CycleDevice> activeDevices = deviceKind == BluetoothReconnectDeviceKind.Input
                ? _audio.GetActiveCaptureCycleEntries()
                : _audio.GetActivePlaybackCycleEntries();

            return BuildRoutineActiveDeviceIds(activeDevices);
        }

        internal static HashSet<string> BuildRoutineActiveDeviceIds(IEnumerable<CycleDevice>? activeDevices)
        {
            HashSet<string> activeDeviceIds = new(StringComparer.OrdinalIgnoreCase);

            if (activeDevices == null)
            {
                return activeDeviceIds;
            }

            foreach (CycleDevice? activeDevice in activeDevices)
            {
                if (activeDevice == null || string.IsNullOrWhiteSpace(activeDevice.Id))
                {
                    continue;
                }

                activeDeviceIds.Add(activeDevice.Id);
            }

            return activeDeviceIds;
        }

        private static string BuildRoutineProcessSwitchStartedMessage(AudioRoutine routine, string flow, string opId, int applicationProcessId)
        {
            return $"routine-{flow}-switch-started | {BuildRoutineDeviceSwitchLogContext(routine, flow, opId, applicationProcessId, perAppRouting: true)}";
        }

        private static string BuildRoutineProcessSwitchCompletedMessage(AudioRoutine routine, string flow, string opId, int applicationProcessId, ProcessAudioDeviceSwitchResult result)
        {
            return $"routine-{flow}-switch-completed | {BuildRoutineDeviceSwitchLogContext(routine, flow, opId, applicationProcessId, perAppRouting: true)} success={result.Success} routingResult={result.Result} applied={result.Result == ProcessAudioRoutingResult.Applied} awaitingAudio={result.Result == ProcessAudioRoutingResult.DeferredNoAudio} deviceName={FormatRoutineLogDevice(result.DeviceName)}";
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
            return $"success={result.Success} skipped={result.Skipped} awaitingAppCompletion={result.AwaitingAppCompletion} appOutputApplied={result.AppOutputApplied} appInputApplied={result.AppInputApplied} outputSucceeded={FormatRoutineLogBool(result.OutputSucceeded)} inputSucceeded={FormatRoutineLogBool(result.InputSucceeded)} masterVolumeSucceeded={FormatRoutineLogBool(result.MasterVolumeSucceeded)} micVolumeSucceeded={FormatRoutineLogBool(result.MicVolumeSucceeded)} outputReconnectAttempted={result.OutputReconnectAttempted} outputReconnectSucceeded={result.OutputReconnectSucceeded} inputReconnectAttempted={result.InputReconnectAttempted} inputReconnectSucceeded={result.InputReconnectSucceeded} outputDevice={FormatRoutineLogDevice(result.OutputDeviceName)} inputDevice={FormatRoutineLogDevice(result.InputDeviceName)} outputFailureDetail={NormalizeRoutineLogValue(result.OutputFailureDetail)} inputFailureDetail={NormalizeRoutineLogValue(result.InputFailureDetail)} elapsedMs={elapsedMs}";
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

        internal static string BuildRoutineDeviceSwitchLogContext(AudioRoutine routine, string flow, string opId, int? applicationProcessId, bool perAppRouting)
        {
            ArgumentNullException.ThrowIfNull(routine);

            string targetDeviceId = string.Equals(flow, "input", StringComparison.OrdinalIgnoreCase)
                ? routine.InputDeviceId
                : routine.OutputDeviceId;
            string targetDeviceName = string.Equals(flow, "input", StringComparison.OrdinalIgnoreCase)
                ? routine.InputDeviceName
                : routine.OutputDeviceName;
            string applicationProcessValue = FormatRoutineLogProcessId(applicationProcessId);

            return $"routineId={FormatRoutineLogIdentifier(routine.Id)} routineName={FormatRoutineLogLabel(routine.Name)} flow={NormalizeRoutineLogValue(flow)} opId={NormalizeRoutineLogValue(opId)} applicationProcessId={applicationProcessValue} perAppRouting={perAppRouting} targetDeviceId={FormatRoutineLogIdentifier(targetDeviceId)} targetDevice={FormatRoutineLogDevice(targetDeviceName)}";
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
