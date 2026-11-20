using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Cli
{
    public static class CliRoutineExecutionPolicy
    {
        internal const string TargetAppUnavailableCode = "routine-target-app-unavailable";
        internal static bool TryResolveManualRunProcessId(
            AudioRoutine routine,
            IRoutineProcessSnapshotProvider snapshotProvider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage)
        {
            ArgumentNullException.ThrowIfNull(snapshotProvider);

            processId = null;
            errorCode = null;
            errorMessage = null;

            if (!RequiresRunningTargetApplication(routine))
            {
                return true;
            }

            List<RoutineProcessSnapshot> processSnapshots = snapshotProvider.CaptureAll(
                GetCaptureOptionsForTriggerTarget(routine.TargetAppPath));
            processId = FindRunningProcessId(routine.TargetAppPath, processSnapshots);
            if (processId is > 0)
            {
                return true;
            }

            string applicationName = GetApplicationDisplayName(routine.TargetAppPath);
            bool unknown = RoutineApplicationRouting.GetApplicationPresence(routine.TargetAppPath, processSnapshots) == null;
            errorCode = unknown ? TargetAppUnavailableCode : "routine-target-app-not-running";
            errorMessage = unknown ? $"The running state of target application '{applicationName}' could not be determined."
                : $"Routine '{routine.Name}' requires the target application '{applicationName}' to be running.";
            return false;
        }

        public static bool RequiresRunningTargetApplication(AudioRoutine? routine)
        {
            return routine != null
                && routine.RouteToApplication;
        }

        public static string GetApplicationDisplayName(string? triggerAppPath)
        {
            return RoutineTriggerPathHelper.GetTriggerDisplayName(triggerAppPath);
        }

        internal static RoutineProcessSnapshotCaptureOptions GetCaptureOptionsForTriggerTarget(string? triggerAppPath)
        {
            return RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerAppPath)
                ? RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId
                : RoutineProcessSnapshotCaptureOptions.None;
        }

        private static int? FindRunningProcessId(string? triggerAppPath, IReadOnlyList<RoutineProcessSnapshot> processSnapshots)
        {
            if (RoutineTriggerPathHelper.LooksLikeExecutablePath(triggerAppPath))
            {
                return FindRunningProcessIdByExecutablePath(triggerAppPath, processSnapshots);
            }

            if (RoutineTriggerPathHelper.LooksLikePackagedAppId(triggerAppPath))
            {
                return FindRunningProcessIdByPackagedAppId(triggerAppPath, processSnapshots);
            }

            return null;
        }

        private static int? FindRunningProcessIdByExecutablePath(string? triggerAppPath, IReadOnlyList<RoutineProcessSnapshot> processSnapshots)
        {
            string normalizedTriggerPath = RoutineTriggerPathHelper.NormalizeExecutablePath(triggerAppPath);
            int? matchedProcessId = null;
            foreach (RoutineProcessSnapshot snapshot in processSnapshots)
            {
                if (!RoutineTriggerPathHelper.IsExecutablePathMatch(snapshot.ExecutablePath, normalizedTriggerPath))
                {
                    continue;
                }

                if (matchedProcessId == null || snapshot.ProcessId < matchedProcessId.Value)
                {
                    matchedProcessId = snapshot.ProcessId;
                }
            }

            return matchedProcessId;
        }

        private static int? FindRunningProcessIdByPackagedAppId(string? triggerAppPath, IReadOnlyList<RoutineProcessSnapshot> processSnapshots)
        {
            int? matchedProcessId = null;
            foreach (RoutineProcessSnapshot snapshot in processSnapshots)
            {
                if (!RoutineTriggerPathHelper.IsPackagedAppMatch(triggerAppPath, snapshot.AppUserModelId, snapshot.ExecutablePath))
                {
                    continue;
                }

                if (matchedProcessId == null || snapshot.ProcessId < matchedProcessId.Value)
                {
                    matchedProcessId = snapshot.ProcessId;
                }
            }

            return matchedProcessId;
        }
    }
}
