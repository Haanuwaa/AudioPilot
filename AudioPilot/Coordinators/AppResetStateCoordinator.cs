using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal readonly record struct ResetDefaultState(
        bool RunAtStartup,
        bool PreserveAudioLevels,
        bool OverlayEnabled,
        OverlayPosition OverlayPosition,
        string OverlayDurationSecondsText,
        bool OutputHotkeysEnabled,
        bool InputHotkeysEnabled,
        bool ShowBalloonOnFirstMinimize,
        bool ShowBalloonAfterSave,
        AppTheme Theme);

    internal readonly record struct ResetPersistedConfigurationDependencies(
        Action UnregisterUserHotkey,
        Action UnregisterAllHotkeys,
        Action RemoveFromStartup,
        Action DeleteSettingsFiles,
        Action ClearCachedSettings,
        Action ClearRoutines,
        Action<Settings> UpdateAudioConfiguration,
        Action<ResetDefaultState> ApplyDefaultState,
        Action<bool, OverlayPosition, double> UpdateOverlay,
        Action SyncSettingsDrafts);

    internal readonly record struct ResetUiSelectionDependencies(
        Action ClearOutputSelections,
        Action LoadOutputDevices,
        Action ResetOutputHotkeys,
        Action ClearInputSelections,
        Action LoadInputDevices,
        Func<Task> RefreshMixerAsync);

    internal static class AppResetStateCoordinator
    {
        public static ResetDefaultState BuildDefaultState()
        {
            return new ResetDefaultState(
                RunAtStartup: false,
                PreserveAudioLevels: true,
                OverlayEnabled: true,
                OverlayPosition: OverlayPosition.BottomRight,
                OverlayDurationSecondsText: AppConstants.Timing.OverlayAutoHideSeconds.ToString("0.0"),
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                ShowBalloonOnFirstMinimize: true,
                ShowBalloonAfterSave: false,
                Theme: AppTheme.System);
        }

        public static void ResetPersistedConfiguration(ResetPersistedConfigurationDependencies dependencies, ILogger logger)
        {
            dependencies.UnregisterUserHotkey();
            dependencies.UnregisterAllHotkeys();
            logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.ResetStepComplete} | step=hotkeys-unregistered");

            dependencies.RemoveFromStartup();
            logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.ResetStepComplete} | step=startup-removed");

            dependencies.DeleteSettingsFiles();
            logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.ResetStepComplete} | step=settings-deleted");

            dependencies.ClearCachedSettings();
            dependencies.ClearRoutines();

            Settings defaultSettings = new();
            dependencies.UpdateAudioConfiguration(defaultSettings);

            ResetDefaultState defaultState = BuildDefaultState();
            dependencies.ApplyDefaultState(defaultState);
            dependencies.UpdateOverlay(
                defaultState.OverlayEnabled,
                defaultState.OverlayPosition,
                AppConstants.Timing.OverlayAutoHideSeconds);
            dependencies.SyncSettingsDrafts();
        }

        public static async Task ResetUiSelectionsAsync(ResetUiSelectionDependencies dependencies)
        {
            dependencies.ClearOutputSelections();
            dependencies.LoadOutputDevices();
            dependencies.ResetOutputHotkeys();
            dependencies.ClearInputSelections();
            dependencies.LoadInputDevices();
            await dependencies.RefreshMixerAsync();
        }
    }
}
