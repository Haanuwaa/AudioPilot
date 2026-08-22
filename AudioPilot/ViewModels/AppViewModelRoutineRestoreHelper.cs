using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal readonly record struct AppViewModelRoutineRestoreDependencies(
        Func<string, string, CycleDevice?> TryGetActivePlaybackCycleEntry,
        Func<string?> GetDefaultPlaybackDeviceId,
        Func<string, string, string, Task> SwitchOutputAsync,
        Func<string, string, CycleDevice?> TryGetActiveRecordingCycleEntry,
        Func<string, string, string, Task> SwitchInputAsync,
        Func<float, bool, string, Task>? RestoreOutputVolumeAsync = null,
        Func<float, bool, string, Task>? RestoreInputVolumeAsync = null);

    internal static class AppViewModelRoutineRestoreHelper
    {
        public static Task<RoutineRestoreResult> RestoreAsync(
            AppViewModel.RoutineStatefulSession session,
            AppViewModelRoutineRestoreDependencies dependencies,
            Logger logger)
        {
            return AppRoutineRestoreCoordinator.ExecuteRestoreAsync(
                session,
                BuildDependencies(session, dependencies),
                logger);
        }

        private static RoutineRestoreDependencies BuildDependencies(
            AppViewModel.RoutineStatefulSession session,
            AppViewModelRoutineRestoreDependencies dependencies)
        {
            if (!session.RestoreSnapshot.HasValue)
            {
                return default;
            }

            return new RoutineRestoreDependencies(
                dependencies.TryGetActivePlaybackCycleEntry,
                dependencies.GetDefaultPlaybackDeviceId,
                dependencies.SwitchOutputAsync,
                dependencies.TryGetActiveRecordingCycleEntry,
                dependencies.SwitchInputAsync,
                dependencies.RestoreOutputVolumeAsync,
                dependencies.RestoreInputVolumeAsync);
        }
    }
}
