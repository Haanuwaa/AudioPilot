using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static class CliRoutineExecutionPolicy
    {
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

            if (!RequiresRunningTriggerProcess(routine))
            {
                return true;
            }

            List<RoutineProcessSnapshot> processSnapshots = snapshotProvider.CaptureAll(
                GetCaptureOptionsForTriggerTarget(routine.TriggerAppPath));
            processId = FindRunningProcessId(routine.TriggerAppPath, processSnapshots);
            if (processId is > 0)
            {
                return true;
            }

            string applicationName = GetTriggerApplicationDisplayName(routine.TriggerAppPath);
            errorCode = "routine-trigger-app-not-running";
            errorMessage = $"Routine '{routine.Name}' requires the target application '{applicationName}' to be running.";
            return false;
        }

        public static bool TryResolveManualRunProcessId(
            AudioRoutine routine,
            out int? processId,
            out string? errorCode,
            out string? errorMessage)
        {
            return TryResolveManualRunProcessId(
                routine,
                new RoutineProcessSnapshotProvider(),
                out processId,
                out errorCode,
                out errorMessage);
        }

        public static bool RequiresRunningTriggerProcess(AudioRoutine? routine)
        {
            return routine != null
                && routine.HasApplicationTrigger
                && RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath);
        }

        public static string GetTriggerApplicationDisplayName(string? triggerAppPath)
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
                if (!RoutineTriggerPathHelper.IsPackagedAppMatch(triggerAppPath, snapshot.AppUserModelId) &&
                    !RoutineTriggerPathHelper.IsPackagedAppExecutablePathMatch(triggerAppPath, snapshot.ExecutablePath))
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
