using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal static List<AudioRoutine> GetAudioPilotStartupTriggeredRoutinesForExecution(IEnumerable<AudioRoutine> routines)
        {
            ArgumentNullException.ThrowIfNull(routines);

            return
            [
                .. routines.Where(static routine => routine.Enabled && routine.HasExecutionTarget)
                    .SelectMany(static routine => routine.ExpandAutomaticTriggers(static trigger => trigger.Kind == RoutineTriggerKind.AudioPilotStartup)).DistinctBy(static routine => routine.Id, StringComparer.OrdinalIgnoreCase)
            ];
        }

        internal async Task ExecuteAudioPilotStartupRoutinesAsync(bool showOverlay)
        {
            List<AudioRoutine> routines = GetAudioPilotStartupTriggeredRoutinesForExecution(GetPersistedRoutineSnapshot());
            foreach (AudioRoutine routine in routines)
            {
                try
                {
                    await ExecuteRoutineForResolvedProcessAsync(routine, 0, showOverlay, "audiopilot-startup", trackLifetime: false, cancellationToken: ShutdownToken);
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", "Error executing AudioPilot startup routine", nameof(ExecuteAudioPilotStartupRoutinesAsync), ex);
                }
            }
        }
    }
}
