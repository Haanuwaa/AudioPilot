using AudioPilot.Constants;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppSwitchCommandCoordinator
    {
        public async ValueTask<bool> SwitchOutputAsync(
            IReadOnlyList<CycleDevice> configuredCycle,
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            bool reverse,
            BluetoothReconnectOptions reconnectOptions,
            Action<string> schedulePostSwitchRefresh,
            Action showOutputCycleMissingWarning,
            Action showOutputSwitchFailureWarning)
        {
            string opId = Guid.NewGuid().ToString("N")[..8];

            if (configuredCycle.Count == 0)
            {
                _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={opId} reason=empty-cycle");
                showOutputCycleMissingWarning();
                return false;
            }

            if (!_requestCoordinator.TryBeginOutputSwitchRequest(opId, out AppSwitchRequestRejectionReason rejectionReason))
            {
                if (rejectionReason is AppSwitchRequestRejectionReason.InProgress or AppSwitchRequestRejectionReason.Debounced)
                {
                    if (rejectionReason == AppSwitchRequestRejectionReason.InProgress)
                    {
                        _outputIntentTracker.ShowActiveReconnectOverlayIfAvailable(_overlay);
                        if (_outputIntentTracker.DoesRequestedOutputTargetMatchActiveTarget(configuredCycle, reverse))
                        {
                            return false;
                        }
                    }

                    TryQueueOutputCoalescedRetry(
                        configuredCycle,
                        muteMic,
                        muteSound,
                        deafen,
                        preserveAudioLevels,
                        reverse,
                        reconnectOptions,
                        schedulePostSwitchRefresh,
                        showOutputCycleMissingWarning,
                        showOutputSwitchFailureWarning,
                        rejectionReason,
                        opId);
                }

                return false;
            }

            (int intentVersion, CancellationToken operationCancellationToken) = _outputIntentTracker.Begin();

            try
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Start} | opId={opId} muteMic={muteMic} muteSound={muteSound} deafen={deafen} preserveAudioLevels={preserveAudioLevels} reverse={reverse}");
                return await ExecuteSwitchCoreAsync(
                    opId,
                    configuredCycle,
                    reverse,
                    reconnectOptions,
                    () => _outputIntentTracker.IsCurrent(intentVersion),
                    CreateOutputSwitchExecutionCallbacks(
                        muteMic,
                        muteSound,
                        deafen,
                        preserveAudioLevels,
                        schedulePostSwitchRefresh,
                        showOutputSwitchFailureWarning),
                    operationCancellationToken);
            }
            finally
            {
                _requestCoordinator.EndOutputSwitchRequest();
            }
        }

        private SwitchExecutionCallbacks CreateOutputSwitchExecutionCallbacks(
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            Action<string> schedulePostSwitchRefresh,
            Action showOutputSwitchFailureWarning)
        {
            return new SwitchExecutionCallbacks(
                GetCurrentDevice: _audio.GetDefaultPlaybackDevice,
                TryResolveActiveCycleEntry: configured => _audio.TryGetActivePlaybackCycleEntry(configured.Id, configured.Name),
                GetActiveDevices: () => MaterializeDevices(_audio.GetActivePlaybackDevices()),
                SwitchDirectAsync: (currentDevice, targetDevice, switchOpId) => _audio.SwitchAudioDeviceAsync(currentDevice.ID, targetDevice.Id, muteMic, muteSound, deafen, preserveAudioLevels, opId: switchOpId),
                SwitchFinalAsync: (currentId, targetDevice, switchOpId) => _audio.SwitchAudioDeviceAsync(currentId, targetDevice.Id, muteMic, muteSound, deafen, preserveAudioLevels, opId: switchOpId),
                RecheckAfterReconnectAttemptAsync: (switchOpId, cycleSnapshot, activeDevices) => RecheckOutputDevicesAfterReconnectAttemptAsync(switchOpId, cycleSnapshot, activeDevices),
                RecheckAfterReconnectSuccessAsync: (switchOpId, cycleSnapshot, pendingDeviceId, pendingDeviceName, activeDevices) => RecheckOutputDevicesAfterReconnectSuccessAsync(switchOpId, cycleSnapshot, pendingDeviceId, pendingDeviceName, activeDevices),
                IsSwitchSuccess: static (success, deviceName) => success && !string.IsNullOrWhiteSpace(deviceName),
                ConfirmCurrentDefaultTarget: (deviceId, deviceName) =>
                {
                    bool confirmed = TryConfirmCurrentDefaultPlaybackTarget(deviceId, deviceName, out string resolvedDeviceName);
                    return (confirmed, resolvedDeviceName);
                },
                ScheduleDeferredAutoSwitch: (switchOpId, cycleSnapshot, pendingDeviceId, pendingDeviceName) =>
                    TryScheduleDeferredOutputAutoSwitch(
                        switchOpId,
                        cycleSnapshot,
                        pendingDeviceId,
                        pendingDeviceName,
                        muteMic,
                        muteSound,
                        deafen,
                        preserveAudioLevels,
                        schedulePostSwitchRefresh,
                        showOutputSwitchFailureWarning),
                OnSwitchSuccess: (switchOpId, deviceName) =>
                {
                    _overlay.Show(OverlayDeviceKind.Output, "Switched output device", deviceName);
                    schedulePostSwitchRefresh(switchOpId);
                },
                OnFinalServiceFailure: deviceName =>
                {
                    _overlay.Show(OverlayDeviceKind.Error, "Failed to switch output device", deviceName);
                    showOutputSwitchFailureWarning();
                },
                OnReconnectPendingFailure: deviceName =>
                {
                    _overlay.Show(OverlayDeviceKind.Error, "Failed to reconnect output device", deviceName);
                    showOutputSwitchFailureWarning();
                },
                OnNoConnectedDevices: switchOpId =>
                {
                    _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={switchOpId} reason=all-configured-disconnected");
                    _overlay.Show(OverlayDeviceKind.Output, "No connected output devices", "Check your configured output cycle");
                },
                OnMissingCurrentDevice: switchOpId =>
                {
                    _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.Failed} | opId={switchOpId} reason=no-default-output");
                },
                OnConfirmedSingleConnectedSuccess: schedulePostSwitchRefresh,
                OnReconnectStarted: deviceName => _overlay.Show(OverlayDeviceKind.Output, "Reconnecting output device", deviceName),
                OnReconnectAttemptProgress: (attempt, maxAttempts, attemptDeviceName) => _overlay.Show(OverlayDeviceKind.Output, $"Reconnecting output device (attempt {attempt}/{maxAttempts})", attemptDeviceName),
                ReconnectKind: BluetoothReconnectDeviceKind.Output,
                OverlayDeviceKind: OverlayDeviceKind.Output,
                SuccessEventName: AppConstants.Audio.LogEvents.OutputSwitch.Success,
                FailedEventName: AppConstants.Audio.LogEvents.OutputSwitch.Failed,
                SkipEventName: AppConstants.Audio.LogEvents.OutputSwitch.Skip,
                SkipDisconnectedEventName: AppConstants.Audio.LogEvents.OutputSwitch.SkipDisconnected,
                PhasesEventName: AppConstants.Audio.LogEvents.OutputSwitch.Phases,
                SuccessOverlayTitle: "Switched output device",
                FailureOverlayTitle: "Failed to switch output device",
                DisposeTraceDeviceKind: "output",
                SuppressConnectedOverlayForOutput: true);
        }

        private void ReplaceActivePlaybackDevices(ref List<MMDevice> activeDevices)
        {
            DisposeDevices(activeDevices);
            activeDevices = MaterializeDevices(_audio.GetActivePlaybackDevices());
        }

        private bool TryConfirmCurrentDefaultPlaybackTarget(
            string pendingDeviceId,
            string pendingDeviceName,
            out string resolvedDeviceName)
        {
            resolvedDeviceName = pendingDeviceName;
            MMDevice? currentDevice = null;

            try
            {
                currentDevice = _audio.GetDefaultPlaybackDevice();
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

        private async Task<List<MMDevice>> RecheckOutputDevicesAfterReconnectAttemptAsync(string opId, List<MMDevice> activeDevices)
        {
            return await _reconnectRecoveryCoordinator.RecheckDevicesAfterReconnectAttemptAsync(
                opId,
                kind: "output",
                configuredCycle: [],
                activeDevices,
                ReplaceActivePlaybackDevices,
                GetLifetimeCancellationToken);
        }

        private async Task<List<MMDevice>> RecheckOutputDevicesAfterReconnectAttemptAsync(string opId, IReadOnlyList<CycleDevice> configuredCycle, List<MMDevice> activeDevices)
        {
            return await _reconnectRecoveryCoordinator.RecheckDevicesAfterReconnectAttemptAsync(
                opId,
                kind: "output",
                configuredCycle,
                activeDevices,
                ReplaceActivePlaybackDevices,
                GetLifetimeCancellationToken);
        }

        private async Task<List<MMDevice>> RecheckOutputDevicesAfterReconnectSuccessAsync(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName,
            List<MMDevice> activeDevices)
        {
            return await _reconnectRecoveryCoordinator.RecheckDevicesAfterReconnectSuccessAsync(
                opId,
                kind: "output",
                configuredCycle,
                pendingDeviceId,
                pendingDeviceName,
                activeDevices,
                ReplaceActivePlaybackDevices,
                (deviceId, deviceName) =>
                {
                    bool confirmed = TryConfirmCurrentDefaultPlaybackTarget(deviceId, deviceName, out string resolvedDeviceName);
                    return (confirmed, resolvedDeviceName);
                },
                GetLifetimeCancellationToken,
                IsLifetimeCancellationRequested);
        }
    }
}
