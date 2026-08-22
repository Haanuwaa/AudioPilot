using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal readonly record struct DeferredAutoSwitchCallbacks(
        Func<List<CycleDevice>> GetActiveDevices,
        Func<CycleDevice?> GetCurrentDevice,
        Func<string, CycleDevice, string, ValueTask<(bool Success, string? DeviceName)>> SwitchTargetAsync,
        Func<string, string, (bool Confirmed, string ResolvedDeviceName)> ConfirmCurrentDefaultTarget,
        Func<bool, string?, bool> IsSwitchSuccess,
        Action<string, string> OnAlreadyActive,
        Action<string, string> OnSwitchSuccess,
        Action<string, string> OnDefaultConfirmed,
        Action<string, string> OnServiceFailure,
        Action<string, string> OnTimeoutConfirmed,
        Action<string> OnTimeoutFailure);

    internal sealed class AppSwitchDeferredAutoSwitchCoordinator(
        AudioDeviceService audio,
        OverlayService overlay,
        Logger logger,
        Action<bool, int> suppressConnectedHotplugOverlay)
    {
        private readonly AudioDeviceService _audio = audio;
        private readonly OverlayService _overlay = overlay;
        private readonly Logger _logger = logger;
        private readonly Action<bool, int> _suppressConnectedHotplugOverlay = suppressConnectedHotplugOverlay;
        private int _deferredOutputSwitchInFlight;
        private int _deferredInputSwitchInFlight;

        public void TryScheduleOutputAutoSwitch(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName,
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            Action<string> schedulePostSwitchRefresh,
            Action showOutputSwitchFailureWarning,
            Func<string, string, (bool Confirmed, string ResolvedDeviceName)> confirmCurrentDefaultTarget,
            Func<bool> isLifetimeCancellationRequested,
            Func<CancellationToken, CancellationTokenSource> createLinkedCancellationSource,
            CancellationToken operationCancellationToken)
        {
            TryScheduleDeferredAutoSwitch(
                ref _deferredOutputSwitchInFlight,
                opId,
                kind: "output",
                skipEventName: AppConstants.Audio.LogEvents.OutputSwitch.Skip,
                alreadyRunningReason: "deferred-output-already-running",
                configuredCycle,
                cycleSnapshot => RunDeferredAutoSwitchBackgroundAsync(
                    opId,
                    AppConstants.Audio.LogEvents.OutputSwitch.Failed,
                    AppConstants.Audio.LogEvents.OutputSwitch.Skip,
                    nameof(TryScheduleOutputAutoSwitch),
                    () => RunDeferredAutoSwitchAsync(
                        opId,
                        cycleSnapshot,
                        pendingDeviceId,
                        pendingDeviceName,
                        AppConstants.Audio.LogEvents.OutputSwitch.Success,
                        AppConstants.Audio.LogEvents.OutputSwitch.Failed,
                        CreateDeferredOutputAutoSwitchCallbacks(
                            muteMic,
                            muteSound,
                            deafen,
                            preserveAudioLevels,
                            schedulePostSwitchRefresh,
                            showOutputSwitchFailureWarning,
                            confirmCurrentDefaultTarget),
                        isLifetimeCancellationRequested,
                        createLinkedCancellationSource,
                        operationCancellationToken),
                    () => Interlocked.Exchange(ref _deferredOutputSwitchInFlight, 0),
                    operationCancellationToken),
                operationCancellationToken);
        }

        public void TryScheduleInputAutoSwitch(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName,
            Func<string, string, (bool Confirmed, string ResolvedDeviceName)> confirmCurrentDefaultTarget,
            Func<bool> isLifetimeCancellationRequested,
            Func<CancellationToken, CancellationTokenSource> createLinkedCancellationSource)
        {
            TryScheduleDeferredAutoSwitch(
                ref _deferredInputSwitchInFlight,
                opId,
                kind: "input",
                skipEventName: AppConstants.Audio.LogEvents.InputSwitch.Skip,
                alreadyRunningReason: "deferred-input-already-running",
                configuredCycle,
                cycleSnapshot => RunDeferredAutoSwitchBackgroundAsync(
                    opId,
                    AppConstants.Audio.LogEvents.InputSwitch.Failed,
                    AppConstants.Audio.LogEvents.InputSwitch.Skip,
                    nameof(TryScheduleInputAutoSwitch),
                    () => RunDeferredAutoSwitchAsync(
                        opId,
                        cycleSnapshot,
                        pendingDeviceId,
                        pendingDeviceName,
                        AppConstants.Audio.LogEvents.InputSwitch.Success,
                        AppConstants.Audio.LogEvents.InputSwitch.Failed,
                        CreateDeferredInputAutoSwitchCallbacks(confirmCurrentDefaultTarget),
                        isLifetimeCancellationRequested,
                        createLinkedCancellationSource,
                        CancellationToken.None),
                    () => Interlocked.Exchange(ref _deferredInputSwitchInFlight, 0),
                    CancellationToken.None),
                CancellationToken.None);
        }

        internal static bool ShouldContinueDeferredLoop(DateTime nowUtc, DateTime deadlineUtc, bool isLifetimeCancellationRequested)
        {
            return !isLifetimeCancellationRequested && nowUtc < deadlineUtc;
        }

        internal static bool ShouldEmitDeferredTimeoutNotification(DateTime nowUtc, DateTime deadlineUtc, bool isLifetimeCancellationRequested)
        {
            return !isLifetimeCancellationRequested && nowUtc >= deadlineUtc;
        }

        private static List<CycleDevice> CloneCycleSnapshot(IReadOnlyList<CycleDevice> configuredCycle)
        {
            List<CycleDevice> clone = new(configuredCycle.Count);
            for (int index = 0; index < configuredCycle.Count; index++)
            {
                CycleDevice device = configuredCycle[index];
                clone.Add(new CycleDevice
                {
                    Id = device.Id,
                    Name = device.Name,
                });
            }

            return clone;
        }

        private bool TryBeginBackgroundSwitchOperation(ref int inFlightFlag, string category, string skipEventName, string opId, string reason)
        {
            if (Interlocked.CompareExchange(ref inFlightFlag, 1, 0) == 0)
            {
                return true;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(category, () => $"{skipEventName} | opId={opId} reason={reason}");
            }

            return false;
        }

        private void LogDeferredAutoSwitchStart(string opId, string kind)
        {
            _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=deferred-auto-switch windowMs={RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs}");
        }

        private void TryScheduleDeferredAutoSwitch(
            ref int inFlightFlag,
            string opId,
            string kind,
            string skipEventName,
            string alreadyRunningReason,
            IReadOnlyList<CycleDevice> configuredCycle,
            Func<IReadOnlyList<CycleDevice>, Task> runBackgroundAsync,
            CancellationToken operationCancellationToken)
        {
            if (operationCancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!TryBeginBackgroundSwitchOperation(ref inFlightFlag, "BluetoothReconnect", skipEventName, opId, alreadyRunningReason))
            {
                return;
            }

            List<CycleDevice> cycleSnapshot = CloneCycleSnapshot(configuredCycle);
            LogDeferredAutoSwitchStart(opId, kind);
            _ = runBackgroundAsync(cycleSnapshot);
        }

        private DeferredAutoSwitchCallbacks CreateDeferredOutputAutoSwitchCallbacks(
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            Action<string> schedulePostSwitchRefresh,
            Action showOutputSwitchFailureWarning,
            Func<string, string, (bool Confirmed, string ResolvedDeviceName)> confirmCurrentDefaultTarget)
        {
            return new DeferredAutoSwitchCallbacks(
                GetActiveDevices: _audio.GetActivePlaybackCycleEntries,
                GetCurrentDevice: () => GetCurrentDeviceSnapshot(_audio.GetDefaultPlaybackDevice),
                SwitchTargetAsync: (currentId, targetDevice, deferredOpId) => _audio.SwitchAudioDeviceAsync(currentId, targetDevice.Id, muteMic, muteSound, deafen, preserveAudioLevels, opId: deferredOpId),
                ConfirmCurrentDefaultTarget: confirmCurrentDefaultTarget,
                IsSwitchSuccess: static (success, deviceName) => success && !string.IsNullOrWhiteSpace(deviceName),
                OnAlreadyActive: (deferredOpId, deviceName) => NotifyDeferredOutputSwitchSuccess(deferredOpId, deviceName, schedulePostSwitchRefresh),
                OnSwitchSuccess: (deferredOpId, deviceName) => NotifyDeferredOutputSwitchSuccess(deferredOpId, deviceName, schedulePostSwitchRefresh),
                OnDefaultConfirmed: (deferredOpId, deviceName) => NotifyDeferredOutputSwitchSuccess(deferredOpId, deviceName, schedulePostSwitchRefresh),
                OnServiceFailure: (_, deviceName) => ShowDeferredOutputSwitchFailure(deviceName, showOutputSwitchFailureWarning),
                OnTimeoutConfirmed: (timeoutOpId, deviceName) => NotifyDeferredOutputSwitchSuccess(timeoutOpId, deviceName, schedulePostSwitchRefresh),
                OnTimeoutFailure: deviceName => ShowDeferredOutputReconnectFailure(deviceName, showOutputSwitchFailureWarning));
        }

        private DeferredAutoSwitchCallbacks CreateDeferredInputAutoSwitchCallbacks(
            Func<string, string, (bool Confirmed, string ResolvedDeviceName)> confirmCurrentDefaultTarget)
        {
            return new DeferredAutoSwitchCallbacks(
                GetActiveDevices: _audio.GetActiveCaptureCycleEntries,
                GetCurrentDevice: () => GetCurrentDeviceSnapshot(_audio.GetDefaultRecordingDevice),
                SwitchTargetAsync: (currentId, targetDevice, deferredOpId) => _audio.SwitchInputDeviceToAsync(
                    targetDevice.Id,
                    targetDevice.Name,
                    true,
                    showOverlay: null,
                    deferredOpId),
                ConfirmCurrentDefaultTarget: confirmCurrentDefaultTarget,
                IsSwitchSuccess: static (success, _) => success,
                OnAlreadyActive: static (_, _) => { },
                OnSwitchSuccess: static (_, _) => { },
                OnDefaultConfirmed: (_, deviceName) => NotifyDeferredInputConfirmation(deviceName),
                OnServiceFailure: (_, deviceName) => ShowDeferredInputSwitchFailure(deviceName),
                OnTimeoutConfirmed: (_, deviceName) => NotifyDeferredInputConfirmation(deviceName),
                OnTimeoutFailure: deviceName => ShowDeferredInputReconnectFailure(deviceName));
        }

        private void NotifyDeferredOutputSwitchSuccess(
            string opId,
            string deviceName,
            Action<string> schedulePostSwitchRefresh)
        {
            _suppressConnectedHotplugOverlay(true, RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs);
            _overlay.Show(OverlayDeviceKind.Output, "Switched output device", deviceName);
            schedulePostSwitchRefresh(opId);
        }

        private void ShowDeferredOutputReconnectFailure(string deviceName, Action showOutputSwitchFailureWarning)
        {
            _overlay.Show(OverlayDeviceKind.Error, "Failed to reconnect output device", deviceName);
            showOutputSwitchFailureWarning();
        }

        private void ShowDeferredOutputSwitchFailure(string deviceName, Action showOutputSwitchFailureWarning)
        {
            _overlay.Show(OverlayDeviceKind.Error, "Failed to switch output device", deviceName);
            showOutputSwitchFailureWarning();
        }

        private void NotifyDeferredInputConfirmation(string deviceName)
        {
            _overlay.Show(OverlayDeviceKind.Input, "Switched input device", deviceName);
        }

        private void ShowDeferredInputSwitchFailure(string deviceName)
        {
            _overlay.Show(OverlayDeviceKind.Error, "Failed to switch input device", deviceName);
        }

        private void ShowDeferredInputReconnectFailure(string deviceName)
        {
            _overlay.Show(OverlayDeviceKind.Error, "Failed to reconnect input device", deviceName);
        }

        private async Task RunDeferredAutoSwitchBackgroundAsync(
            string opId,
            string failedEventName,
            string skipEventName,
            string callerName,
            Func<Task> runDeferredSwitchAsync,
            Action releaseInFlight,
            CancellationToken operationCancellationToken)
        {
            try
            {
                await runDeferredSwitchAsync();
            }
            catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested)
            {
                _logger.Info("BluetoothReconnect", () => $"{skipEventName} | opId={opId} reason=deferred-auto-switch-superseded");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("BluetoothReconnect", () => $"{failedEventName} | opId={opId} reason=deferred-auto-switch-exception", callerName, ex);
            }
            finally
            {
                releaseInFlight();
            }
        }

        private async Task RunDeferredAutoSwitchAsync(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName,
            string successEventName,
            string failedEventName,
            DeferredAutoSwitchCallbacks callbacks,
            Func<bool> isLifetimeCancellationRequested,
            Func<CancellationToken, CancellationTokenSource> createLinkedCancellationSource,
            CancellationToken operationCancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs);

            while (ShouldContinueDeferredLoop(DateTime.UtcNow, deadlineUtc, isLifetimeCancellationRequested()) && !operationCancellationToken.IsCancellationRequested)
            {
                operationCancellationToken.ThrowIfCancellationRequested();
                List<CycleDevice> activeDevices = callbacks.GetActiveDevices();
                SwitchCycleSnapshotState cycleState = AppSwitchCycleStateResolver.BuildCycleSnapshotState(configuredCycle, activeDevices);
                if (AppSwitchCycleStateResolver.TryResolveDeferredPendingTarget(pendingDeviceId, pendingDeviceName, cycleState.ConnectedCycle, out CycleDevice targetDevice))
                {
                    operationCancellationToken.ThrowIfCancellationRequested();
                    CycleDevice? currentDevice = callbacks.GetCurrentDevice();
                    if (currentDevice == null)
                    {
                        using CancellationTokenSource linkedCts = createLinkedCancellationSource(operationCancellationToken);
                        await WaitForDeviceStateSignalOrDelayAsync(linkedCts.Token);
                        continue;
                    }

                    string currentId = currentDevice.Id;
                    string deferredOpId = $"{opId}-defer";
                    if (targetDevice.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase))
                    {
                        callbacks.OnAlreadyActive(deferredOpId, targetDevice.Name);
                        _logger.Info("BluetoothReconnect", () => $"{successEventName} | opId={deferredOpId} reason=deferred-auto-switch-already-active target={LogPrivacy.Device(targetDevice.Name)}");
                        return;
                    }

                    var (success, deviceName) = await callbacks.SwitchTargetAsync(currentId, targetDevice, deferredOpId);
                    if (callbacks.IsSwitchSuccess(success, deviceName))
                    {
                        string switchDeviceName = string.IsNullOrWhiteSpace(deviceName) ? targetDevice.Name : deviceName;
                        callbacks.OnSwitchSuccess(deferredOpId, switchDeviceName);
                        _logger.Info("BluetoothReconnect", () => $"{successEventName} | opId={deferredOpId} reason=deferred-auto-switch target={LogPrivacy.Device(switchDeviceName)}");
                    }
                    else
                    {
                        var (confirmed, confirmedDeviceName) = callbacks.ConfirmCurrentDefaultTarget(pendingDeviceId, pendingDeviceName);
                        if (confirmed)
                        {
                            callbacks.OnDefaultConfirmed(deferredOpId, confirmedDeviceName);
                            _logger.Info("BluetoothReconnect", () => $"{successEventName} | opId={deferredOpId} reason=deferred-default-confirmed target={LogPrivacy.Device(confirmedDeviceName)}");
                        }
                        else
                        {
                            callbacks.OnServiceFailure(deferredOpId, pendingDeviceName);
                            _logger.Warning("BluetoothReconnect", () => $"{failedEventName} | opId={deferredOpId} reason=deferred-service-failure");
                        }
                    }

                    return;
                }

                using CancellationTokenSource waitLinkedCts = createLinkedCancellationSource(operationCancellationToken);
                await WaitForDeviceStateSignalOrDelayAsync(waitLinkedCts.Token);
            }

            if (operationCancellationToken.IsCancellationRequested || !ShouldEmitDeferredTimeoutNotification(DateTime.UtcNow, deadlineUtc, isLifetimeCancellationRequested()))
            {
                return;
            }

            var (timeoutConfirmed, resolvedDeviceName) = callbacks.ConfirmCurrentDefaultTarget(pendingDeviceId, pendingDeviceName);
            if (timeoutConfirmed)
            {
                callbacks.OnTimeoutConfirmed(opId, resolvedDeviceName);
                _logger.Info("BluetoothReconnect", () => $"{successEventName} | opId={opId} reason=deferred-timeout-default-confirmed target={LogPrivacy.Device(resolvedDeviceName)}");
                return;
            }

            callbacks.OnTimeoutFailure(pendingDeviceName);
            _logger.Warning("BluetoothReconnect", () => $"{failedEventName} | opId={opId} reason=deferred-auto-switch-timeout windowMs={RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs}");
        }

        private async Task WaitForDeviceStateSignalOrDelayAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> stateChangedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDeviceStateChanged() => stateChangedSignal.TrySetResult(true);
            _audio.DeviceStateChanged += OnDeviceStateChanged;

            try
            {
                Task intervalDelayTask = Task.Delay(RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs, cancellationToken);
                await Task.WhenAny(stateChangedSignal.Task, intervalDelayTask);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                _audio.DeviceStateChanged -= OnDeviceStateChanged;
            }
        }

        private static CycleDevice? GetCurrentDeviceSnapshot(Func<MMDevice?> getCurrentDevice)
        {
            using MMDevice? device = getCurrentDevice();
            if (device == null)
            {
                return null;
            }

            return AppSwitchCycleStateResolver.CreateCycleDevice(device.ID, device.FriendlyName);
        }
    }
}
