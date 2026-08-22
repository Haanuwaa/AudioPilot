using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal readonly record struct AppRefreshExecutionInput(
        bool PromptOnPotentialOverwrite,
        bool RefreshMixerWhenWindowHidden,
        bool CheckSettingsFileChanges,
        bool IsWindowVisible,
        bool IsCleaningUp,
        int OutputCycleCount);

    internal readonly record struct AppRefreshExecutionDependencies(
        Func<bool> HasPendingLocalEditsForRefresh,
        Func<Task<bool>> HasSettingsFileChangedAsync,
        Func<Task<AppDialogResult>> PromptReloadExternalSettings,
        Func<Task> RefreshAvailableDeviceCollectionsAsync,
        Func<Task<Settings?>> LoadSettingsForRefreshAsync,
        Action<Settings> ApplyExternallyReloadedSettings,
        Func<bool> HasUiSettingsDivergedFromCachedSettings,
        Func<Task<AppDialogResult>> PromptReloadCachedSettings,
        Func<Settings?> GetCachedSettingsSnapshot,
        Action GenerateDeviceReferenceFile,
        Action RefreshDeviceCache,
        Func<Task> UpdateMuteFlagsFromSystemAsync,
        Func<bool, Task> RefreshMixerAsync,
        Func<long> GetEditRevision,
        Action EnsureActive);

    internal readonly record struct AppRefreshExecutionResult(
        bool SettingsChanged,
        RefreshWorkflowOutcome WorkflowOutcome);

    internal static class AppRefreshCycleCoordinator
    {
        /// <summary>
        /// Executes the full refresh cycle, including external settings reconciliation, collection refresh, cached UI
        /// drift handling, and post-refresh side effects under one correlated operation id.
        /// </summary>
        internal static async Task<AppRefreshExecutionResult> ExecuteAsync(
            AppRefreshExecutionInput input,
            AppRefreshExecutionDependencies dependencies,
            string refreshOpId,
            Logger logger,
            CancellationToken cancellationToken = default)
        {
            if (input.IsCleaningUp)
                return new(false, RefreshWorkflowOutcome.Continue);

            void EnsureActive()
            {
                cancellationToken.ThrowIfCancellationRequested();
                dependencies.EnsureActive();
            }

            EnsureActive();
            logger.Debug(
                "AppViewModel",
                () => $"refresh-start | opId={refreshOpId} promptOnPotentialOverwrite={input.PromptOnPotentialOverwrite} refreshMixerWhenWindowHidden={input.RefreshMixerWhenWindowHidden} checkSettingsFileChanges={input.CheckSettingsFileChanges}");

            long editRevisionAtStart = dependencies.GetEditRevision();
            bool hadPendingLocalEdits = input.PromptOnPotentialOverwrite && dependencies.HasPendingLocalEditsForRefresh();
            bool settingsFileChanged = input.CheckSettingsFileChanges &&
                await dependencies.HasSettingsFileChangedAsync().WaitAsync(cancellationToken);
            EnsureActive();
            bool settingsChanged = settingsFileChanged;

            async Task<bool> AuthorizeSettingsReloadAsync()
            {
                EnsureActive();
                long editRevision = dependencies.GetEditRevision();
                bool hasPendingLocalEdits = input.PromptOnPotentialOverwrite && dependencies.HasPendingLocalEditsForRefresh();
                if (!hasPendingLocalEdits && (editRevision != editRevisionAtStart || hadPendingLocalEdits))
                {
                    settingsChanged = false;
                    logger.Info("AppViewModel", () => $"refresh-settings-reload-skip | opId={refreshOpId} reason=newer-saved-edits");
                    return false;
                }
                settingsChanged = await AppRefreshExecutionCoordinator.ResolveSettingsChangedForRefreshAsync(
                    settingsFileChanged,
                    input.PromptOnPotentialOverwrite,
                    hasPendingLocalEdits,
                    dependencies.PromptReloadExternalSettings,
                    refreshOpId,
                    logger,
                    cancellationToken);
                EnsureActive();
                if (dependencies.GetEditRevision() != editRevision)
                {
                    settingsChanged = false;
                    logger.Info("AppViewModel", () => $"refresh-settings-reload-skip | opId={refreshOpId} reason=newer-local-edits");
                }
                return settingsChanged;
            }

            bool refreshDeviceCollections = AppRefreshExecutionCoordinator.ShouldRefreshDeviceCollections(
                settingsChanged,
                input.RefreshMixerWhenWindowHidden,
                input.IsWindowVisible);

            RefreshWorkflowOutcome workflowOutcome = await AppRefreshExecutionCoordinator.RefreshCollectionsAndApplySettingsAsync(
                settingsFileChanged,
                refreshDeviceCollections,
                dependencies.RefreshAvailableDeviceCollectionsAsync,
                dependencies.LoadSettingsForRefreshAsync,
                dependencies.ApplyExternallyReloadedSettings,
                refreshOpId,
                logger,
                AuthorizeSettingsReloadAsync,
                EnsureActive,
                cancellationToken);

            if (workflowOutcome == RefreshWorkflowOutcome.AbortSettingsReloadNull)
            {
                return new AppRefreshExecutionResult(settingsChanged, workflowOutcome);
            }

            await AppRefreshExecutionCoordinator.ApplyCachedSettingsIfUiDriftedAsync(
                settingsFileChanged,
                input.PromptOnPotentialOverwrite,
                !settingsFileChanged && input.PromptOnPotentialOverwrite && dependencies.HasUiSettingsDivergedFromCachedSettings(),
                dependencies.PromptReloadCachedSettings,
                dependencies.GetCachedSettingsSnapshot,
                dependencies.ApplyExternallyReloadedSettings,
                refreshOpId,
                logger,
                dependencies.GetEditRevision,
                EnsureActive,
                cancellationToken);

            await AppRefreshExecutionCoordinator.ApplyPostRefreshEffectsAsync(
                input.RefreshMixerWhenWindowHidden,
                input.IsWindowVisible,
                input.IsCleaningUp,
                dependencies.GenerateDeviceReferenceFile,
                dependencies.RefreshDeviceCache,
                dependencies.UpdateMuteFlagsFromSystemAsync,
                dependencies.RefreshMixerAsync,
                input.OutputCycleCount,
                refreshOpId,
                logger,
                EnsureActive,
                cancellationToken);

            EnsureActive();
            logger.Debug("AppViewModel", () => $"refresh-completed | opId={refreshOpId} settingsChanged={settingsChanged}");
            return new AppRefreshExecutionResult(settingsChanged, workflowOutcome);
        }
    }
}
