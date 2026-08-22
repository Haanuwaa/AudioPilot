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
                .. routines.Where(static routine =>
                    routine.Enabled &&
                    routine.TriggerKind == RoutineTriggerKind.AudioPilotStartup &&
                    routine.HasExecutionTarget)
            ];
        }

        internal async Task ExecuteAudioPilotStartupRoutinesAsync(bool showOverlay)
        {
            List<AudioRoutine> routines = GetAudioPilotStartupTriggeredRoutinesForExecution(GetPersistedRoutineSnapshot());
            foreach (AudioRoutine routine in routines)
            {
                try
                {
                    await ExecuteRoutineAsync(routine, showOverlay, executionSource: "audiopilot-startup");
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", "Error executing AudioPilot startup routine", nameof(ExecuteAudioPilotStartupRoutinesAsync), ex);
                }
            }
        }
    }
}
