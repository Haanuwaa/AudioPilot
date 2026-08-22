using AudioPilot.Logging;
using AudioPilot.Services.Routines;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelRoutineStatefulDeactivationHelper
    {
        public static async Task ApplyAsync(
            RoutineStatefulSession? session,
            bool shouldRestore,
            Logger logger,
            Func<RoutineStatefulSession, Task> restoreAsync,
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
