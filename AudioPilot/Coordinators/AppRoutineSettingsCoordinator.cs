using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal static class AppRoutineSettingsCoordinator
    {
        internal static Settings BuildSavedRoutineSettings(
            Settings cachedSettings,
            IEnumerable<AudioRoutine> routines,
            IReadOnlyList<CycleDevice> availableOutputDevices,
            IReadOnlyList<CycleDevice> availableInputDevices)
        {
            ArgumentNullException.ThrowIfNull(cachedSettings);

            Settings newSettings = AppSettingsWorkflowCoordinator.CloneSettings(cachedSettings);
            newSettings.Routines.Items = ReconcileRoutines(routines, availableOutputDevices, availableInputDevices);
            return newSettings;
        }

        internal static List<AudioRoutine> ReconcileRoutines(
            IEnumerable<AudioRoutine>? routines,
            IReadOnlyList<CycleDevice> availableOutputDevices,
            IReadOnlyList<CycleDevice> availableInputDevices)
        {
            List<AudioRoutine> reconciled = AppViewModel.CloneRoutines(routines);
            foreach (AudioRoutine routine in reconciled)
            {
                CycleDevice resolvedOutput = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                    new CycleDevice { Id = routine.OutputDeviceId, Name = routine.OutputDeviceName },
                    availableOutputDevices);
                routine.OutputDeviceId = resolvedOutput.Id;
                routine.OutputDeviceName = resolvedOutput.Name;

                CycleDevice resolvedInput = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
                    new CycleDevice { Id = routine.InputDeviceId, Name = routine.InputDeviceName },
                    availableInputDevices);
                routine.InputDeviceId = resolvedInput.Id;
                routine.InputDeviceName = resolvedInput.Name;
            }

            return reconciled;
        }

        internal static void RunSaveSideEffects(
            Settings newSettings,
            Action persistUiState,
            Action<Settings> registerRoutineHotkeys,
            Action<IEnumerable<AudioRoutine>?> applyRoutinesFromSettings)
        {
            persistUiState();
            registerRoutineHotkeys(newSettings);
            applyRoutinesFromSettings(newSettings.Routines?.Items);
        }
    }
}
