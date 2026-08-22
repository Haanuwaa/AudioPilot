using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal readonly record struct CliSettingsMutationDecision<TResult>(
        TResult Result,
        bool Persist,
        Settings? ReplacementSettings = null);

    internal readonly record struct CliSettingsMutationOutcome<TResult>(
        TResult Result,
        Settings? PersistedSettings);

    internal static class AppCliSettingsMutationCoordinator
    {
        public static async Task<CliSettingsMutationOutcome<TResult>> ExecuteAsync<TResult>(
            Func<Settings?> currentSettingsProvider,
            Func<Task<Settings>> loadSettingsAsync,
            Func<Settings, CliSettingsMutationDecision<TResult>> mutation,
            Func<Settings, Task> persistSettingsAsync,
            Action<Settings> publishSettings,
            SemaphoreSlim settingsWriteSemaphore,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(currentSettingsProvider);
            ArgumentNullException.ThrowIfNull(loadSettingsAsync);
            ArgumentNullException.ThrowIfNull(mutation);
            ArgumentNullException.ThrowIfNull(persistSettingsAsync);
            ArgumentNullException.ThrowIfNull(publishSettings);
            ArgumentNullException.ThrowIfNull(settingsWriteSemaphore);

            bool lockAcquired = false;
            try
            {
                await settingsWriteSemaphore.WaitAsync(cancellationToken);
                lockAcquired = true;

                Settings current = currentSettingsProvider() ?? await loadSettingsAsync();
                Settings candidate = current.Clone();
                CliSettingsMutationDecision<TResult> decision = mutation(candidate);
                if (!decision.Persist)
                {
                    return new CliSettingsMutationOutcome<TResult>(decision.Result, PersistedSettings: null);
                }

                candidate = decision.ReplacementSettings ?? candidate;
                cancellationToken.ThrowIfCancellationRequested();
                await persistSettingsAsync(candidate);
                publishSettings(candidate);
                return new CliSettingsMutationOutcome<TResult>(decision.Result, candidate);
            }
            finally
            {
                if (lockAcquired)
                {
                    settingsWriteSemaphore.Release();
                }
            }
        }
    }
}
