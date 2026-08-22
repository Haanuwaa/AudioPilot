using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct StartupToggleExecutionInput(
        bool TargetValue,
        int DebounceMs,
        string OperationId);

    internal readonly record struct StartupToggleExecutionDependencies(
        Func<bool> IsStaleRequest,
        Func<Task<bool>> ApplyRegistryChangeAsync,
        Func<Task<bool>> PersistSettingsAsync);

    internal static class AppStartupToggleCoordinator
    {
        /// <summary>
        /// Creates a correlation id for a debounced startup-registry toggle operation.
        /// </summary>
        public static string CreateOperationId()
        {
            return $"startup-registry:{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Executes the debounced startup toggle and rechecks staleness before both the registry update and settings
        /// persistence steps so newer user intent can supersede in-flight work.
        /// </summary>
        public static async Task ExecuteDebouncedToggleAsync(
            StartupToggleExecutionInput input,
            StartupToggleExecutionDependencies dependencies,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            await Task.Delay(input.DebounceMs, cancellationToken);

            if (dependencies.IsStaleRequest())
            {
                logger.Trace("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupDebounceSkip} | opId={input.OperationId} reason=stale-request");
                return;
            }

            bool startupUpdated = await dependencies.ApplyRegistryChangeAsync();
            if (!startupUpdated)
            {
                return;
            }

            if (dependencies.IsStaleRequest())
            {
                logger.Trace("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupDebounceSkip} | opId={input.OperationId} reason=stale-settings-write");
                return;
            }

            bool settingsUpdated = await dependencies.PersistSettingsAsync();
            if (!settingsUpdated)
            {
                logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupSyncWarning} | opId={input.OperationId} reason=settings-write-failed persistence=next-save");
            }
        }
    }
}
