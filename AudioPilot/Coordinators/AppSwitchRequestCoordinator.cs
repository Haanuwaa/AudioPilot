using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal enum AppSwitchRequestRejectionReason
    {
        None,
        InProgress,
        Debounced,
    }

    internal sealed class AppSwitchRequestCoordinator(Logger logger)
    {
        private readonly Logger _logger = logger;
        private int _outputSwitchInFlight;
        private int _inputSwitchInFlight;
        private long _lastOutputSwitchRequestTicks;
        private long _lastInputSwitchRequestTicks;
        private int _outputSkipInProgressCount;
        private int _outputSkipDebouncedCount;
        private int _inputSkipInProgressCount;
        private int _inputSkipDebouncedCount;
        private int _pendingOutputRetryQueued;
        private int _pendingInputRetryQueued;

        public bool TryBeginOutputSwitchRequest(string opId, out AppSwitchRequestRejectionReason rejectionReason)
        {
            return TryBeginSwitchRequest(
                opId,
                isOutput: true,
                ref _outputSwitchInFlight,
                ref _lastOutputSwitchRequestTicks,
                ref _outputSkipInProgressCount,
                ref _outputSkipDebouncedCount,
                RuntimeTuningConfig.OutputSwitchDebounceMs,
                AppConstants.Audio.LogEvents.OutputSwitch.Skip,
                out rejectionReason);
        }

        public void EndOutputSwitchRequest()
        {
            EndSwitchRequest(ref _outputSwitchInFlight);
        }

        public bool TryBeginInputSwitchRequest(string opId, out AppSwitchRequestRejectionReason rejectionReason)
        {
            return TryBeginSwitchRequest(
                opId,
                isOutput: false,
                ref _inputSwitchInFlight,
                ref _lastInputSwitchRequestTicks,
                ref _inputSkipInProgressCount,
                ref _inputSkipDebouncedCount,
                RuntimeTuningConfig.InputSwitchDebounceMs,
                AppConstants.Audio.LogEvents.InputSwitch.Skip,
                out rejectionReason);
        }

        public void EndInputSwitchRequest()
        {
            EndSwitchRequest(ref _inputSwitchInFlight);
        }

        public bool TryBeginOutputCoalescedRetry()
        {
            return TryBeginCoalescedRetry(ref _pendingOutputRetryQueued);
        }

        public void EndOutputCoalescedRetry()
        {
            EndCoalescedRetry(ref _pendingOutputRetryQueued);
        }

        public bool TryBeginInputCoalescedRetry()
        {
            return TryBeginCoalescedRetry(ref _pendingInputRetryQueued);
        }

        public void EndInputCoalescedRetry()
        {
            EndCoalescedRetry(ref _pendingInputRetryQueued);
        }

        public long GetLastOutputSwitchRequestTicks()
        {
            return GetLastSwitchRequestTicks(ref _lastOutputSwitchRequestTicks);
        }

        public long GetLastInputSwitchRequestTicks()
        {
            return GetLastSwitchRequestTicks(ref _lastInputSwitchRequestTicks);
        }

        public static int ResolveCoalescedRetryDelayMs(
            bool wasDebounced,
            long nowTicks,
            long lastRequestTicks,
            long debounceWindowTicks,
            int fallbackDelayMs)
        {
            int normalizedFallbackDelayMs = Math.Max(1, fallbackDelayMs);
            if (!wasDebounced || lastRequestTicks <= 0 || debounceWindowTicks <= 0)
            {
                return normalizedFallbackDelayMs;
            }

            long elapsedTicks = Math.Max(0, nowTicks - lastRequestTicks);
            if (elapsedTicks >= debounceWindowTicks)
            {
                return 1;
            }

            long remainingTicks = debounceWindowTicks - elapsedTicks;
            int remainingMs = (int)Math.Ceiling(TimeSpan.FromTicks(remainingTicks).TotalMilliseconds);
            return Math.Clamp(remainingMs + 1, 1, normalizedFallbackDelayMs);
        }

        private bool TryBeginSwitchRequest(
            string opId,
            bool isOutput,
            ref int switchInFlight,
            ref long lastRequestTicks,
            ref int skipInProgressCount,
            ref int skipDebouncedCount,
            int debounceMs,
            string skipLogEvent,
            out AppSwitchRequestRejectionReason rejectionReason)
        {
            rejectionReason = AppSwitchRequestRejectionReason.None;
            if (Interlocked.CompareExchange(ref switchInFlight, 1, 0) != 0)
            {
                int total = Interlocked.Increment(ref skipInProgressCount);
                LogRejectedSwitchRequest(skipLogEvent, opId, isOutput, "in-progress", total);
                rejectionReason = AppSwitchRequestRejectionReason.InProgress;
                return false;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            long previousTicks = Interlocked.Read(ref lastRequestTicks);
            if (previousTicks > 0 && (nowTicks - previousTicks) < TimeSpan.FromMilliseconds(debounceMs).Ticks)
            {
                int total = Interlocked.Increment(ref skipDebouncedCount);
                LogRejectedSwitchRequest(skipLogEvent, opId, isOutput, "debounced", total);
                Interlocked.Exchange(ref switchInFlight, 0);
                rejectionReason = AppSwitchRequestRejectionReason.Debounced;
                return false;
            }

            Interlocked.Exchange(ref lastRequestTicks, nowTicks);
            return true;
        }

        private void LogRejectedSwitchRequest(string skipLogEvent, string opId, bool isOutput, string reason, int totalSkipsForReason)
        {
            _logger.Debug("AppViewModel", () => $"{skipLogEvent} | opId={opId} reason=coordinator-{reason}");
            LogSwitchSpamGuardDiagnosticsIfNeeded(isOutput, reason, totalSkipsForReason);
        }

        private static void EndSwitchRequest(ref int switchInFlight)
        {
            Interlocked.Exchange(ref switchInFlight, 0);
        }

        private static bool TryBeginCoalescedRetry(ref int pendingRetryQueued)
        {
            return Interlocked.CompareExchange(ref pendingRetryQueued, 1, 0) == 0;
        }

        private static void EndCoalescedRetry(ref int pendingRetryQueued)
        {
            Interlocked.Exchange(ref pendingRetryQueued, 0);
        }

        private static long GetLastSwitchRequestTicks(ref long lastRequestTicks)
        {
            return Interlocked.Read(ref lastRequestTicks);
        }

        private void LogSwitchSpamGuardDiagnosticsIfNeeded(bool isOutput, string reason, int totalSkipsForReason)
        {
            int interval = Math.Max(1, AppConstants.Timing.SwitchSpamGuardDiagnosticsLogEveryN);
            if (totalSkipsForReason != 1 && (totalSkipsForReason % interval) != 0)
            {
                return;
            }

            int outputInProgress = Interlocked.CompareExchange(ref _outputSkipInProgressCount, 0, 0);
            int outputDebounced = Interlocked.CompareExchange(ref _outputSkipDebouncedCount, 0, 0);
            int inputInProgress = Interlocked.CompareExchange(ref _inputSkipInProgressCount, 0, 0);
            int inputDebounced = Interlocked.CompareExchange(ref _inputSkipDebouncedCount, 0, 0);

            string direction = isOutput ? "output" : "input";
            _logger.Trace(
                "AppViewModel",
                $"{AppConstants.Audio.LogEvents.Diagnostics.SwitchSpamGuardDiagnostics} | direction={direction} reason={reason} reasonCount={totalSkipsForReason} outputInProgress={outputInProgress} outputDebounced={outputDebounced} inputInProgress={inputInProgress} inputDebounced={inputDebounced}",
                nameof(LogSwitchSpamGuardDiagnosticsIfNeeded));
        }
    }
}
