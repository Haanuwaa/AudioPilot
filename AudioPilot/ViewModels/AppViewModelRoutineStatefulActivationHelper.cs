using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.ViewModels
{
    internal readonly record struct RoutineStatefulActivationExecutionResult(
        RoutineExecutionResult Result,
        RoutineAudioRestoreSnapshot? RestoreSnapshot)
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
            Func<AudioRoutine, RoutineAudioRestoreSnapshot?> captureRestoreSnapshot,
            Func<AudioRoutine, bool, int?, string, Task<RoutineExecutionResult>> executeRoutineAsync,
            Action<AudioRoutine, int?, RoutineAudioRestoreSnapshot?> registerRoutineStatefulSession,
            Func<AudioRoutine, string, bool, int?, string> buildExecutionLogContext,
            Func<RoutineExecutionResult, string> buildRoutineExecutionResultLogContext,
            bool trackLifetime = true,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(routine);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(captureRestoreSnapshot);
            ArgumentNullException.ThrowIfNull(executeRoutineAsync);
            ArgumentNullException.ThrowIfNull(registerRoutineStatefulSession);
            ArgumentNullException.ThrowIfNull(buildExecutionLogContext);
            ArgumentNullException.ThrowIfNull(buildRoutineExecutionResultLogContext);

            cancellationToken.ThrowIfCancellationRequested();
            // Snapshot reads can synchronously wait for device/COM work. Keep presentation callbacks on the caller.
            RoutineAudioRestoreSnapshot? restoreSnapshot = trackLifetime
                ? await Task.Run(() => captureRestoreSnapshot(routine), cancellationToken) : null;
            cancellationToken.ThrowIfCancellationRequested();

            logger.Info(
                "AppViewModel",
                () => $"routine-execution-resolved-process-started | {buildExecutionLogContext(routine, executionSource, showOverlay, rootProcessId)} hasRestoreSnapshot={restoreSnapshot.HasValue}");

            RoutineExecutionResult result = await executeRoutineAsync(routine, showOverlay, rootProcessId, executionSource);
            if (trackLifetime && (result.Success || result.HasPartialSuccess || result.HasPerAppRoutingContinuation) && routine.IsStatefulTrigger)
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
