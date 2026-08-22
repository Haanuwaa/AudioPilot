using AudioPilot.Constants;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppSwitchCommandCoordinator
    {
        public async ValueTask<bool> SwitchInputAsync(
            IReadOnlyList<CycleDevice> configuredCycle,
            bool reverse,
            bool preserveAudioLevels,
            BluetoothReconnectOptions reconnectOptions)
        {
            string opId = Guid.NewGuid().ToString("N")[..8];

            if (configuredCycle.Count == 0)
            {
                _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={opId} reason=empty-cycle");
                return false;
            }

            if (!_requestCoordinator.TryBeginInputSwitchRequest(opId, out AppSwitchRequestRejectionReason rejectionReason))
            {
                if (rejectionReason is AppSwitchRequestRejectionReason.InProgress or AppSwitchRequestRejectionReason.Debounced)
                {
                    TryQueueInputCoalescedRetry(configuredCycle, reverse, reconnectOptions, rejectionReason, opId);
                }

                return false;
            }

            (int intentVersion, CancellationToken operationCancellationToken) = _inputIntentTracker.Begin();

            try
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Start} | opId={opId} reverse={reverse} preserveAudioLevels={preserveAudioLevels}");
                return await ExecuteSwitchCoreAsync(
                    opId,
                    configuredCycle,
                    reverse,
                    reconnectOptions,
                    () => _inputIntentTracker.IsCurrent(intentVersion),
                    CreateInputSwitchExecutionCallbacks(preserveAudioLevels),
                    operationCancellationToken);
            }
            finally
            {
                _requestCoordinator.EndInputSwitchRequest();
            }
        }

        private SwitchExecutionCallbacks CreateInputSwitchExecutionCallbacks(bool preserveAudioLevels)
        {
            return new SwitchExecutionCallbacks(
                GetCurrentDevice: _audio.GetDefaultRecordingDevice,
                TryResolveActiveCycleEntry: configured => _audio.TryGetActiveRecordingCycleEntry(configured.Id, configured.Name),
                GetActiveDevices: () => MaterializeDevices(_audio.GetActiveCaptureDevices()),
                SwitchDirectAsync: (_, targetDevice, switchOpId) => _audio.SwitchInputDeviceToAsync(
                    targetDevice.Id,
                    targetDevice.Name,
                    preserveAudioLevels,
                    (kind, title, message) => _overlay.Show(kind, title, message),
                    switchOpId),
                SwitchFinalAsync: (_, targetDevice, switchOpId) => _audio.SwitchInputDeviceToAsync(
                    targetDevice.Id,
                    targetDevice.Name,
                    preserveAudioLevels,
                    (kind, title, message) => _overlay.Show(kind, title, message),
                    switchOpId),
                RecheckAfterReconnectAttemptAsync: (switchOpId, cycleSnapshot, activeDevices) => RecheckInputDevicesAfterReconnectAttemptAsync(switchOpId, cycleSnapshot, activeDevices),
                RecheckAfterReconnectSuccessAsync: (switchOpId, cycleSnapshot, pendingDeviceId, pendingDeviceName, activeDevices) => RecheckInputDevicesAfterReconnectSuccessAsync(switchOpId, cycleSnapshot, pendingDeviceId, pendingDeviceName, activeDevices),
                IsSwitchSuccess: static (success, _) => success,
                ConfirmCurrentDefaultTarget: (deviceId, deviceName) =>
                {
                    bool confirmed = TryConfirmCurrentDefaultRecordingTarget(deviceId, deviceName, out string resolvedDeviceName);
                    return (confirmed, resolvedDeviceName);
                },
                ScheduleDeferredAutoSwitch: (switchOpId, cycleSnapshot, pendingDeviceId, pendingDeviceName) =>
                    TryScheduleDeferredInputAutoSwitch(switchOpId, cycleSnapshot, pendingDeviceId, pendingDeviceName),
                OnSwitchSuccess: static (_, _) => { },
                OnFinalServiceFailure: static _ => { },
                OnReconnectPendingFailure: deviceName => _overlay.Show(OverlayDeviceKind.Error, "Failed to reconnect input device", deviceName),
                OnNoConnectedDevices: _ => _overlay.Show(OverlayDeviceKind.Input, "No connected input devices", "Check your configured input cycle"),
                OnMissingCurrentDevice: switchOpId => _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Failed} | opId={switchOpId} reason=no-default-input"),
                OnConfirmedSingleConnectedSuccess: static _ => { },
                OnReconnectStarted: deviceName => _overlay.Show(OverlayDeviceKind.Input, "Reconnecting input device", deviceName),
                OnReconnectAttemptProgress: (attempt, maxAttempts, attemptDeviceName) => _overlay.Show(OverlayDeviceKind.Input, $"Reconnecting input device (attempt {attempt}/{maxAttempts})", attemptDeviceName),
                ReconnectKind: BluetoothReconnectDeviceKind.Input,
                OverlayDeviceKind: OverlayDeviceKind.Input,
                SuccessEventName: AppConstants.Audio.LogEvents.InputSwitch.Success,
                FailedEventName: AppConstants.Audio.LogEvents.InputSwitch.Failed,
                SkipEventName: AppConstants.Audio.LogEvents.InputSwitch.Skip,
                SkipDisconnectedEventName: AppConstants.Audio.LogEvents.InputSwitch.SkipDisconnected,
                PhasesEventName: AppConstants.Audio.LogEvents.InputSwitch.Phases,
                SuccessOverlayTitle: "Switched input device",
                FailureOverlayTitle: "Failed to switch input device",
                DisposeTraceDeviceKind: "input",
                SuppressConnectedOverlayForOutput: false);
        }

        private void ReplaceActiveCaptureDevices(ref List<MMDevice> activeDevices)
        {
            DisposeDevices(activeDevices);
            activeDevices = MaterializeDevices(_audio.GetActiveCaptureDevices());
        }

        private bool TryConfirmCurrentDefaultRecordingTarget(
            string pendingDeviceId,
            string pendingDeviceName,
            out string resolvedDeviceName)
        {
            resolvedDeviceName = pendingDeviceName;
            MMDevice? currentDevice = null;

            try
            {
                currentDevice = _audio.GetDefaultRecordingDevice();
                if (currentDevice == null)
                {
                    return false;
                }

                string currentDeviceName = AppSwitchCycleStateResolver.TryGetFriendlyName(currentDevice, pendingDeviceName);
                if (!AppSwitchReconnectRecoveryCoordinator.DoesCurrentDefaultMatchPendingTarget(currentDevice.ID, currentDeviceName, pendingDeviceId, pendingDeviceName))
                {
                    return false;
                }

                resolvedDeviceName = string.IsNullOrWhiteSpace(currentDeviceName) ? pendingDeviceName : currentDeviceName;
                return true;
            }
            finally
            {
                currentDevice?.Dispose();
            }
        }

        private async Task<List<MMDevice>> RecheckInputDevicesAfterReconnectAttemptAsync(string opId, IReadOnlyList<CycleDevice> configuredCycle, List<MMDevice> activeDevices)
        {
            return await _reconnectRecoveryCoordinator.RecheckDevicesAfterReconnectAttemptAsync(
                opId,
                kind: "input",
                configuredCycle,
                activeDevices,
                ReplaceActiveCaptureDevices,
                GetLifetimeCancellationToken);
        }

        private async Task<List<MMDevice>> RecheckInputDevicesAfterReconnectSuccessAsync(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName,
            List<MMDevice> activeDevices)
        {
            return await _reconnectRecoveryCoordinator.RecheckDevicesAfterReconnectSuccessAsync(
                opId,
                kind: "input",
                configuredCycle,
                pendingDeviceId,
                pendingDeviceName,
                activeDevices,
                ReplaceActiveCaptureDevices,
                (deviceId, deviceName) =>
                {
                    bool confirmed = TryConfirmCurrentDefaultRecordingTarget(deviceId, deviceName, out string resolvedDeviceName);
                    return (confirmed, resolvedDeviceName);
                },
                GetLifetimeCancellationToken,
                IsLifetimeCancellationRequested);
        }
    }
}
