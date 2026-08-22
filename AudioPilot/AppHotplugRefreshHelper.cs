using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot
{
    /// <summary>
    /// Captures the externally-owned callbacks and counters that the debounced hotplug refresh helper needs in
    /// order to run without depending on a materialized main-window implementation.
    /// </summary>
    internal readonly record struct AppHotplugRefreshDependencies(
        Func<bool> IsShutdownRequested,
        Func<int, CancellationToken, Task> DelayAsync,
        Func<int> ConsumePendingSignals,
        Action<int> AddCoalescedEvents,
        Func<Task> RefreshDevicesForHotplugAsync,
        Func<CancellationToken, Task> WaitForHotplugRefreshSettlementAsync,
        Action ProcessPostRefresh,
        Func<CancellationToken, Task> ExecuteDeviceChangeTriggeredRoutinesAsync,
        Func<int> IncrementAppliedRefreshes,
        Func<int> ReadCoalescedEvents,
        Func<int> ReadSuppressedRefreshes,
        int DiagnosticsInterval);

    internal static class AppHotplugRefreshHelper
    {
        internal static int ResolveDebounceMs(int pendingSignals, int configuredDebounceMs, bool isWindowVisible)
        {
            int normalized = Math.Max(1, configuredDebounceMs);
            return !isWindowVisible || pendingSignals <= 1
                ? Math.Min(normalized, RuntimeTuningConfig.HotplugRefreshFastPathDebounceMs)
                : normalized;
        }

        internal static async Task WaitForSettlementAsync(
            Func<CancellationToken, Task> waitForRefreshSettlementAsync,
            CancellationToken cancellationToken)
        {
            await waitForRefreshSettlementAsync(cancellationToken);
            await Task.Yield();
        }

        /// <summary>
        /// Executes the debounced hotplug refresh pipeline, including device refresh, settlement waiting,
        /// post-refresh processing, and device-triggered routine execution.
        /// </summary>
        /// <remarks>
        /// Shutdown and debounce cancellation are treated as expected exits and stop the pipeline between phases.
        /// The settlement wait is bounded by the shared shutdown timeout so a stuck refresh cannot block later
        /// post-refresh work forever; when that timeout is hit the helper logs a warning and continues with the
        /// best available state instead of abandoning the refresh batch.
        /// </remarks>
        public static async Task ExecuteAsync(
            int debounceDelayMs,
            AppHotplugRefreshDependencies dependencies,
            Logger logger,
            string failureContext,
            CancellationToken debounceToken)
        {
            try
            {
                bool debugEnabled = logger.IsEnabled(LogLevel.Debug);
                double delayMs = 0;
                double refreshMs = 0;
                double settlementMs = 0;
                double postRefreshMs = 0;
                double routinesMs = 0;
                bool settlementTimedOut = false;
                long totalStartTimestamp = 0;

                if (debugEnabled)
                {
                    totalStartTimestamp = Stopwatch.GetTimestamp();
                }

                if (dependencies.IsShutdownRequested())
                {
                    return;
                }

                long phaseStartTimestamp = 0;
                if (debugEnabled)
                {
                    phaseStartTimestamp = Stopwatch.GetTimestamp();
                }

                await dependencies.DelayAsync(debounceDelayMs, debounceToken);
                if (debugEnabled)
                {
                    delayMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                }

                if (dependencies.IsShutdownRequested())
                {
                    return;
                }

                int coalescedSignals = dependencies.ConsumePendingSignals();
                int coalescedExtras = Math.Max(0, coalescedSignals - 1);
                if (coalescedExtras > 0)
                {
                    dependencies.AddCoalescedEvents(coalescedExtras);
                }

                if (debugEnabled)
                {
                    phaseStartTimestamp = Stopwatch.GetTimestamp();
                }

                await dependencies.RefreshDevicesForHotplugAsync();
                if (debugEnabled)
                {
                    refreshMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                }

                if (dependencies.IsShutdownRequested())
                {
                    return;
                }

                phaseStartTimestamp = Stopwatch.GetTimestamp();
                Task settlementTask = dependencies.WaitForHotplugRefreshSettlementAsync(debounceToken);
                Task timeoutTask = Task.Delay(AppConstants.Timing.ShutdownStepTimeoutMs, debounceToken);
                Task completedTask = await Task.WhenAny(settlementTask, timeoutTask);
                settlementMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                if (completedTask == timeoutTask)
                {
                    settlementTimedOut = true;
                    logger.Warning(
                        "AppHotplugRefreshHelper",
                        $"hotplug-refresh-settlement-timeout | timeoutMs={AppConstants.Timing.ShutdownStepTimeoutMs} elapsedMs={settlementMs:F1}");
                }
                else
                {
                    await settlementTask;
                }

                if (dependencies.IsShutdownRequested())
                {
                    return;
                }

                if (debugEnabled)
                {
                    phaseStartTimestamp = Stopwatch.GetTimestamp();
                }

                dependencies.ProcessPostRefresh();
                if (debugEnabled)
                {
                    postRefreshMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                }

                if (dependencies.IsShutdownRequested())
                {
                    return;
                }

                if (debugEnabled)
                {
                    phaseStartTimestamp = Stopwatch.GetTimestamp();
                }

                await dependencies.ExecuteDeviceChangeTriggeredRoutinesAsync(debounceToken);
                if (debugEnabled)
                {
                    routinesMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                }

                int appliedRefreshes = dependencies.IncrementAppliedRefreshes();
                if (debugEnabled && ShouldLogDiagnostics(appliedRefreshes, dependencies.DiagnosticsInterval))
                {
                    logger.Debug(
                        "AppHotplugRefreshHelper",
                        $"{AppConstants.Audio.LogEvents.Diagnostics.HotplugDiagnostics} | " +
                        $"coalescedEvents={dependencies.ReadCoalescedEvents()} " +
                        $"suppressedRefreshes={dependencies.ReadSuppressedRefreshes()} " +
                        $"appliedRefreshes={appliedRefreshes} " +
                        $"lastBatchSignals={coalescedSignals} " +
                        $"debounceMs={debounceDelayMs} " +
                        $"delayElapsedMs={delayMs:F1} " +
                        $"refreshElapsedMs={refreshMs:F1} " +
                        $"settlementElapsedMs={settlementMs:F1} " +
                        $"settlementTimedOut={settlementTimedOut} " +
                        $"postRefreshElapsedMs={postRefreshMs:F1} " +
                        $"routinesElapsedMs={routinesMs:F1} " +
                        $"totalElapsedMs={Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds:F1}");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Warning("AppHotplugRefreshHelper", "hotplug-refresh-failed", failureContext, ex);
            }
        }

        public static bool ShouldLogDiagnostics(int appliedRefreshes, int diagnosticsInterval)
        {
            int normalizedInterval = Math.Max(1, diagnosticsInterval);
            return appliedRefreshes == 1 || (appliedRefreshes % normalizedInterval) == 0;
        }
    }
}
