using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppSwitchCommandCoordinator
    {
        private void TryQueueCoalescedRetry(
            Func<bool> tryBeginRetry,
            string skipEventName,
            string opId,
            AppSwitchRequestRejectionReason rejectionReason,
            IReadOnlyList<CycleDevice> configuredCycle,
            long lastRequestTicks,
            int debounceMs,
            Func<int, IReadOnlyList<CycleDevice>, Task> runBackgroundAsync)
        {
            _retryCoordinator.TryQueueCoalescedRetry(
                tryBeginRetry,
                skipEventName,
                opId,
                rejectionReason,
                configuredCycle,
                lastRequestTicks,
                debounceMs,
                CloneCycleSnapshot,
                runBackgroundAsync);
        }

        private async Task RunCoalescedRetryBackgroundAsync(
            int retryDelayMs,
            Func<Task> runSwitchAsync,
            Action endRetry,
            string skipEventName,
            string opId,
            CancellationToken operationCancellationToken)
        {
            await _retryCoordinator.RunContainedCoalescedRetryBackgroundAsync(
                retryDelayMs,
                runSwitchAsync,
                endRetry,
                GetLifetimeCancellationToken,
                CreateLinkedCancellationSource,
                skipEventName,
                opId,
                operationCancellationToken);
        }

        private void TryScheduleDeferredOutputAutoSwitch(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName,
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            Action<string> schedulePostSwitchRefresh,
            Action showOutputSwitchFailureWarning)
        {
            _deferredAutoSwitchCoordinator.TryScheduleOutputAutoSwitch(
                opId,
                configuredCycle,
                pendingDeviceId,
                pendingDeviceName,
                muteMic,
                muteSound,
                deafen,
                preserveAudioLevels,
                schedulePostSwitchRefresh,
                showOutputSwitchFailureWarning,
                (deviceId, deviceName) =>
                {
                    bool confirmed = TryConfirmCurrentDefaultPlaybackTarget(deviceId, deviceName, out string resolvedDeviceName);
                    return (confirmed, resolvedDeviceName);
                },
                IsLifetimeCancellationRequested,
                CreateLinkedCancellationSource,
                _outputIntentTracker.GetActiveToken());
        }

        private void TryScheduleDeferredInputAutoSwitch(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName)
        {
            _deferredAutoSwitchCoordinator.TryScheduleInputAutoSwitch(
                opId,
                configuredCycle,
                pendingDeviceId,
                pendingDeviceName,
                (deviceId, deviceName) =>
                {
                    bool confirmed = TryConfirmCurrentDefaultRecordingTarget(deviceId, deviceName, out string resolvedDeviceName);
                    return (confirmed, resolvedDeviceName);
                },
                IsLifetimeCancellationRequested,
                CreateLinkedCancellationSource);
        }

        private void TryQueueOutputCoalescedRetry(
            IReadOnlyList<CycleDevice> configuredCycle,
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            bool reverse,
            BluetoothReconnectOptions reconnectOptions,
            Action<string> schedulePostSwitchRefresh,
            Action showOutputCycleMissingWarning,
            Action showOutputSwitchFailureWarning,
            AppSwitchRequestRejectionReason rejectionReason,
            string opId)
        {
            TryQueueCoalescedRetry(
                _requestCoordinator.TryBeginOutputCoalescedRetry,
                AppConstants.Audio.LogEvents.OutputSwitch.Skip,
                opId,
                rejectionReason,
                configuredCycle,
                _requestCoordinator.GetLastOutputSwitchRequestTicks(),
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                (retryDelayMs, cycleSnapshot) => RunCoalescedRetryBackgroundAsync(
                    retryDelayMs,
                    () => SwitchOutputAsync(
                        cycleSnapshot,
                        muteMic,
                        muteSound,
                        deafen,
                        preserveAudioLevels,
                        reverse,
                        reconnectOptions,
                        schedulePostSwitchRefresh,
                        showOutputCycleMissingWarning,
                        showOutputSwitchFailureWarning).AsTask(),
                    _requestCoordinator.EndOutputCoalescedRetry,
                    AppConstants.Audio.LogEvents.OutputSwitch.Skip,
                    opId,
                    _outputIntentTracker.GetActiveToken()));
        }

        private void TryQueueInputCoalescedRetry(
            IReadOnlyList<CycleDevice> configuredCycle,
            bool reverse,
            BluetoothReconnectOptions reconnectOptions,
            AppSwitchRequestRejectionReason rejectionReason,
            string opId)
        {
            TryQueueCoalescedRetry(
                _requestCoordinator.TryBeginInputCoalescedRetry,
                AppConstants.Audio.LogEvents.InputSwitch.Skip,
                opId,
                rejectionReason,
                configuredCycle,
                _requestCoordinator.GetLastInputSwitchRequestTicks(),
                RuntimeTuningConfig.InputSwitchDebounceMs,
                (retryDelayMs, cycleSnapshot) => RunCoalescedRetryBackgroundAsync(
                    retryDelayMs,
                    () => SwitchInputAsync(cycleSnapshot, reverse, true, reconnectOptions).AsTask(),
                    _requestCoordinator.EndInputCoalescedRetry,
                    AppConstants.Audio.LogEvents.InputSwitch.Skip,
                    opId,
                    CancellationToken.None));
        }

        private static List<CycleDevice> CloneCycleSnapshot(IReadOnlyList<CycleDevice> configuredCycle)
        {
            return AppViewModelDeviceCycleHelper.CloneCycleDevices(configuredCycle);
        }

        private CancellationTokenSource CreateLinkedCancellationSource(CancellationToken operationCancellationToken)
        {
            CancellationToken lifetimeToken = GetLifetimeCancellationToken();
            if (!operationCancellationToken.CanBeCanceled)
            {
                return CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            }

            return CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, operationCancellationToken);
        }

        private static void ThrowIfOperationCanceled(Func<bool> isOperationCurrent, CancellationToken operationCancellationToken)
        {
            operationCancellationToken.ThrowIfCancellationRequested();
            if (!isOperationCurrent())
            {
                throw new OperationCanceledException(operationCancellationToken);
            }
        }

        private bool TryHandleSingleConnectedCycle(
            string opId,
            string skipEventName,
            string failedEventName,
            string successEventName,
            OverlayDeviceKind successOverlayKind,
            string successOverlayTitle,
            string failureOverlayTitle,
            List<MMDevice> activeDevices,
            List<CycleDevice> connectedCycle,
            List<CycleDevice> skippedDevices,
            bool reconnectAttempted,
            bool reconnectSucceeded,
            Action<string> onReconnectPendingFailure,
            Action<string, string> scheduleDeferredAutoSwitch,
            Func<string, string, (bool Confirmed, string ResolvedDeviceName)> tryConfirmCurrentDefaultTarget,
            Action onConfirmedSuccess,
            out bool success)
        {
            success = false;

            if (connectedCycle.Count != 1)
            {
                return false;
            }

            if (skippedDevices.Count > 0
                && AppSwitchCycleStateResolver.TryResolveReconnectedCycleDeviceByName(activeDevices, connectedCycle, skippedDevices, out CycleDevice remappedDevice, out string matchReason, out string configuredName))
            {
                connectedCycle.Add(remappedDevice);
                _logger.Info(
                    "AppViewModel",
                    () => $"{skipEventName} | opId={opId} reason=probe-remapped-by-name configured={LogPrivacy.Device(configuredName)} matched={LogPrivacy.Device(remappedDevice.Name)} match={matchReason}");
            }

            SingleConnectedCycleDecision decision = AppSwitchCycleStateResolver.ResolveSingleConnectedCycleDecision(
                connectedCycle.Count,
                skippedDevices.Count,
                reconnectAttempted,
                reconnectSucceeded);

            if (decision == SingleConnectedCycleDecision.ContinueSwitch)
            {
                return false;
            }

            if (decision == SingleConnectedCycleDecision.FailNoAlternateConnected)
            {
                _logger.Warning("AppViewModel", () => $"{failedEventName} | opId={opId} reason=no-alternate-connected");
                string failedDeviceName = skippedDevices.FirstOrDefault()?.Name ?? connectedCycle[0].Name;
                _overlay.Show(OverlayDeviceKind.Error, failureOverlayTitle, failedDeviceName);
                return true;
            }

            CycleDevice pendingTarget = skippedDevices[0];
            if (decision == SingleConnectedCycleDecision.AwaitReconnectObservation)
            {
                var (confirmed, resolvedDeviceName) = tryConfirmCurrentDefaultTarget(pendingTarget.Id, pendingTarget.Name);
                if (confirmed)
                {
                    _overlay.Show(successOverlayKind, successOverlayTitle, resolvedDeviceName);
                    onConfirmedSuccess();
                    _logger.Info("AppViewModel", () => $"{successEventName} | opId={opId} reason=reconnect-success-default-confirmed target={LogPrivacy.Device(resolvedDeviceName)}");
                    success = true;
                    return true;
                }

                _logger.Info("AppViewModel", () => $"{skipEventName} | opId={opId} reason=reconnect-success-awaiting-observation target={LogPrivacy.Device(pendingTarget.Name)} action=deferred-auto-switch");
                scheduleDeferredAutoSwitch(pendingTarget.Id, pendingTarget.Name);
                return true;
            }

            onReconnectPendingFailure(pendingTarget.Name);
            _logger.Warning("AppViewModel", () => $"{failedEventName} | opId={opId} reason=reconnect-pending target={LogPrivacy.Device(pendingTarget.Name)}");
            return true;
        }

        private void DisposeDevices(IEnumerable<MMDevice> devices)
        {
            foreach (MMDevice device in devices)
            {
                try
                {
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AppViewModel", () => $"Ignored dispose exception for device refresh: {ex.GetType().Name}");
                    }
                }
            }
        }

        private List<MMDevice> MaterializeDevices(MMDeviceCollection collection)
        {
            using (collection)
            {
                return AudioDeviceCollectionHelper.MaterializeDevices(collection, (index, ex) =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.Diagnostics.SwitchSpamGuardDiagnostics} | reason=device-materialize-failed index={index} exceptionType={ex.GetType().Name}");
                    }
                });
            }
        }
    }
}
