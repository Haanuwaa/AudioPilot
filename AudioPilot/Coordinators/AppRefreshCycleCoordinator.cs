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
        Func<bool, Task> RefreshMixerAsync);

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
            Logger logger)
        {
            logger.Debug(
                "AppViewModel",
                () => $"refresh-start | opId={refreshOpId} promptOnPotentialOverwrite={input.PromptOnPotentialOverwrite} refreshMixerWhenWindowHidden={input.RefreshMixerWhenWindowHidden} checkSettingsFileChanges={input.CheckSettingsFileChanges}");

            bool hasPendingLocalEdits = input.PromptOnPotentialOverwrite && dependencies.HasPendingLocalEditsForRefresh();
            bool settingsFileChanged = input.CheckSettingsFileChanges && await dependencies.HasSettingsFileChangedAsync();
            bool settingsChanged = await AppRefreshExecutionCoordinator.ResolveSettingsChangedForRefreshAsync(
                settingsFileChanged,
                input.PromptOnPotentialOverwrite,
                hasPendingLocalEdits,
                dependencies.PromptReloadExternalSettings,
                refreshOpId,
                logger);

            bool refreshDeviceCollections = AppRefreshExecutionCoordinator.ShouldRefreshDeviceCollections(
                settingsChanged,
                input.RefreshMixerWhenWindowHidden,
                input.IsWindowVisible);

            RefreshWorkflowOutcome workflowOutcome = await AppRefreshExecutionCoordinator.RefreshCollectionsAndApplySettingsAsync(
                settingsChanged,
                refreshDeviceCollections,
                dependencies.RefreshAvailableDeviceCollectionsAsync,
                dependencies.LoadSettingsForRefreshAsync,
                dependencies.ApplyExternallyReloadedSettings,
                refreshOpId,
                logger);

            if (workflowOutcome == RefreshWorkflowOutcome.AbortSettingsReloadNull)
            {
                return new AppRefreshExecutionResult(settingsChanged, workflowOutcome);
            }

            await AppRefreshExecutionCoordinator.ApplyCachedSettingsIfUiDriftedAsync(
                settingsChanged,
                input.PromptOnPotentialOverwrite,
                input.PromptOnPotentialOverwrite && dependencies.HasUiSettingsDivergedFromCachedSettings(),
                dependencies.PromptReloadCachedSettings,
                dependencies.GetCachedSettingsSnapshot,
                dependencies.ApplyExternallyReloadedSettings,
                refreshOpId,
                logger);

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
                logger);

            logger.Debug("AppViewModel", () => $"refresh-completed | opId={refreshOpId} settingsChanged={settingsChanged}");
            return new AppRefreshExecutionResult(settingsChanged, workflowOutcome);
        }
    }
}
