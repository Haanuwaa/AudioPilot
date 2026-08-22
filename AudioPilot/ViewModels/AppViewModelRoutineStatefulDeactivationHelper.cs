using AudioPilot.Logging;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelRoutineStatefulDeactivationHelper
    {
        public static async Task ApplyAsync(
            AppViewModel.RoutineStatefulSession? session,
            bool shouldRestore,
            Logger logger,
            Func<AppViewModel.RoutineStatefulSession, Task> restoreAsync,
            Action updateRoutineAppStartMonitorState,
            Action updateSteamBigPictureMonitorState)
        {
            if (session == null)
            {
                return;
            }

            logger.Info(
                "AppViewModel",
                () => $"routine-stateful-session-deactivated | {AppViewModel.BuildRoutineStatefulSessionLogContext(session, shouldRestore)}");

            try
            {
                if (shouldRestore)
                {
                    try
                    {
                        await restoreAsync(session);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(
                            "AppViewModel",
                            () => $"routine-stateful-restore-failed-during-deactivation | {AppViewModel.BuildRoutineStatefulSessionLogContext(session, shouldRestore: true)}",
                            nameof(ApplyAsync),
                            ex);
                    }
                }
            }
            finally
            {
                updateRoutineAppStartMonitorState();
                updateSteamBigPictureMonitorState();
            }
        }
    }
}
