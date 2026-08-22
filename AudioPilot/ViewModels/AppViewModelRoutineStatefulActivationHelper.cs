using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal readonly record struct RoutineStatefulActivationExecutionResult(
        AppViewModel.RoutineExecutionResult Result,
        AppViewModel.RoutineAudioRestoreSnapshot? RestoreSnapshot)
    {
        public bool HasRestoreSnapshot => RestoreSnapshot.HasValue;
    }

    internal static class AppViewModelRoutineStatefulActivationHelper
    {
        public static async Task<RoutineStatefulActivationExecutionResult> ExecuteAsync(
            AudioRoutine routine,
            int? rootProcessId,
            bool showOverlay,
            string executionSource,
            Logger logger,
            Func<AudioRoutine, AppViewModel.RoutineAudioRestoreSnapshot?> captureRestoreSnapshot,
            Func<AudioRoutine, bool, int?, string, Task<AppViewModel.RoutineExecutionResult>> executeRoutineAsync,
            Action<AudioRoutine, int?, AppViewModel.RoutineAudioRestoreSnapshot?> registerRoutineStatefulSession,
            Func<AudioRoutine, string, bool, int?, string> buildExecutionLogContext,
            Func<AppViewModel.RoutineExecutionResult, string> buildRoutineExecutionResultLogContext)
        {
            ArgumentNullException.ThrowIfNull(routine);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(captureRestoreSnapshot);
            ArgumentNullException.ThrowIfNull(executeRoutineAsync);
            ArgumentNullException.ThrowIfNull(registerRoutineStatefulSession);
            ArgumentNullException.ThrowIfNull(buildExecutionLogContext);
            ArgumentNullException.ThrowIfNull(buildRoutineExecutionResultLogContext);

            AppViewModel.RoutineAudioRestoreSnapshot? restoreSnapshot = captureRestoreSnapshot(routine);

            logger.Info(
                "AppViewModel",
                () => $"routine-execution-resolved-process-started | {buildExecutionLogContext(routine, executionSource, showOverlay, rootProcessId)} hasRestoreSnapshot={restoreSnapshot.HasValue}");

            AppViewModel.RoutineExecutionResult result = await executeRoutineAsync(routine, showOverlay, rootProcessId, executionSource);
            if (result.Success && routine.IsStatefulTrigger)
            {
                registerRoutineStatefulSession(routine, rootProcessId, restoreSnapshot);
            }

            logger.Info(
                "AppViewModel",
                () => $"routine-execution-resolved-process-completed | {buildExecutionLogContext(routine, executionSource, showOverlay, rootProcessId)} {buildRoutineExecutionResultLogContext(result)} hasRestoreSnapshot={restoreSnapshot.HasValue} statefulTrigger={routine.IsStatefulTrigger}");

            return new RoutineStatefulActivationExecutionResult(result, restoreSnapshot);
        }
    }
}
