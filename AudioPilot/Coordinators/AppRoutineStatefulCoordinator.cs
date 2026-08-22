using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Coordinators
{

    internal readonly record struct SteamBigPictureMonitorDecision(
        bool ShouldMonitor,
        bool StartMonitor);

    internal static class AppRoutineStatefulCoordinator
    {
        /// <summary>
        /// Creates the canonical stateful-session record for a routine activation.
        /// </summary>
        /// <remarks>
        /// Session identity is derived from the trigger model so app-start sessions stay bound to the originating root
        /// process while trigger kinds with a single logical activation, such as Steam Big Picture, reuse a stable key.
        /// </remarks>
        internal static RoutineStatefulSession CreateSession(
            AudioRoutine routine,
            int? rootProcessId,
            long activationSequence,
            RoutineAudioRestoreSnapshot? restoreSnapshot,
            RoutineProcessSnapshot? processIdentity = null, RoutineAppOutputLease? routingLease = null)
        {
            ArgumentNullException.ThrowIfNull(routine);

            string routineId = string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.Id;
            string sessionKey = CreateRoutineStatefulSessionKey(routine, rootProcessId);
            return new RoutineStatefulSession(
                sessionKey,
                routineId,
                routine.Name,
                routine.TriggerKind,
                activationSequence,
                routine.RestorePreviousAudioOnDeactivate,
                restoreSnapshot,
                rootProcessId,
                CreateRoutineConfigurationFingerprint(routine),
                processIdentity)
            { RoutingLease = routingLease?.Clone(), TriggerKey = routine.RuntimeTriggerKey, ActionFingerprint = CreateRoutineActionFingerprint(routine) };
        }

        /// <summary>
        /// Returns app-start session keys whose tracked root process no longer appears in the latest process snapshot
        /// set, ordered newest-first so teardown can unwind from the most recent activation.
        /// </summary>
        internal static List<string> GetEndedAppStartSessionKeys(
            IReadOnlyDictionary<string, RoutineStatefulSession> activeSessions,
            IReadOnlyList<RoutineProcessSnapshot> processSnapshots)
        {
            if (activeSessions.Count == 0)
            {
                return [];
            }

            var liveProcesses = new Dictionary<int, RoutineProcessSnapshot>(processSnapshots.Count);
            foreach (RoutineProcessSnapshot snapshot in processSnapshots)
            {
                if (snapshot.ProcessId > 0)
                {
                    liveProcesses[snapshot.ProcessId] = snapshot;
                }
            }

            return
            [
                .. activeSessions.Values
                    .Where(static session => session.TriggerKind == RoutineTriggerKind.Application)
                    .Where(session => session.RootProcessId is > 0 && (!liveProcesses.TryGetValue(session.RootProcessId.Value, out RoutineProcessSnapshot current) ||
                        (session.ProcessIdentity is RoutineProcessSnapshot expected && !RoutineLifetimeService.IsSameProcess(expected, current))))
                    .OrderByDescending(static session => session.ActivationSequence)
                    .Select(static session => session.SessionKey)
            ];
        }

        internal static long GetLatestActivationSequence(
            IEnumerable<RoutineStatefulSession> activeSessions)
        {
            ArgumentNullException.ThrowIfNull(activeSessions);

            long latestActivationSequence = 0;

            foreach (RoutineStatefulSession session in activeSessions)
            {
                if (session.ActivationSequence > latestActivationSequence)
                {
                    latestActivationSequence = session.ActivationSequence;
                }
            }

            return latestActivationSequence;
        }

        /// <summary>
        /// Identifies active stateful sessions whose source routines are no longer valid for their trigger bucket.
        /// </summary>
        /// <remarks>
        /// This lets the caller remove stale sessions after settings edits disable, retarget, or delete routines
        /// without confusing those changes with normal trigger deactivation.
        /// </remarks>
        internal static List<string> GetInvalidRoutineStatefulSessionKeys(
            IReadOnlyDictionary<string, RoutineStatefulSession> activeSessions,
            IReadOnlyList<AudioRoutine> appStartTriggeredRoutines,
            IReadOnlyList<AudioRoutine> steamBigPictureTriggeredRoutines)
        {
            ArgumentNullException.ThrowIfNull(activeSessions);
            ArgumentNullException.ThrowIfNull(appStartTriggeredRoutines);
            ArgumentNullException.ThrowIfNull(steamBigPictureTriggeredRoutines);

            if (activeSessions.Count == 0) return [];

            Dictionary<string, string> validAppStartRoutineFingerprints = appStartTriggeredRoutines
                .Where(static routine => routine.Enabled && routine.TriggerKind == RoutineTriggerKind.Application)
                .GroupBy(
                    static routine => string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.RuntimeTriggerKey,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => CreateRoutineConfigurationFingerprint(group.Last()),
                    StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> validSteamRoutineFingerprints = steamBigPictureTriggeredRoutines
                .Where(static routine => routine.Enabled && routine.TriggerKind == RoutineTriggerKind.SteamBigPicture)
                .GroupBy(
                    static routine => string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.RuntimeTriggerKey,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => CreateRoutineConfigurationFingerprint(group.Last()),
                    StringComparer.OrdinalIgnoreCase);

            return
            [
                .. activeSessions.Values
                    .Where(session => session.TriggerKind switch
                    {
                        RoutineTriggerKind.Application => !IsSessionConfigurationCurrent(session, validAppStartRoutineFingerprints),
                        RoutineTriggerKind.SteamBigPicture => !IsSessionConfigurationCurrent(session, validSteamRoutineFingerprints),
                        _ => true,
                    })
                    .OrderByDescending(static session => session.ActivationSequence)
                    .Select(static session => session.SessionKey)
            ];
        }

        private static bool IsSessionConfigurationCurrent(
            RoutineStatefulSession session,
            Dictionary<string, string> currentFingerprints)
        {
            if (!currentFingerprints.TryGetValue(session.TriggerKey, out string? currentFingerprint))
            {
                return false;
            }

            return string.IsNullOrEmpty(session.RoutineConfigurationFingerprint) ||
                string.Equals(session.RoutineConfigurationFingerprint, currentFingerprint, StringComparison.Ordinal);
        }

        internal static string CreateRoutineActionFingerprint(AudioRoutine routine) => string.Join('',
            routine.OutputDeviceId, routine.OutputDeviceStableId, routine.InputDeviceId, routine.InputDeviceStableId,
            routine.MasterVolumePercent, routine.MicVolumePercent, routine.CommunicationsFingerprint, routine.OutputMuteAction, routine.InputMuteAction, routine.SwitchOutputPerApp, routine.TargetAppPath, routine.Conditions.ConfigurationKey);

        internal static string CreateRoutineConfigurationFingerprint(AudioRoutine routine)
        {
            ArgumentNullException.ThrowIfNull(routine);

            return string.Join(
                '\u001f',
                routine.TriggerKind,
                routine.ApplicationTriggerMode,
                routine.TriggerAppPath?.Trim() ?? string.Empty,
                routine.ApplicationTriggerTitlePattern?.Trim() ?? string.Empty,
                routine.ApplicationTriggerTitleMatchMode,
                routine.OutputDeviceId?.Trim() ?? string.Empty,
                routine.OutputDeviceStableId?.Trim() ?? string.Empty,
                routine.InputDeviceId?.Trim() ?? string.Empty,
                routine.InputDeviceStableId?.Trim() ?? string.Empty,
                routine.MasterVolumePercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                routine.MicVolumePercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                routine.CommunicationsFingerprint, routine.OutputMuteAction, routine.InputMuteAction,
                routine.SwitchOutputPerApp,
                routine.TargetAppPath,
                routine.RestorePreviousAudioOnDeactivate, routine.Conditions.ConfigurationKey);
        }

        /// <summary>
        /// Decides whether Steam Big Picture monitoring should be active based on watched routines, active sessions,
        /// and the current cleanup state.
        /// </summary>
        internal static SteamBigPictureMonitorDecision ResolveSteamBigPictureMonitorDecision(
            bool monitoringEnabled,
            bool isCleaningUp,
            int watchedRoutineCount,
            bool hasActiveSteamBigPictureSessions,
            bool monitorRunning)
        {
            bool shouldMonitor = monitoringEnabled &&
                !isCleaningUp &&
                (watchedRoutineCount > 0 || hasActiveSteamBigPictureSessions);

            return new SteamBigPictureMonitorDecision(
                shouldMonitor,
                shouldMonitor && !monitorRunning);
        }

        /// <summary>
        /// Builds the canonical stateful-session key for a routine trigger so single-activation triggers remain stable
        /// while app-start triggers stay tied to the originating root process.
        /// </summary>
        internal static string CreateRoutineStatefulSessionKey(AudioRoutine routine, int? rootProcessId)
        {
            ArgumentNullException.ThrowIfNull(routine);

            string routineId = string.IsNullOrWhiteSpace(routine.Id) ? "unknown" : routine.RuntimeTriggerKey;
            return routine.TriggerKind switch
            {
                RoutineTriggerKind.Application => routine.ApplicationTriggerMode switch
                {
                    ApplicationTriggerMode.ProcessFocus => $"process-focus:{routineId}:{rootProcessId ?? 0}",
                    _ => $"application-launch:{routineId}:{rootProcessId ?? 0}",
                },
                RoutineTriggerKind.SteamBigPicture => $"steam-big-picture:{routineId}",
                _ => $"routine:{routineId}",
            };
        }
    }
}
