using System.Diagnostics;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal readonly record struct SwitchExecutionCallbacks(
        Func<MMDevice?> GetCurrentDevice,
        Func<CycleDevice, CycleDevice?> TryResolveActiveCycleEntry,
        Func<List<MMDevice>> GetActiveDevices,
        Func<MMDevice, CycleDevice, string, ValueTask<(bool Success, string? DeviceName)>> SwitchDirectAsync,
        Func<string, CycleDevice, string, ValueTask<(bool Success, string? DeviceName)>> SwitchFinalAsync,
        Func<string, IReadOnlyList<CycleDevice>, List<MMDevice>, Task<List<MMDevice>>> RecheckAfterReconnectAttemptAsync,
        Func<string, IReadOnlyList<CycleDevice>, string, string, List<MMDevice>, Task<List<MMDevice>>> RecheckAfterReconnectSuccessAsync,
        Func<bool, string?, bool> IsSwitchSuccess,
        Func<string, string, (bool Confirmed, string ResolvedDeviceName)> ConfirmCurrentDefaultTarget,
        Action<string, IReadOnlyList<CycleDevice>, string, string> ScheduleDeferredAutoSwitch,
        Action<string, string> OnSwitchSuccess,
        Action<string> OnFinalServiceFailure,
        Action<string> OnReconnectPendingFailure,
        Action<string> OnNoConnectedDevices,
        Action<string> OnMissingCurrentDevice,
        Action<string> OnConfirmedSingleConnectedSuccess,
        Action<string> OnReconnectStarted,
        Action<int, int, string> OnReconnectAttemptProgress,
        BluetoothReconnectDeviceKind ReconnectKind,
        OverlayDeviceKind OverlayDeviceKind,
        string SuccessEventName,
        string FailedEventName,
        string SkipEventName,
        string SkipDisconnectedEventName,
        string PhasesEventName,
        string SuccessOverlayTitle,
        string FailureOverlayTitle,
        string DisposeTraceDeviceKind,
        bool SuppressConnectedOverlayForOutput);

    internal sealed partial class AppSwitchCommandCoordinator(
        AudioDeviceService audio,
        OverlayService overlay,
        Logger logger,
        BluetoothReconnectCoordinator bluetoothReconnectCoordinator,
        Action<bool, int>? suppressConnectedHotplugOverlay = null) : IDisposable
    {
        private readonly AudioDeviceService _audio = audio;
        private readonly OverlayService _overlay = overlay;
        private readonly Logger _logger = logger;
        private readonly BluetoothReconnectCoordinator _bluetoothReconnectCoordinator = bluetoothReconnectCoordinator;
        private readonly Action<bool, int> _suppressConnectedHotplugOverlay = suppressConnectedHotplugOverlay ?? ((_, _) => { });
        private readonly AppSwitchRequestCoordinator _requestCoordinator = new(logger);
        private readonly AppSwitchOutputIntentTracker _outputIntentTracker = new(audio);
        private readonly AppSwitchInputIntentTracker _inputIntentTracker = new(audio);
        private readonly AppSwitchReconnectRecoveryCoordinator _reconnectRecoveryCoordinator = new(audio, logger);
        private readonly AppSwitchRetryCoordinator _retryCoordinator = new(logger);
        private readonly AppSwitchDeferredAutoSwitchCoordinator _deferredAutoSwitchCoordinator = new(audio, overlay, logger, suppressConnectedHotplugOverlay ?? ((_, _) => { }));
        private static readonly CancellationToken CanceledLifetimeToken = new(canceled: true);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly SemaphoreSlim _directOutputGate = new(1, 1);
        private readonly SemaphoreSlim _directInputGate = new(1, 1);
        private CancellationTokenSource? _directOutputCts;
        private CancellationTokenSource? _directInputCts;
        private int _disposeStarted;

        internal static int ResolveCycleTargetIndex(int currentIndex, int cycleCount, bool reverse)
        {
            return AppSwitchCycleStateResolver.ResolveCycleTargetIndex(currentIndex, cycleCount, reverse);
        }

        internal static bool TryResolveDeferredSwitchTargetIndex(
            string? currentDeviceId,
            IReadOnlyList<CycleDevice> connectedCycle,
            bool reverse,
            out int targetIndex)
        {
            return AppSwitchCycleStateResolver.TryResolveDeferredSwitchTargetIndex(
                currentDeviceId,
                connectedCycle,
                reverse,
                out targetIndex);
        }

        internal static bool TryResolveDeferredPendingTarget(
            string? pendingDeviceId,
            string? pendingDeviceName,
            IReadOnlyList<CycleDevice> connectedCycle,
            out CycleDevice targetDevice)
        {
            return AppSwitchCycleStateResolver.TryResolveDeferredPendingTarget(
                pendingDeviceId,
                pendingDeviceName,
                connectedCycle,
                out targetDevice);
        }

        internal static SwitchCycleStateResolution ResolveInitialCycleState(
            IReadOnlyList<CycleDevice> configuredCycle,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            Func<SwitchCycleState> buildEnumeratedState)
        {
            return AppSwitchCycleStateResolver.ResolveInitialCycleState(
                configuredCycle,
                tryResolveActiveDevice,
                buildEnumeratedState);
        }

        internal static bool TryBuildExactIdCycleState(
            IReadOnlyList<CycleDevice> configuredCycle,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            out List<CycleDevice> connectedCycle,
            out List<CycleDevice> skippedDevices)
        {
            return AppSwitchCycleStateResolver.TryBuildExactIdCycleState(
                configuredCycle,
                tryResolveActiveDevice,
                out connectedCycle,
                out skippedDevices);
        }

        internal static bool TryResolveConfiguredTarget(
            IReadOnlyList<CycleDevice> configuredCycle,
            string? currentDeviceId,
            bool reverse,
            out CycleDevice targetDevice)
        {
            return AppSwitchCycleStateResolver.TryResolveConfiguredTarget(
                configuredCycle,
                currentDeviceId,
                reverse,
                out targetDevice);
        }

        internal static bool TryResolveConfiguredDeviceByActiveName(
            CycleDevice configured,
            IReadOnlyDictionary<string, string> activeNamesById,
            ISet<string> reservedActiveIds,
            out CycleDevice remappedDevice)
        {
            return AppSwitchCycleStateResolver.TryResolveConfiguredDeviceByActiveName(
                configured,
                activeNamesById,
                reservedActiveIds,
                out remappedDevice);
        }

        internal static SingleConnectedCycleDecision ResolveSingleConnectedCycleDecision(
            int connectedCount,
            int skippedCount,
            bool reconnectAttempted,
            bool reconnectSucceeded)
        {
            return AppSwitchCycleStateResolver.ResolveSingleConnectedCycleDecision(
                connectedCount,
                skippedCount,
                reconnectAttempted,
                reconnectSucceeded);
        }

        internal static bool ShouldContinueDeferredLoop(DateTime nowUtc, DateTime deadlineUtc, bool isLifetimeCancellationRequested)
        {
            return !isLifetimeCancellationRequested && nowUtc < deadlineUtc;
        }

        internal static bool ShouldEmitDeferredTimeoutNotification(DateTime nowUtc, DateTime deadlineUtc, bool isLifetimeCancellationRequested)
        {
            return !isLifetimeCancellationRequested && nowUtc >= deadlineUtc;
        }

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

        internal static string ResolvePhaseResult(string currentPhaseResult, bool success, bool deferredAutoSwitchScheduled)
        {
            if (success)
            {
                return "success";
            }

            if (deferredAutoSwitchScheduled)
            {
                return "deferred";
            }

            return currentPhaseResult;
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

        internal static int ResolveCoalescedRetryDelayMs(
            bool wasDebounced,
            long nowTicks,
            long lastRequestTicks,
            long debounceWindowTicks,
            int fallbackDelayMs)
        {
            return AppSwitchRetryCoordinator.ResolveCoalescedRetryDelayMs(
                wasDebounced,
                nowTicks,
                lastRequestTicks,
                debounceWindowTicks,
                fallbackDelayMs);
        }

        private async ValueTask<bool> ExecuteSwitchCoreAsync(
            string opId,
            IReadOnlyList<CycleDevice> configuredCycle,
            bool reverse,
            BluetoothReconnectOptions reconnectOptions,
            Func<bool> isOperationCurrent,
            SwitchExecutionCallbacks callbacks,
            CancellationToken operationCancellationToken)
        {
            var switchStopwatch = Stopwatch.StartNew();
            double enumerateDevicesMs = 0;
            double currentDeviceLookupMs = 0;
            double cycleResolveMs = 0;
            double serviceSwitchMs = 0;
            string phaseResult = "failed";
            bool deferredAutoSwitchScheduled = false;
            List<MMDevice> activeDevices = [];
            MMDevice? currentDevice = null;
            bool reconnectActivityTracked = false;

            try
            {
                ThrowIfOperationCanceled(isOperationCurrent, operationCancellationToken);
                currentDevice = callbacks.GetCurrentDevice();
                currentDeviceLookupMs = switchStopwatch.Elapsed.TotalMilliseconds;

                if (currentDevice != null && AppSwitchCycleStateResolver.TryResolveConfiguredTarget(configuredCycle, currentDevice.ID, reverse, out CycleDevice directTargetCandidate))
                {
                    cycleResolveMs = switchStopwatch.Elapsed.TotalMilliseconds - currentDeviceLookupMs;

                    if (!directTargetCandidate.Id.Equals(currentDevice.ID, StringComparison.OrdinalIgnoreCase))
                    {
                        ThrowIfOperationCanceled(isOperationCurrent, operationCancellationToken);
                        double directServiceSwitchStartMs = switchStopwatch.Elapsed.TotalMilliseconds;
                        var (directSuccess, directDeviceName) = await callbacks.SwitchDirectAsync(currentDevice, directTargetCandidate, opId);
                        serviceSwitchMs = switchStopwatch.Elapsed.TotalMilliseconds - directServiceSwitchStartMs;

                        if (callbacks.IsSwitchSuccess(directSuccess, directDeviceName))
                        {
                            string successDeviceName = ResolveSwitchSuccessDeviceName(directTargetCandidate.Name, directDeviceName);
                            callbacks.OnSwitchSuccess(opId, successDeviceName);
                            _logger.Info("AppViewModel", () => $"{callbacks.SuccessEventName} | opId={opId} target={LogPrivacy.Device(successDeviceName)}");
                            phaseResult = "success";
                            return true;
                        }
                    }
                }

                SwitchCycleStateResolution initialCycleState = AppSwitchCycleStateResolver.ResolveInitialCycleState(
                    configuredCycle,
                    callbacks.TryResolveActiveCycleEntry,
                    () =>
                    {
                        activeDevices = callbacks.GetActiveDevices();
                        enumerateDevicesMs = switchStopwatch.Elapsed.TotalMilliseconds;
                        return AppSwitchCycleStateResolver.BuildCycleState(configuredCycle, activeDevices);
                    });
                Dictionary<string, MMDevice> activeById = initialCycleState.State.ActiveById;
                List<CycleDevice> connectedCycle = initialCycleState.State.ConnectedCycle;
                List<CycleDevice> skippedDevices = initialCycleState.State.SkippedDevices;

                cycleResolveMs = Math.Max(cycleResolveMs, switchStopwatch.Elapsed.TotalMilliseconds - currentDeviceLookupMs);

                bool currentDeviceIsConfiguredCycleDevice = currentDevice != null
                    && AppSwitchCycleStateResolver.ContainsConfiguredDeviceId(configuredCycle, currentDevice.ID);

                if (currentDevice != null
                    && !currentDeviceIsConfiguredCycleDevice
                    && connectedCycle.Count == 1
                    && !connectedCycle[0].Id.Equals(currentDevice.ID, StringComparison.OrdinalIgnoreCase))
                {
                    CycleDevice externalDefaultTarget = connectedCycle[0];
                    if (skippedDevices.Count > 0)
                    {
                        _logger.Warning("AppViewModel", () => $"{callbacks.SkipDisconnectedEventName} | opId={opId} count={skippedDevices.Count} reason=current-outside-cycle");
                    }

                    ThrowIfOperationCanceled(isOperationCurrent, operationCancellationToken);
                    double externalDefaultSwitchStartMs = switchStopwatch.Elapsed.TotalMilliseconds;
                    var (externalDefaultSuccess, externalDefaultDeviceName) = await callbacks.SwitchFinalAsync(currentDevice.ID, externalDefaultTarget, opId);
                    serviceSwitchMs = switchStopwatch.Elapsed.TotalMilliseconds - externalDefaultSwitchStartMs;

                    if (callbacks.IsSwitchSuccess(externalDefaultSuccess, externalDefaultDeviceName))
                    {
                        string successDeviceName = ResolveSwitchSuccessDeviceName(externalDefaultTarget.Name, externalDefaultDeviceName);
                        callbacks.OnSwitchSuccess(opId, successDeviceName);
                        _logger.Info("AppViewModel", () => $"{callbacks.SuccessEventName} | opId={opId} reason=current-outside-cycle target={LogPrivacy.Device(successDeviceName)}");
                        phaseResult = "success";
                        return true;
                    }

                    callbacks.OnFinalServiceFailure(externalDefaultTarget.Name);
                    _logger.Warning("AppViewModel", () => $"{callbacks.FailedEventName} | opId={opId} reason=service-reported-failure currentOutsideCycle=true");
                    return false;
                }

                bool reconnectAttempted = false;
                bool reconnectSucceeded = false;
                if (connectedCycle.Count <= 1 && skippedDevices.Count > 0)
                {
                    string reconnectTargetName = skippedDevices[0].Name;
                    string reconnectTargetId = skippedDevices[0].Id;
                    reconnectActivityTracked = true;
                    _outputIntentTracker.SetActiveTarget(callbacks.ReconnectKind, reconnectTargetId, reconnectTargetName);
                    _outputIntentTracker.SetReconnectOverlayDeviceName(callbacks.ReconnectKind, reconnectTargetName);
                    callbacks.OnReconnectStarted(reconnectTargetName);

                    BluetoothReconnectAttemptResult reconnectResult = await _bluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        configuredCycle,
                        new HashSet<string>(activeById.Keys, StringComparer.OrdinalIgnoreCase),
                        callbacks.ReconnectKind,
                        reconnectOptions,
                        opId,
                        onAttemptProgress: (attempt, maxAttempts, attemptDeviceName) =>
                        {
                            if (attempt <= 1)
                            {
                                return;
                            }

                            _outputIntentTracker.SetReconnectOverlayDeviceName(callbacks.ReconnectKind, attemptDeviceName);
                            callbacks.OnReconnectAttemptProgress(attempt, maxAttempts, attemptDeviceName);
                        },
                        cancellationToken: operationCancellationToken);
                    reconnectAttempted = reconnectResult.Attempted;
                    reconnectSucceeded = reconnectResult.Connected;
                    ThrowIfOperationCanceled(isOperationCurrent, operationCancellationToken);

                    if (reconnectResult.Connected)
                    {
                        _suppressConnectedHotplugOverlay(
                            callbacks.SuppressConnectedOverlayForOutput,
                            AppSwitchReconnectRecoveryCoordinator.ResolveReconnectConnectedOverlaySuppressMs(
                                RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                                RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs,
                                RuntimeTuningConfig.BluetoothReconnectSuccessTimeoutGraceMs,
                                RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs));
                        activeDevices = await callbacks.RecheckAfterReconnectSuccessAsync(opId, configuredCycle, reconnectTargetId, reconnectTargetName, activeDevices);
                        ApplyCycleState(AppSwitchCycleStateResolver.BuildCycleState(configuredCycle, activeDevices), out activeById, out connectedCycle, out skippedDevices);
                    }
                    else if (reconnectResult.Attempted)
                    {
                        activeDevices = await callbacks.RecheckAfterReconnectAttemptAsync(opId, configuredCycle, activeDevices);
                        ApplyCycleState(AppSwitchCycleStateResolver.BuildCycleState(configuredCycle, activeDevices), out activeById, out connectedCycle, out skippedDevices);
                    }
                }

                if (connectedCycle.Count == 0)
                {
                    callbacks.OnNoConnectedDevices(opId);
                    return false;
                }

                if (TryHandleSingleConnectedCycle(
                    opId,
                    callbacks.SkipEventName,
                    callbacks.FailedEventName,
                    callbacks.SuccessEventName,
                    callbacks.OverlayDeviceKind,
                    callbacks.SuccessOverlayTitle,
                    callbacks.FailureOverlayTitle,
                    activeDevices,
                    connectedCycle,
                    skippedDevices,
                    reconnectAttempted,
                    reconnectSucceeded,
                    callbacks.OnReconnectPendingFailure,
                    (pendingId, pendingName) =>
                    {
                        if (!isOperationCurrent())
                        {
                            return;
                        }

                        deferredAutoSwitchScheduled = true;
                        callbacks.ScheduleDeferredAutoSwitch(opId, configuredCycle, pendingId, pendingName);
                    },
                    callbacks.ConfirmCurrentDefaultTarget,
                    () => callbacks.OnConfirmedSingleConnectedSuccess(opId),
                    out bool singleConnectedSuccess))
                {
                    phaseResult = ResolvePhaseResult(phaseResult, singleConnectedSuccess, deferredAutoSwitchScheduled);

                    return singleConnectedSuccess;
                }

                currentDevice ??= callbacks.GetCurrentDevice();
                if (currentDevice == null)
                {
                    callbacks.OnMissingCurrentDevice(opId);
                    return false;
                }

                string currentId = currentDevice.ID;
                int currentIndex = connectedCycle.FindIndex(d => d.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase));
                int targetIndex = AppSwitchCycleStateResolver.ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
                CycleDevice targetDevice = connectedCycle[targetIndex];

                if (skippedDevices.Count > 0)
                {
                    _logger.Warning("AppViewModel", () => $"{callbacks.SkipDisconnectedEventName} | opId={opId} count={skippedDevices.Count}");
                }

                ThrowIfOperationCanceled(isOperationCurrent, operationCancellationToken);
                double serviceSwitchStartMs = switchStopwatch.Elapsed.TotalMilliseconds;
                var (success, deviceName) = await callbacks.SwitchFinalAsync(currentId, targetDevice, opId);
                serviceSwitchMs = switchStopwatch.Elapsed.TotalMilliseconds - serviceSwitchStartMs;

                if (callbacks.IsSwitchSuccess(success, deviceName))
                {
                    string successDeviceName = ResolveSwitchSuccessDeviceName(targetDevice.Name, deviceName);
                    callbacks.OnSwitchSuccess(opId, successDeviceName);
                    _logger.Info("AppViewModel", () => $"{callbacks.SuccessEventName} | opId={opId} target={LogPrivacy.Device(successDeviceName)}");
                    phaseResult = "success";
                    return true;
                }

                callbacks.OnFinalServiceFailure(targetDevice.Name);
                _logger.Warning("AppViewModel", () => $"{callbacks.FailedEventName} | opId={opId} reason=service-reported-failure");
                return false;
            }
            catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested || !isOperationCurrent())
            {
                phaseResult = "superseded";
                _logger.Info("AppViewModel", () => $"{callbacks.SkipEventName} | opId={opId} reason=superseded-by-newer-request");
                return false;
            }
            finally
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "AppViewModel",
                        () => $"{callbacks.PhasesEventName} | opId={opId} enumerateMs={enumerateDevicesMs:F1} currentLookupMs={currentDeviceLookupMs:F1} cycleResolveMs={cycleResolveMs:F1} serviceSwitchMs={serviceSwitchMs:F1} totalMs={switchStopwatch.Elapsed.TotalMilliseconds:F1} result={phaseResult}");
                }

                currentDevice?.Dispose();
                DisposeSwitchDevices(activeDevices, callbacks.DisposeTraceDeviceKind);
                if (reconnectActivityTracked)
                {
                    _outputIntentTracker.ClearActiveTarget(callbacks.ReconnectKind);
                    _outputIntentTracker.ClearReconnectOverlayDeviceName(callbacks.ReconnectKind);
                }
            }
        }

        private static void ApplyCycleState(
            SwitchCycleState state,
            out Dictionary<string, MMDevice> activeById,
            out List<CycleDevice> connectedCycle,
            out List<CycleDevice> skippedDevices)
        {
            activeById = state.ActiveById;
            connectedCycle = state.ConnectedCycle;
            skippedDevices = state.SkippedDevices;
        }

        private static string ResolveSwitchSuccessDeviceName(string fallbackName, string? resolvedDeviceName)
        {
            return string.IsNullOrWhiteSpace(resolvedDeviceName)
                ? fallbackName
                : resolvedDeviceName;
        }

        private void DisposeSwitchDevices(IEnumerable<MMDevice> activeDevices, string deviceKind)
        {
            foreach (MMDevice device in activeDevices)
            {
                try
                {
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AppViewModel", () => $"Ignored dispose exception for {deviceKind} device: {ex.GetType().Name}");
                    }
                }
            }
        }

        /// <summary>
        /// Cancels lifetime-scoped deferred work and makes disposal idempotent.
        /// </summary>
        /// <remarks>
        /// The coordinator intentionally does not block waiting for deferred auto-switch loops to finish here. Those
        /// loops observe the shared lifetime token and exit on their own so shutdown paths are not forced to wait on
        /// reconnect polling windows.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _outputIntentTracker.Dispose();
            _inputIntentTracker.Dispose();
            _lifetimeCts.Cancel();
            TryCancelDirectRequest(ref _directOutputCts);
            TryCancelDirectRequest(ref _directInputCts);
            _lifetimeCts.Dispose();
        }

        private static void TryCancelDirectRequest(ref CancellationTokenSource? field)
        {
            CancellationTokenSource? source = Interlocked.Exchange(ref field, null);
            try
            {
                source?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            source?.Dispose();
        }

        private CancellationToken GetLifetimeCancellationToken()
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return CanceledLifetimeToken;
            }

            try
            {
                return _lifetimeCts.Token;
            }
            catch (ObjectDisposedException)
            {
                return CanceledLifetimeToken;
            }
        }

        private bool IsLifetimeCancellationRequested()
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return true;
            }

            try
            {
                return _lifetimeCts.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
