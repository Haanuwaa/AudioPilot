using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal enum MixerRefreshTarget
    {
        Output,
        Input,
        Both,
    }

    internal enum SessionCreatedMixerRefreshOutcome
    {
        Refreshed,
        SkippedWindowHidden,
        SkippedRefreshInProgress,
    }

    internal readonly record struct SessionCreatedMixerRefreshInput(int QueuedSignals, int DebounceMs, string SignalLabel, MixerRefreshTarget Target);

    internal readonly record struct SessionCreatedMixerRefreshDrainResult(int CoalescedSignals, MixerRefreshTarget Target);

    internal readonly record struct SessionCreatedMixerRefreshResolution(
        SessionCreatedMixerRefreshOutcome Outcome,
        int WaitCount,
        double WaitMs);

    internal readonly record struct SessionCreatedMixerRefreshDependencies(
        Func<MixerRefreshTarget, SessionCreatedMixerRefreshDrainResult> DrainPendingSignals,
        Func<bool> IsWindowVisible,
        Func<bool> IsCleaningUp,
        Func<MixerRefreshTarget, bool> IsRefreshInProgress,
        Func<MixerRefreshTarget, CancellationToken, Task> WaitForRefreshSettlementAsync,
        int RefreshSettlementTimeoutMs,
        Func<MixerRefreshTarget, bool, Task> RefreshMixerAsync);

    internal static class AppMixerRefreshCoordinator
    {
        /// <summary>
        /// Debounces session-created notifications and refreshes the mixer when the window is still eligible.
        /// </summary>
        /// <remarks>
        /// After the debounce delay, pending signals are drained so bursty session creation collapses into one mixer
        /// refresh. Visibility, cleanup, and in-progress refresh guards are re-evaluated at execution time so stale
        /// queued work can skip itself safely.
        /// </remarks>
        internal static async Task<SessionCreatedMixerRefreshOutcome> ExecuteSessionCreatedRefreshAsync(
            SessionCreatedMixerRefreshInput input,
            SessionCreatedMixerRefreshDependencies dependencies,
            Logger logger,
            CancellationToken cancellationToken)
        {
            bool debugEnabled = logger.IsEnabled(LogLevel.Debug);
            bool logDetailedRefreshEvents = ShouldLogDetailedRefreshEvents(input.SignalLabel);
            long refreshStartTimestamp = 0;
            if (debugEnabled)
            {
                refreshStartTimestamp = Stopwatch.GetTimestamp();
            }

            await Task.Delay(input.DebounceMs, cancellationToken);

            SessionCreatedMixerRefreshDrainResult drainResult = dependencies.DrainPendingSignals(input.Target);
            int coalescedSignals = drainResult.CoalescedSignals;
            MixerRefreshTarget refreshTarget = drainResult.Target;
            if (coalescedSignals <= 0)
            {
                coalescedSignals = input.QueuedSignals;
                refreshTarget = input.Target;
            }

            SessionCreatedMixerRefreshResolution resolution = await ResolveSessionCreatedRefreshOutcomeAsync(
                dependencies,
                logger,
                coalescedSignals,
                refreshTarget,
                debugEnabled,
                cancellationToken);

            if (debugEnabled && logDetailedRefreshEvents)
            {
                logger.Debug(
                    "AppViewModel",
                    () => $"{AppConstants.Audio.LogEvents.Diagnostics.MixerRefreshCoordination} | signal={input.SignalLabel} coalescedSignals={coalescedSignals} debounceMs={input.DebounceMs} outcome={resolution.Outcome} waitCount={resolution.WaitCount} waitMs={resolution.WaitMs:F1} totalMs={Stopwatch.GetElapsedTime(refreshStartTimestamp).TotalMilliseconds:F1}");
            }

            switch (resolution.Outcome)
            {
                case SessionCreatedMixerRefreshOutcome.SkippedWindowHidden:
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AppViewModel", () => $"{input.SignalLabel} events coalesced={coalescedSignals}; mixer refresh skipped because window is not visible");
                    }
                    return resolution.Outcome;

                case SessionCreatedMixerRefreshOutcome.SkippedRefreshInProgress:
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AppViewModel", () => $"{input.SignalLabel} events coalesced={coalescedSignals}; mixer refresh skipped because a refresh is already in progress");
                    }
                    return resolution.Outcome;

                default:
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AppViewModel", () => $"{input.SignalLabel} events coalesced={coalescedSignals}; triggering debounced mixer refresh");
                    }

                    await dependencies.RefreshMixerAsync(refreshTarget, dependencies.IsWindowVisible());
                    return SessionCreatedMixerRefreshOutcome.Refreshed;
            }
        }

        private static async Task<SessionCreatedMixerRefreshResolution> ResolveSessionCreatedRefreshOutcomeAsync(
            SessionCreatedMixerRefreshDependencies dependencies,
            Logger logger,
            int coalescedSignals,
            MixerRefreshTarget refreshTarget,
            bool collectTiming,
            CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, dependencies.RefreshSettlementTimeoutMs));
            bool waitedForRefresh = false;
            int waitCount = 0;
            double waitMs = 0;

            while (true)
            {
                bool isWindowVisible = dependencies.IsWindowVisible();
                bool isCleaningUp = dependencies.IsCleaningUp();

                SessionCreatedMixerRefreshOutcome outcome = ResolveSessionCreatedRefreshOutcome(
                    isWindowVisible,
                    isCleaningUp,
                    dependencies.IsRefreshInProgress(refreshTarget));
                if (outcome != SessionCreatedMixerRefreshOutcome.SkippedRefreshInProgress)
                {
                    if (waitedForRefresh && logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AppViewModel", () => $"Session-created events coalesced={coalescedSignals}; mixer refresh resumed after waiting for the active refresh to settle");
                    }

                    return new SessionCreatedMixerRefreshResolution(outcome, waitCount, waitMs);
                }

                waitedForRefresh = true;
                TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return new SessionCreatedMixerRefreshResolution(SessionCreatedMixerRefreshOutcome.SkippedRefreshInProgress, waitCount, waitMs);
                }

                long waitStartTimestamp = 0;
                if (collectTiming)
                {
                    waitStartTimestamp = Stopwatch.GetTimestamp();
                }

                waitCount++;
                try
                {
                    await dependencies.WaitForRefreshSettlementAsync(refreshTarget, cancellationToken).WaitAsync(remaining, cancellationToken);
                    if (collectTiming)
                    {
                        waitMs += Stopwatch.GetElapsedTime(waitStartTimestamp).TotalMilliseconds;
                    }
                }
                catch (TimeoutException)
                {
                    if (collectTiming)
                    {
                        waitMs += Stopwatch.GetElapsedTime(waitStartTimestamp).TotalMilliseconds;
                    }

                    return new SessionCreatedMixerRefreshResolution(SessionCreatedMixerRefreshOutcome.SkippedRefreshInProgress, waitCount, waitMs);
                }
            }
        }

        private static bool ShouldLogDetailedRefreshEvents(string signalLabel)
        {
            return !string.Equals(signalLabel, "Show-window", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves whether a debounced session-created refresh should execute after visibility, shutdown, and current
        /// refresh state are rechecked.
        /// </summary>
        internal static SessionCreatedMixerRefreshOutcome ResolveSessionCreatedRefreshOutcome(
            bool isWindowVisible,
            bool isCleaningUp,
            bool isRefreshInProgress)
        {
            if (!AppMixerRefreshGuardHelper.ShouldRefreshForNewSession(isWindowVisible, isCleaningUp))
            {
                return SessionCreatedMixerRefreshOutcome.SkippedWindowHidden;
            }

            return isRefreshInProgress
                ? SessionCreatedMixerRefreshOutcome.SkippedRefreshInProgress
                : SessionCreatedMixerRefreshOutcome.Refreshed;
        }

    }
}
