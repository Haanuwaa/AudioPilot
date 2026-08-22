using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal static class AudioDeviceServiceLifecycle
    {
        public static async Task<bool> DrainBackgroundTasksAsync(Task[] pendingTasks, Logger logger)
        {
            return await BackgroundTaskHelper.DrainWithGraceAndLoggingAsync(
                pendingTasks,
                AppConstants.Timing.CleanupWaitMs,
                AppConstants.Timing.CleanupGraceExtensionMs,
                BackgroundTaskHelper.CreateWarningDrainLoggingCallbacks(
                    logger,
                    "AudioDeviceService",
                    (pendingAfterInitial, faultedCount, canceledCount) => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeTimeout} | stage=initial waitMs={AppConstants.Timing.CleanupWaitMs} pending={pendingAfterInitial} faulted={faultedCount} canceled={canceledCount}",
                    (faultedCount, canceledCount) => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeCompleteAfterGrace} | waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs} faulted={faultedCount} canceled={canceledCount}",
                    (pendingAfterGrace, faultedCount, canceledCount) => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeTimeout} | stage=forced waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs} pending={pendingAfterGrace} faulted={faultedCount} canceled={canceledCount}"));
        }

        internal static void DisposeOwnedResource(Logger logger, Action dispose, string resourceName)
        {
            try
            {
                dispose();
            }
            catch (Exception ex)
            {
                logger.Warning(
                    "AudioDeviceService",
                    () => $"audio-dispose-resource-failed | resource={resourceName}",
                    nameof(AudioDeviceService.DisposeAsync),
                    ex);
            }
        }
    }
}
