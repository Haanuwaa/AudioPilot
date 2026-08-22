using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal enum RefreshWorkflowOutcome
    {
        Continue,
        AbortSettingsReloadNull,
    }

    internal static class AppRefreshExecutionCoordinator
    {
        /// <summary>
        /// Runs the collection refresh step and, when external settings changed, reloads and reapplies the persisted
        /// settings before allowing the broader refresh workflow to continue.
        /// </summary>
        internal static async Task<RefreshWorkflowOutcome> RefreshCollectionsAndApplySettingsAsync(
            bool settingsChanged,
            bool refreshDeviceCollections,
            Func<Task> refreshAvailableDeviceCollectionsAsync,
            Func<Task<Settings?>> loadSettingsAsync,
            Action<Settings> applyExternallyReloadedSettings,
            string refreshOpId,
            Logger logger)
        {
            if (settingsChanged)
            {
                logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.RefreshSettingsReload} | opId={refreshOpId} reason=external-change");
                Settings? newSettings = await loadSettingsAsync();

                if (newSettings == null)
                {
                    logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.RefreshFailed} | opId={refreshOpId} reason=settings-reload-null");
                    return RefreshWorkflowOutcome.AbortSettingsReloadNull;
                }

                await refreshAvailableDeviceCollectionsAsync();
                applyExternallyReloadedSettings(newSettings);
                return RefreshWorkflowOutcome.Continue;
            }

            if (refreshDeviceCollections)
            {
                await refreshAvailableDeviceCollectionsAsync();
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("AppViewModel", () => $"refresh-device-collections-deferred | opId={refreshOpId} reason=window-hidden");
            }

            return RefreshWorkflowOutcome.Continue;
        }

        internal static bool ShouldRefreshDeviceCollections(
            bool settingsChanged,
            bool refreshMixerWhenWindowHidden,
            bool isWindowVisible)
        {
            return settingsChanged || refreshMixerWhenWindowHidden || isWindowVisible;
        }

        /// <summary>
        /// Decides whether an external settings change should proceed when local unsaved edits may be overwritten.
        /// </summary>
        internal static async Task<bool> ResolveSettingsChangedForRefreshAsync(
            bool settingsChanged,
            bool promptOnPotentialOverwrite,
            bool hasPendingLocalEdits,
            Func<Task<AppDialogResult>> promptOverwrite,
            string refreshOpId,
            Logger logger)
        {
            if (!settingsChanged || !promptOnPotentialOverwrite || !hasPendingLocalEdits)
            {
                return settingsChanged;
            }

            AppDialogResult overwriteResult = await promptOverwrite();
            if (overwriteResult == AppDialogResult.Confirmed)
            {
                return true;
            }

            logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RefreshSettingsReloadSkip} | opId={refreshOpId} reason=user-kept-local-edits");
            return false;
        }

        /// <summary>
        /// Restores cached settings into the UI when no external file change occurred but the editable state has
        /// drifted and the user opted to discard those unsaved edits.
        /// </summary>
        internal static async Task ApplyCachedSettingsIfUiDriftedAsync(
            bool settingsChanged,
            bool promptOnPotentialOverwrite,
            bool hasUiSettingsDivergedFromCachedSettings,
            Func<Task<AppDialogResult>> promptReloadCachedSettings,
            Func<Settings?> getCachedSettingsSnapshot,
            Action<Settings> applyExternallyReloadedSettings,
            string refreshOpId,
            Logger logger)
        {
            if (settingsChanged || !hasUiSettingsDivergedFromCachedSettings)
            {
                return;
            }

            logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.RefreshSettingsReload} | opId={refreshOpId} reason=ui-state-drift");

            bool reloadCachedSettings = promptOnPotentialOverwrite && await promptReloadCachedSettings() == AppDialogResult.Confirmed;
            if (!reloadCachedSettings)
            {
                logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RefreshSettingsReloadSkip} | opId={refreshOpId} reason=unsaved-edits-preserved");
                return;
            }

            Settings? cachedSettingsSnapshot = getCachedSettingsSnapshot();
            if (cachedSettingsSnapshot != null)
            {
                applyExternallyReloadedSettings(cachedSettingsSnapshot);
            }
        }

        /// <summary>
        /// Applies the post-refresh side effects, deferring non-critical work when the window is hidden unless the
        /// caller explicitly requested hidden-window mixer refresh behavior.
        /// </summary>
        internal static async Task ApplyPostRefreshEffectsAsync(
            bool refreshMixerWhenWindowHidden,
            bool isWindowVisible,
            bool isCleaningUp,
            Action generateDeviceReferenceFile,
            Action refreshDeviceCache,
            Func<Task> updateMuteFlagsFromSystemAsync,
            Func<bool, Task> refreshMixerAsync,
            int outputCycleCount,
            string refreshOpId,
            Logger logger)
        {
            generateDeviceReferenceFile();

            bool deferNonCriticalHotplugPostRefresh = ShouldDeferNonCriticalHotplugPostRefresh(
                refreshMixerWhenWindowHidden,
                isWindowVisible);

            if (!deferNonCriticalHotplugPostRefresh)
            {
                refreshDeviceCache();
                await updateMuteFlagsFromSystemAsync();
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.HotplugPostRefreshDeferred} | opId={refreshOpId} actions=device-cache,mute-flags reason=window-hidden");
            }

            bool shouldRefreshMixer = refreshMixerWhenWindowHidden || AppMixerRefreshGuardHelper.ShouldRefreshForHotplug(isWindowVisible, isCleaningUp);
            if (shouldRefreshMixer)
            {
                await refreshMixerAsync(isWindowVisible);
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.HotplugMixerRefreshDeferred} | opId={refreshOpId} reason=window-hidden");
            }

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.Trace("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RefreshState} | opId={refreshOpId} outputCycleCount={outputCycleCount}");
            }
        }

        /// <summary>
        /// Returns whether non-critical post-refresh work should stay deferred while the window is hidden.
        /// </summary>
        internal static bool ShouldDeferNonCriticalHotplugPostRefresh(
            bool refreshMixerWhenWindowHidden,
            bool isWindowVisible)
        {
            return !refreshMixerWhenWindowHidden && !isWindowVisible;
        }
    }
}
