namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineProcessSnapshotHelper
{
    internal static IReadOnlyList<int> FindRunningProcessIdsForExecutablePath(
        string normalizedTriggerPath,
        IReadOnlyList<RoutineProcessSnapshot> processSnapshots)
    {
        if (string.IsNullOrWhiteSpace(normalizedTriggerPath))
        {
            return [];
        }

        return
        [
            .. processSnapshots
                .Where(snapshot =>
                    snapshot.ProcessId > 0 &&
                    string.Equals(
                        Helpers.RoutineTriggerPathHelper.NormalizeExecutablePath(snapshot.ExecutablePath),
                        normalizedTriggerPath,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(static snapshot => snapshot.ProcessId)
                .Select(static snapshot => snapshot.ProcessId)
        ];
    }
}
