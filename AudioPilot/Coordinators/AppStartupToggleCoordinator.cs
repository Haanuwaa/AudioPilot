using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct StartupToggleExecutionInput(
        int DebounceMs,
        string OperationId);

    internal readonly record struct StartupToggleExecutionDependencies(
        Func<bool> IsStaleRequest,
        Func<Task<bool>> ApplyAndPersistAsync);

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
        /// Debounces startup changes before committing registration and settings together. The commit must recheck
        /// staleness after acquiring its settings write lock so queued work respects newer user intent.
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

            if (!await dependencies.ApplyAndPersistAsync())
            {
                logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupSyncWarning} | opId={input.OperationId} reason=registration-or-settings-write-failed");
            }
        }
    }
}
