using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal enum SteamBigPictureStateChangeAction
    {
        None,
        Activate,
        Deactivate,
    }

    internal readonly record struct SteamBigPictureLoopContext(
        bool ShouldMonitor,
        IReadOnlyList<AudioRoutine> WatchedRoutines);

    internal readonly record struct SteamBigPictureStateChangeResult(
        bool NextDetectedState,
        bool StateChanged,
        SteamBigPictureStateChangeAction Action,
        IReadOnlyList<AudioRoutine> ActivationRoutines);

    internal readonly record struct SteamBigPictureSignalEvaluationDecision(
        SteamBigPictureStateChangeResult StateChange,
        bool ShouldQueueConfirmationCheck);

    internal static class AppSteamBigPictureCoordinator
    {
        /// <summary>
        /// Builds the current Steam Big Picture monitoring context and clones watched routines only when the
        /// monitor or fallback heartbeat should remain active.
        /// </summary>
        internal static SteamBigPictureLoopContext BuildLoopContext(
            bool monitoringEnabled,
            bool isCleaningUp,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            bool hasActiveSteamBigPictureSessions)
        {
            ArgumentNullException.ThrowIfNull(watchedRoutines);

            bool shouldMonitor = monitoringEnabled &&
                !isCleaningUp &&
                (watchedRoutines.Count > 0 || hasActiveSteamBigPictureSessions);

            if (!shouldMonitor)
            {
                return new SteamBigPictureLoopContext(false, []);
            }

            return new SteamBigPictureLoopContext(
                true,
                [.. watchedRoutines.Select(static routine => routine.Clone())]);
        }

        /// <summary>
        /// Converts the latest Big Picture detection state into a rising-edge activation or falling-edge deactivation
        /// result so callers only react once per state transition.
        /// </summary>
        internal static SteamBigPictureStateChangeResult ResolveStateChange(
            bool previousDetected,
            bool isSteamBigPictureActive,
            IReadOnlyList<AudioRoutine> watchedRoutines)
        {
            ArgumentNullException.ThrowIfNull(watchedRoutines);

            bool stateChanged = previousDetected != isSteamBigPictureActive;
            if (!stateChanged)
            {
                return new SteamBigPictureStateChangeResult(
                    isSteamBigPictureActive,
                    false,
                    SteamBigPictureStateChangeAction.None,
                    []);
            }

            if (!isSteamBigPictureActive)
            {
                return new SteamBigPictureStateChangeResult(
                    false,
                    true,
                    SteamBigPictureStateChangeAction.Deactivate,
                    []);
            }

            return new SteamBigPictureStateChangeResult(
                true,
                true,
                SteamBigPictureStateChangeAction.Activate,
                [..
                    watchedRoutines
                        .Where(static routine => routine.Enabled && routine.HasExecutionTarget)
                        .Select(static routine => routine.Clone())]);
        }

        /// <summary>
        /// Decides whether a signal-triggered evaluation should be followed by one delayed confirmation pass.
        /// </summary>
        /// <remarks>
        /// Big Picture teardown can fire a window event before the visible window disappears. When that happens,
        /// the first recheck still reads "active" and would otherwise miss the falling edge entirely.
        /// </remarks>
        internal static SteamBigPictureSignalEvaluationDecision ResolveSignalEvaluation(
            bool previousDetected,
            bool isSteamBigPictureActive,
            IReadOnlyList<AudioRoutine> watchedRoutines,
            bool allowConfirmationCheck)
        {
            SteamBigPictureStateChangeResult stateChange = ResolveStateChange(
                previousDetected,
                isSteamBigPictureActive,
                watchedRoutines);

            bool shouldQueueConfirmationCheck = allowConfirmationCheck &&
                previousDetected &&
                isSteamBigPictureActive &&
                !stateChange.StateChanged;

            return new SteamBigPictureSignalEvaluationDecision(
                stateChange,
                shouldQueueConfirmationCheck);
        }
    }
}
