using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal readonly record struct ExternalReloadDependencies(
        Action CacheSettings,
        Action ApplyLogLevel,
        Action ApplyAdvancedTuning,
        Action LoadOutputDevices,
        Action<IReadOnlyList<CycleDevice>?> ApplyOutputCycle,
        Action LoadInputDevices,
        Action<IReadOnlyList<CycleDevice>?> ApplyInputCycle,
        Action<IEnumerable<AudioRoutine>?> ApplyRoutines,
        Action LoadOutputHotkey,
        Action LoadOutputReverseHotkey,
        Action LoadInputHotkey,
        Action LoadInputReverseHotkey,
        Action ApplyOutputHotkeysEnabled,
        Action ApplyInputHotkeysEnabled,
        Action ApplyTheme,
        Action ApplyOverlayPosition,
        Action ApplyOverlayDurationText,
        Func<HotkeyRegistrationResult> RegisterHotkeys,
        Action<HotkeyRegistrationResult> LogHotkeyResults,
        Action RegisterRoutineHotkeys,
        Action ApplyRunAtStartup,
        Action ApplyPreserveAudioLevels,
        Action ApplyOverlayEnabled,
        Action LogSettingsApply,
        Action ApplyOverlayDisplaySettings,
        Action UpdateAudioConfiguration,
        Action SyncSettingsDrafts);

    internal static class AppExternalReloadCoordinator
    {
        public static void Apply(Settings newSettings, ExternalReloadDependencies dependencies)
        {
            dependencies.CacheSettings();
            dependencies.ApplyLogLevel();
            dependencies.ApplyAdvancedTuning();

            dependencies.LoadOutputDevices();
            dependencies.ApplyOutputCycle(newSettings.DeviceSwitching.Output.CycleDevices);
            dependencies.LoadInputDevices();
            dependencies.ApplyInputCycle(newSettings.DeviceSwitching.Input.CycleDevices);
            dependencies.ApplyRoutines(newSettings.Routines.Items);

            dependencies.LoadOutputHotkey();
            dependencies.LoadOutputReverseHotkey();
            dependencies.LoadInputHotkey();
            dependencies.LoadInputReverseHotkey();
            dependencies.ApplyOutputHotkeysEnabled();
            dependencies.ApplyInputHotkeysEnabled();

            dependencies.ApplyTheme();
            dependencies.ApplyOverlayPosition();
            dependencies.ApplyOverlayDurationText();

            HotkeyRegistrationResult registrationResult = dependencies.RegisterHotkeys();
            dependencies.LogHotkeyResults(registrationResult);
            dependencies.RegisterRoutineHotkeys();

            dependencies.ApplyRunAtStartup();
            dependencies.ApplyPreserveAudioLevels();
            dependencies.ApplyOverlayEnabled();
            dependencies.LogSettingsApply();
            dependencies.ApplyOverlayDisplaySettings();
            dependencies.UpdateAudioConfiguration();
            dependencies.SyncSettingsDrafts();
        }
    }
}
