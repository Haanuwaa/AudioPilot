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
    }
}
