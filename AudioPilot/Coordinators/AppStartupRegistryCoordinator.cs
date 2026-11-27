using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal enum StartupInitAction
    {
        None,
        Add,
        Remove,
    }

    internal readonly record struct StartupInitPlan(
        StartupInitAction Action,
        string Reason,
        bool RunAtStartupValue,
        bool UseTaskScheduler);

    internal static class AppStartupRegistryCoordinator
    {
        /// <summary>
        /// Detects whether a queued startup toggle request is stale because a newer debounce source replaced it or the
        /// requested target state no longer matches the current state.
        /// </summary>
        internal static bool IsStaleDebounceRequest(
            CancellationTokenSource? activeDebounce,
            CancellationTokenSource requestDebounce,
            bool currentRunAtStartup,
            bool targetValue)
        {
            return !ReferenceEquals(activeDebounce, requestDebounce) || currentRunAtStartup != targetValue;
        }

        /// <summary>
        /// Resolves the startup-registry action needed to reconcile persisted settings, first-run behavior, and the
        /// current registry entry state.
        /// </summary>
        internal static StartupInitPlan BuildInitPlan(
            bool noSettingsFileExists,
            StartupSettings settings,
            StartupRegistrationSnapshot registration,
            bool settingsSchemaIsNewer = false)
        {
            if (!registration.Available)
                return new(StartupInitAction.None, "registration-unavailable", settings.Enabled, settings.UseTaskScheduler);

            if (settingsSchemaIsNewer)
                return new(StartupInitAction.None, "newer-settings-schema", registration.RunExists || registration.TaskEnabled, settings.UseTaskScheduler);

            if (noSettingsFileExists)
            {
                if (registration.TaskExists && !registration.RunMatches)
                    return new(registration.TaskEnabled && (!registration.TaskMatches || registration.RunExists)
                            ? StartupInitAction.Add : StartupInitAction.None,
                        "first-run-scheduled-task", registration.TaskEnabled, UseTaskScheduler: true);

                return new(registration.RunExists && !registration.RunMatches ? StartupInitAction.Add : StartupInitAction.None,
                    "first-run-run-key", registration.RunExists, UseTaskScheduler: false);
            }

            if (settings.Enabled)
            {
                bool valid = registration.Matches(settings.UseTaskScheduler);
                return new(valid ? StartupInitAction.None : StartupInitAction.Add,
                    valid ? "settings-enabled-valid-entry" : "missing-or-invalid-entry", true, settings.UseTaskScheduler);
            }

            return new(registration.RunExists || registration.TaskEnabled ? StartupInitAction.Remove : StartupInitAction.None,
                "disabled-in-settings", false, settings.UseTaskScheduler);
        }

        /// <summary>
        /// Updates the startup toggle and, when specified, the startup method in a normalized settings copy.
        /// </summary>
        internal static Settings CreateStartupUpdatedSettings(Settings sourceSettings, bool startupEnabled, bool? useTaskScheduler = null)
        {
            Settings updatedSettings = AppSettingsWorkflowCoordinator.CloneSettings(sourceSettings);
            updatedSettings.Startup.Enabled = startupEnabled;
            if (useTaskScheduler.HasValue) updatedSettings.Startup.UseTaskScheduler = useTaskScheduler.Value;
            return updatedSettings;
        }

        /// <summary>
        /// Executes the startup initialization plan and emits the appropriate sync log severity for corrective or
        /// first-run actions.
        /// </summary>
        internal static void ExecuteInitPlan(
            StartupInitPlan startupPlan,
            bool noSettingsFileExists,
            Action addToStartup,
            Action removeFromStartup,
            string startupRegistryOpId,
            Logger logger)
        {
            switch (startupPlan.Action)
            {
                case StartupInitAction.Add:
                    logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistrySync} | opId={startupRegistryOpId} action=add reason={startupPlan.Reason}");
                    addToStartup();
                    break;
                case StartupInitAction.Remove:
                    logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistrySync} | opId={startupRegistryOpId} action=remove reason={startupPlan.Reason}");
                    removeFromStartup();
                    break;
                case StartupInitAction.None:
                    if (noSettingsFileExists)
                    {
                        string keepOrDisable = startupPlan.RunAtStartupValue ? "keep" : "disable";
                        logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistrySync} | opId={startupRegistryOpId} action={keepOrDisable} reason={startupPlan.Reason}");
                    }
                    break;
            }
        }
    }
}
