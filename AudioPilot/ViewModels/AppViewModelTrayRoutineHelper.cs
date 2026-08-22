using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelTrayRoutineHelper
    {
        public static bool TryResolveTrayRoutine(
            string routineId,
            IReadOnlyList<AudioRoutine> persistedRoutines,
            out AudioRoutine routine)
        {
            routine = null!;
            if (string.IsNullOrWhiteSpace(routineId))
            {
                return false;
            }

            AudioRoutine? matchedRoutine = persistedRoutines.FirstOrDefault(candidate =>
                candidate.Enabled &&
                candidate.ShowInTrayMenu &&
                string.Equals(candidate.Id, routineId, StringComparison.OrdinalIgnoreCase));

            if (matchedRoutine == null)
            {
                return false;
            }

            routine = matchedRoutine;
            return true;
        }

        public static bool ShouldResolveRunningTriggerProcess(AudioRoutine routine)
        {
            ArgumentNullException.ThrowIfNull(routine);
            return routine.HasApplicationTrigger && RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath);
        }

        public static string GetMissingTriggerApplicationDisplayName(AudioRoutine routine)
        {
            ArgumentNullException.ThrowIfNull(routine);
            return RoutineTriggerPathHelper.GetTriggerDisplayName(routine.TriggerAppPath);
        }
    }
}
