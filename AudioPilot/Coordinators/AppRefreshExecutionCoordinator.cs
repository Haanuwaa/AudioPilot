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
            Logger logger,
            Func<Task<bool>>? authorizeSettingsReloadAsync = null,
            Action? ensureActive = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ensureActive?.Invoke();
            if (settingsChanged)
            {
                logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.RefreshSettingsReload} | opId={refreshOpId} reason=external-change");
                Settings? newSettings = await loadSettingsAsync().WaitAsync(cancellationToken);
                ensureActive?.Invoke();

                if (newSettings == null)
                {
                    logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.RefreshFailed} | opId={refreshOpId} reason=settings-reload-null");
                    return RefreshWorkflowOutcome.AbortSettingsReloadNull;
                }

                await refreshAvailableDeviceCollectionsAsync().WaitAsync(cancellationToken);
                ensureActive?.Invoke();
                if (authorizeSettingsReloadAsync == null || await authorizeSettingsReloadAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ensureActive?.Invoke();
                    applyExternallyReloadedSettings(newSettings);
                }
                return RefreshWorkflowOutcome.Continue;
            }

            if (refreshDeviceCollections)
            {
                await refreshAvailableDeviceCollectionsAsync().WaitAsync(cancellationToken);
                ensureActive?.Invoke();
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
            Logger logger,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!settingsChanged || !promptOnPotentialOverwrite || !hasPendingLocalEdits)
            {
                return settingsChanged;
            }

            AppDialogResult overwriteResult = await promptOverwrite().WaitAsync(cancellationToken);
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
            Logger logger,
            Func<long>? getEditRevision = null,
            Action? ensureActive = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ensureActive?.Invoke();
            if (settingsChanged || !hasUiSettingsDivergedFromCachedSettings)
            {
                return;
            }

            logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.RefreshSettingsReload} | opId={refreshOpId} reason=ui-state-drift");

            long editRevision = getEditRevision?.Invoke() ?? 0;
            bool reloadCachedSettings = promptOnPotentialOverwrite &&
                await promptReloadCachedSettings().WaitAsync(cancellationToken) == AppDialogResult.Confirmed;
            cancellationToken.ThrowIfCancellationRequested();
            ensureActive?.Invoke();
            reloadCachedSettings &= (getEditRevision?.Invoke() ?? 0) == editRevision;
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
            Logger logger,
            Action? ensureActive = null,
            CancellationToken cancellationToken = default)
        {
            if (isCleaningUp)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            ensureActive?.Invoke();
            generateDeviceReferenceFile();

            bool deferNonCriticalHotplugPostRefresh = ShouldDeferNonCriticalHotplugPostRefresh(
                refreshMixerWhenWindowHidden,
                isWindowVisible);

            if (!deferNonCriticalHotplugPostRefresh)
            {
                ensureActive?.Invoke();
                refreshDeviceCache();
                cancellationToken.ThrowIfCancellationRequested();
                ensureActive?.Invoke();
                await updateMuteFlagsFromSystemAsync().WaitAsync(cancellationToken);
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.HotplugPostRefreshDeferred} | opId={refreshOpId} actions=device-cache,mute-flags reason=window-hidden");
            }

            cancellationToken.ThrowIfCancellationRequested();
            ensureActive?.Invoke();
            bool shouldRefreshMixer = refreshMixerWhenWindowHidden || isWindowVisible;
            if (shouldRefreshMixer)
            {
                await refreshMixerAsync(isWindowVisible).WaitAsync(cancellationToken);
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
