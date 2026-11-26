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

            int displayOrder = 0;
            foreach (AudioRoutine? candidate in persistedRoutines)
            {
                if (candidate == null) continue;
                displayOrder++;
                if (candidate is not { Enabled: true, ShowInTrayMenu: true } ||
                    !string.Equals(candidate.Id, routineId, StringComparison.OrdinalIgnoreCase)) continue;

                routine = candidate.Clone();
                routine.DisplayOrder = displayOrder;
                return true;
            }

            return false;
        }
    }
}
