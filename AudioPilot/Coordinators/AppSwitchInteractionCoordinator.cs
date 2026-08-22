using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal static class AppSwitchInteractionCoordinator
    {
        public static async Task<AppViewModel.ResumeHotkeyRegistrationResult> RegisterResumeHotkeysOnDispatcherAsync(
            Func<Func<AppViewModel.ResumeHotkeyRegistrationResult>, Task<AppViewModel.ResumeHotkeyRegistrationResult>> executeOnDispatcherAsync,
            Func<HotkeyRegistrationResult> registerHotkeys,
            Func<RoutineHotkeyRegistrationResult> registerRoutineHotkeys)
        {
            return await executeOnDispatcherAsync(() =>
            {
                HotkeyRegistrationResult result = registerHotkeys();
                RoutineHotkeyRegistrationResult routineResult = registerRoutineHotkeys();
                return MapResumeHotkeyRegistrationResult(result, !routineResult.HasFailures);
            });
        }

        public static AppViewModel.ResumeHotkeyRegistrationResult MapResumeHotkeyRegistrationResult(HotkeyRegistrationResult result, bool routineHotkeysRegistered = true)
        {
            return new AppViewModel.ResumeHotkeyRegistrationResult(
                result.ToggleAppVisibilityRegistered,
                result.MediaHotkeysRegistered,
                result.MuteHotkeysRegistered,
                result.ListenToInputRegistered,
                result.VolumeStepHotkeysRegistered,
                result.OutputSwitchRegistered,
                result.InputSwitchRegistered,
                result.OutputReverseSwitchRegistered,
                result.InputReverseSwitchRegistered,
                routineHotkeysRegistered);
        }

        public static bool FinalizeSwitch(bool switched, bool output, Action<bool> markSwitchOverlayShown)
        {
            if (!switched)
            {
                return false;
            }

            markSwitchOverlayShown(output);
            return true;
        }
    }
}
