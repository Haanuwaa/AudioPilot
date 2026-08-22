using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct MinimizeWindowPlan(
        MinimizeAttemptResult AttemptResult,
        bool ShowBalloon,
        bool ConsumeFirstRunBalloon,
        bool ConsumeSaveBalloon);

    internal static class AppWindowVisibilityCoordinator
    {
        public static async Task<bool> ToggleWindowVisibilityAsync(
            bool isWindowVisible,
            Func<Task<bool>> showWindowAsync,
            Action minimizeWindow,
            ILogger logger)
        {
            if (isWindowVisible)
            {
                logger.Debug("AppViewModel", "window-visibility-toggle | action=hide");
                minimizeWindow();
                return true;
            }

            logger.Debug("AppViewModel", "window-visibility-toggle | action=show");
            return await showWindowAsync();
        }

        public static async Task<bool> ShowWindowAsync(
            AppWindowStateCoordinator windowState,
            Func<Task<bool>> showWindowFrontAndCenterAsync,
            Func<Task> refreshAvailableDeviceCollectionsAsync,
            Action refreshDeviceCache,
            Func<Task> refreshMixerAsync,
            Func<Task> updateMuteFlagsAsync,
            ILogger logger,
            DateTime now)
        {
            windowState.RequestInteractiveShow();
            if (!windowState.IsStartupVisibilityResolved)
            {
                logger.Debug("AppViewModel", "window-show-deferred | reason=startup-visibility-pending");
                return false;
            }

            if (!await showWindowFrontAndCenterAsync())
            {
                logger.Warning("AppViewModel", "window-show-failed | reason=shell-transition-failed");
                return false;
            }

            windowState.MarkShown(now);
            refreshDeviceCache();
            _ = ObserveShowRefreshAsync(refreshAvailableDeviceCollectionsAsync, refreshMixerAsync, updateMuteFlagsAsync, logger);
            return true;
        }

        internal static async Task ObserveShowRefreshAsync(
            Func<Task> refreshAvailableDeviceCollectionsAsync,
            Func<Task> refreshMixerAsync,
            Func<Task> updateMuteFlagsAsync,
            ILogger logger)
        {
            try
            {
                Task deviceRefreshTask = InvokeRefreshAsync(refreshAvailableDeviceCollectionsAsync);
                Task mixerRefreshTask = InvokeRefreshAsync(refreshMixerAsync);
                Task muteRefreshTask = InvokeRefreshAsync(updateMuteFlagsAsync);
                await Task.WhenAll(deviceRefreshTask, mixerRefreshTask, muteRefreshTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.Debug("AppViewModel", "window-show-refresh-cancelled");
            }
            catch (Exception ex)
            {
                logger.Error("AppViewModel", "window-show-refresh-failed", nameof(ShowWindowAsync), ex);
            }
        }

        private static async Task InvokeRefreshAsync(Func<Task> refreshAsync)
        {
            await refreshAsync().ConfigureAwait(false);
        }

        public static bool StartHiddenToTray(Func<bool> startHiddenToTray, ILogger logger)
        {
            bool succeeded = startHiddenToTray();
            logger.Debug("AppViewModel", () => $"window-start-hidden-to-tray | result={(succeeded ? "success" : "failed")}");
            return succeeded;
        }

        public static MinimizeWindowPlan BuildMinimizePlan(
            AppWindowStateCoordinator windowState,
            bool showBalloonAfterSave,
            DateTime now)
        {
            MinimizeAttemptResult minimizeResult = windowState.TryBeginMinimize(now);
            bool showBalloonOnFirstRun = windowState.ShowBalloonOnFirstMinimize;
            bool showBalloon = showBalloonOnFirstRun || showBalloonAfterSave;

            return new MinimizeWindowPlan(
                minimizeResult,
                showBalloon,
                ConsumeFirstRunBalloon: minimizeResult == MinimizeAttemptResult.Started && showBalloonOnFirstRun,
                ConsumeSaveBalloon: minimizeResult == MinimizeAttemptResult.Started && showBalloonAfterSave);
        }

        public static void ApplyMinimizePlan(
            AppWindowStateCoordinator windowState,
            MinimizeWindowPlan plan,
            Func<bool, string, bool> minimizeToTray,
            Action clearSaveBalloon,
            ILogger logger)
        {
            if (plan.AttemptResult == MinimizeAttemptResult.Cooldown)
            {
                logger.Debug("AppViewModel", "minimize-to-tray-skipped | reason=show-cooldown");
                return;
            }

            if (plan.AttemptResult == MinimizeAttemptResult.AlreadyMinimizing)
            {
                logger.Debug("AppViewModel", "minimize-to-tray-skipped | reason=already-minimizing");
                return;
            }

            logger.Debug("AppViewModel", "minimize-to-tray-start");

            bool succeeded = minimizeToTray(plan.ShowBalloon, AppConstants.Identity.DisplayName);
            if (!succeeded)
            {
                windowState.AbortMinimize();
                logger.Warning("AppViewModel", "minimize-to-tray-failed | state=aborted");
                return;
            }

            logger.Debug("AppViewModel", "minimize-to-tray-hidden");
            windowState.CompleteMinimize();

            if (plan.ConsumeFirstRunBalloon)
            {
                windowState.ShowBalloonOnFirstMinimize = false;
            }

            if (plan.ConsumeSaveBalloon)
            {
                clearSaveBalloon();
            }
        }
    }
}
