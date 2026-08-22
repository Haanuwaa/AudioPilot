using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal delegate void AppSwitchDeviceListRefresher(ref List<MMDevice> activeDevices);

    internal sealed class AppSwitchReconnectRecoveryCoordinator(
        AudioDeviceService audio,
        Logger logger)
    {
        private readonly AudioDeviceService _audio = audio;
        private readonly Logger _logger = logger;

        internal static int ResolveAdaptiveSuccessRecheckIntervalMs(int elapsedMs)
        {
            if (elapsedMs < 1200)
            {
                return RuntimeTuningConfig.BluetoothReconnectSuccessRecheckInitialIntervalMs;
            }

            if (elapsedMs < 4000)
            {
                return RuntimeTuningConfig.BluetoothReconnectSuccessRecheckMidIntervalMs;
            }

            return RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs;
        }

        internal static int ResolveSuccessObservedRecheckIntervalMs(int elapsedMs, bool pendingObserved)
        {
            int adaptiveIntervalMs = ResolveAdaptiveSuccessRecheckIntervalMs(elapsedMs);
            if (!pendingObserved)
            {
                return adaptiveIntervalMs;
            }

            return Math.Min(adaptiveIntervalMs, RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs);
        }

        internal static bool ShouldApplyReconnectSuccessTimeoutGrace(int graceMs)
        {
            return graceMs > 0;
        }

        internal static bool DoesCurrentDefaultMatchPendingTarget(
            string? currentDeviceId,
            string? currentDeviceName,
            string? pendingDeviceId,
            string? pendingDeviceName)
        {
            if (!string.IsNullOrWhiteSpace(currentDeviceId)
                && !string.IsNullOrWhiteSpace(pendingDeviceId)
                && currentDeviceId.Equals(pendingDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(currentDeviceName) || string.IsNullOrWhiteSpace(pendingDeviceName))
            {
                return false;
            }

            string normalizedPendingName = BluetoothReconnectService.NormalizeForMatch(pendingDeviceName);
            if (string.IsNullOrWhiteSpace(normalizedPendingName))
            {
                return false;
            }

            string matchReason = BluetoothReconnectService.ResolveMatchReason(
                currentDeviceName,
                pendingDeviceName,
                normalizedPendingName);

            return !string.IsNullOrWhiteSpace(matchReason);
        }

        internal static bool IsReconnectSuccessPendingTargetSatisfied(
            IReadOnlyList<CycleDevice> connectedCycle,
            bool pendingIdSpecified,
            string pendingDeviceId,
            string pendingDeviceName,
            Func<string, string, (bool Confirmed, string ResolvedDeviceName)> confirmCurrentDefaultTarget,
            out bool defaultConfirmed)
        {
            defaultConfirmed = false;

            if (!pendingIdSpecified)
            {
                return true;
            }

            if (AppSwitchCycleStateResolver.TryResolveDeferredPendingTarget(pendingDeviceId, pendingDeviceName, connectedCycle, out _))
            {
                return true;
            }

            (defaultConfirmed, _) = confirmCurrentDefaultTarget(pendingDeviceId, pendingDeviceName);
            return defaultConfirmed;
        }

        internal static int ResolveReconnectConnectedOverlaySuppressMs(
            int baseSuppressMs,
            int stabilizeWindowMs,
            int timeoutGraceMs,
            int recheckIntervalMs)
        {
            long reconnectSuppressMs = (long)Math.Max(0, stabilizeWindowMs)
                + Math.Max(0, timeoutGraceMs)
                + Math.Max(0, recheckIntervalMs);

            if (reconnectSuppressMs > int.MaxValue)
            {
                reconnectSuppressMs = int.MaxValue;
            }

            return Math.Max(Math.Max(0, baseSuppressMs), (int)reconnectSuppressMs);
        }

        public async Task<List<MMDevice>> RecheckDevicesAfterReconnectAttemptAsync(
            string opId,
            string kind,
            IReadOnlyList<CycleDevice> configuredCycle,
            List<MMDevice> activeDevices,
            AppSwitchDeviceListRefresher replaceDevices,
            Func<CancellationToken> getLifetimeCancellationToken)
        {
            int totalDelayMs = RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs;
            int quickDelayMs = Math.Min(
                RuntimeTuningConfig.BluetoothReconnectPostAttemptQuickRecheckDelayMs,
                totalDelayMs);

            _logger.Debug(
                "BluetoothReconnect",
                () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=attempt-adaptive quickDelayMs={quickDelayMs} totalDelayMs={totalDelayMs}");

            await Task.Delay(quickDelayMs, getLifetimeCancellationToken());
            replaceDevices(ref activeDevices);

            if (configuredCycle.Count > 0)
            {
                var (_, connectedAfterQuickProbe, _) = AppSwitchCycleStateResolver.BuildCycleState(configuredCycle, activeDevices);
                if (connectedAfterQuickProbe.Count > 1)
                {
                    _logger.Debug(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=attempt-adaptive result=quick-ready connected={connectedAfterQuickProbe.Count}");
                    return activeDevices;
                }
            }

            int remainderDelayMs = totalDelayMs - quickDelayMs;
            if (remainderDelayMs > 0)
            {
                await Task.Delay(remainderDelayMs, getLifetimeCancellationToken());
                replaceDevices(ref activeDevices);
            }

            return activeDevices;
        }

        public async Task<List<MMDevice>> RecheckDevicesAfterReconnectSuccessAsync(
            string opId,
            string kind,
            IReadOnlyList<CycleDevice> configuredCycle,
            string pendingDeviceId,
            string pendingDeviceName,
            List<MMDevice> activeDevices,
            AppSwitchDeviceListRefresher replaceDevices,
            Func<string, string, (bool Confirmed, string ResolvedDeviceName)> confirmCurrentDefaultTarget,
            Func<CancellationToken> getLifetimeCancellationToken,
            Func<bool> isLifetimeCancellationRequested)
        {
            _logger.Debug(
                "BluetoothReconnect",
                () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=success-stabilize windowMs={RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs} intervalMs={RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs} stableMs={RuntimeTuningConfig.BluetoothReconnectSuccessActiveStableMs} strategy=event-driven");

            var stopwatch = Stopwatch.StartNew();
            int elapsedMs = 0;
            int activeStableMs = 0;
            int checks = 0;
            bool pendingObserved = false;
            bool pendingIdSpecified = !string.IsNullOrWhiteSpace(pendingDeviceId);

            while (stopwatch.ElapsedMilliseconds < RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs
                && !isLifetimeCancellationRequested())
            {
                int loopStartElapsedMs = (int)stopwatch.ElapsedMilliseconds;

                TaskCompletionSource<bool> stateChangedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnDeviceStateChanged() => stateChangedSignal.TrySetResult(true);
                _audio.DeviceStateChanged += OnDeviceStateChanged;

                try
                {
                    int loopDelayMs = ResolveSuccessObservedRecheckIntervalMs(loopStartElapsedMs, pendingObserved);
                    CancellationToken lifetimeToken = getLifetimeCancellationToken();
                    Task intervalDelayTask = Task.Delay(loopDelayMs, lifetimeToken);
                    await Task.WhenAny(stateChangedSignal.Task, intervalDelayTask);
                    lifetimeToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    _audio.DeviceStateChanged -= OnDeviceStateChanged;
                }

                elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                int elapsedDeltaMs = Math.Max(1, elapsedMs - loopStartElapsedMs);
                bool signaledByDeviceState = stateChangedSignal.Task.IsCompleted;
                checks++;

                replaceDevices(ref activeDevices);
                var (_, connectedCycle, _) = AppSwitchCycleStateResolver.BuildCycleState(configuredCycle, activeDevices);
                bool pendingSatisfied = IsReconnectSuccessPendingTargetSatisfied(
                    connectedCycle,
                    pendingIdSpecified,
                    pendingDeviceId,
                    pendingDeviceName,
                    confirmCurrentDefaultTarget,
                    out bool defaultConfirmed);

                if (pendingSatisfied)
                {
                    pendingObserved = true;
                }

                if (connectedCycle.Count > 1 && pendingSatisfied)
                {
                    activeStableMs += elapsedDeltaMs;
                    if (activeStableMs < RuntimeTuningConfig.BluetoothReconnectSuccessActiveStableMs)
                    {
                        continue;
                    }

                    _logger.Debug(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=success-stabilize result=ready elapsedMs={elapsedMs} connected={connectedCycle.Count} pendingSatisfied={pendingSatisfied} defaultConfirmed={defaultConfirmed} activeStableMs={activeStableMs} checks={checks} wake={(signaledByDeviceState ? "event" : "interval")}");
                    return activeDevices;
                }

                if (defaultConfirmed)
                {
                    _logger.Debug(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=success-stabilize result=ready-default-confirmed elapsedMs={elapsedMs} connected={connectedCycle.Count} checks={checks} wake={(signaledByDeviceState ? "event" : "interval")}");
                    return activeDevices;
                }

                activeStableMs = 0;
            }

            int timeoutGraceMs = RuntimeTuningConfig.BluetoothReconnectSuccessTimeoutGraceMs;
            if (ShouldApplyReconnectSuccessTimeoutGrace(timeoutGraceMs))
            {
                int graceDeadlineMs = elapsedMs + timeoutGraceMs;
                _logger.Debug(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=success-stabilize result=grace-start elapsedMs={elapsedMs} graceMs={timeoutGraceMs} checks={checks}");

                while (stopwatch.ElapsedMilliseconds < graceDeadlineMs && !isLifetimeCancellationRequested())
                {
                    await Task.Delay(RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs, getLifetimeCancellationToken());
                    elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                    checks++;

                    replaceDevices(ref activeDevices);
                    var (_, connectedCycle, _) = AppSwitchCycleStateResolver.BuildCycleState(configuredCycle, activeDevices);
                    bool pendingSatisfied = IsReconnectSuccessPendingTargetSatisfied(
                        connectedCycle,
                        pendingIdSpecified,
                        pendingDeviceId,
                        pendingDeviceName,
                        confirmCurrentDefaultTarget,
                        out bool defaultConfirmed);
                    if (pendingSatisfied)
                    {
                        pendingObserved = true;
                    }

                    if (connectedCycle.Count > 1 && pendingSatisfied)
                    {
                        _logger.Debug(
                            "BluetoothReconnect",
                            () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=success-stabilize result=ready-grace elapsedMs={elapsedMs} connected={connectedCycle.Count} pendingSatisfied={pendingSatisfied} defaultConfirmed={defaultConfirmed} checks={checks}");
                        return activeDevices;
                    }

                    if (defaultConfirmed)
                    {
                        _logger.Debug(
                            "BluetoothReconnect",
                            () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=success-stabilize result=ready-default-confirmed-grace elapsedMs={elapsedMs} connected={connectedCycle.Count} checks={checks}");
                        return activeDevices;
                    }
                }
            }

            _logger.Debug(
                "BluetoothReconnect",
                () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Recheck} | opId={opId} kind={kind} mode=success-stabilize result=timeout elapsedMs={elapsedMs} checks={checks} pendingObserved={pendingObserved} graceMs={timeoutGraceMs}");
            return activeDevices;
        }
    }
}
