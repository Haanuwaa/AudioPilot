using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelCleanupDrainLogHelper
    {
        public static BackgroundTaskDrainLoggingCallbacks CreateCallbacks(ILogger logger, string cleanupOpId)
        {
            return BackgroundTaskHelper.CreateWarningDrainLoggingCallbacks(
                logger,
                "AppViewModel",
                (pendingAfterInitial, faultedCount, canceledCount) => BuildInitialTimeoutMessage(cleanupOpId, pendingAfterInitial, faultedCount, canceledCount),
                (faultedCount, canceledCount) => BuildCompletedWithinGraceMessage(cleanupOpId, faultedCount, canceledCount),
                (pendingAfterGrace, faultedCount, canceledCount) => BuildForcedTimeoutMessage(cleanupOpId, pendingAfterGrace, faultedCount, canceledCount));
        }

        internal static string BuildInitialTimeoutMessage(string cleanupOpId, int pendingAfterInitial, int faultedCount, int canceledCount)
        {
            return $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupTimeout} | opId={cleanupOpId} stage=initial waitMs={AppConstants.Timing.CleanupWaitMs} pending={pendingAfterInitial} faulted={faultedCount} canceled={canceledCount}";
        }

        internal static string BuildCompletedWithinGraceMessage(string cleanupOpId, int faultedCount, int canceledCount)
        {
            return $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupCompleteAfterGrace} | opId={cleanupOpId} waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs} faulted={faultedCount} canceled={canceledCount}";
        }

        internal static string BuildForcedTimeoutMessage(string cleanupOpId, int pendingAfterGrace, int faultedCount, int canceledCount)
        {
            return $"{AppConstants.Audio.LogEvents.ViewModel.App.CleanupTimeout} | opId={cleanupOpId} stage=forced waitMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs} pending={pendingAfterGrace} faulted={faultedCount} canceled={canceledCount}";
        }
    }
}
