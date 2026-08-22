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
        ValidatePath,
    }

    internal readonly record struct StartupInitPlan(
        StartupInitAction Action,
        string Reason,
        bool RunAtStartupValue,
        bool Warn);

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
            bool settingsRunAtStartup,
            bool inStartup,
            bool inStartupWithValidPath)
        {
            if (!noSettingsFileExists)
            {
                if (settingsRunAtStartup)
                {
                    if (!inStartupWithValidPath)
                    {
                        return new StartupInitPlan(StartupInitAction.Add, "missing-or-invalid-entry", true, false);
                    }

                    return new StartupInitPlan(StartupInitAction.ValidatePath, "settings-enabled-valid-entry", true, false);
                }

                if (inStartup)
                {
                    return new StartupInitPlan(StartupInitAction.Remove, "disabled-in-settings", false, true);
                }

                return new StartupInitPlan(StartupInitAction.None, "settings-disabled-no-entry", false, false);
            }

            if (inStartupWithValidPath)
            {
                return new StartupInitPlan(StartupInitAction.None, "first-run-valid-entry", true, false);
            }

            if (inStartup)
            {
                return new StartupInitPlan(StartupInitAction.Add, "first-run-mismatched-path", true, false);
            }

            return new StartupInitPlan(StartupInitAction.None, "first-run-no-entry", false, false);
        }

        /// <summary>
        /// Produces a settings snapshot that updates only the startup toggle while preserving the rest of the persisted
        /// startup-relevant configuration in normalized form.
        /// </summary>
        internal static Settings CreateStartupUpdatedSettings(Settings sourceSettings, bool startupEnabled)
        {
            Settings updatedSettings = AppSettingsWorkflowCoordinator.CloneSettings(sourceSettings);
            updatedSettings.RunAtStartup = startupEnabled;
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
            Action validateAndUpdateStartupPath,
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
                    if (startupPlan.Warn)
                    {
                        logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistrySync} | opId={startupRegistryOpId} action=remove reason={startupPlan.Reason}");
                    }
                    else
                    {
                        logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistrySync} | opId={startupRegistryOpId} action=remove reason={startupPlan.Reason}");
                    }

                    removeFromStartup();
                    break;
                case StartupInitAction.ValidatePath:
                    validateAndUpdateStartupPath();
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

        /// <summary>
        /// Applies a direct startup toggle change and converts registry exceptions into a boolean result so callers can
        /// keep their debounce workflow deterministic.
        /// </summary>
        internal static bool TryApplyStartupChange(
            bool enable,
            Action addToStartup,
            Action removeFromStartup,
            string startupRegistryOpId,
            Logger logger,
            string methodName)
        {
            try
            {
                if (enable)
                {
                    addToStartup();
                    logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistryUpdate} | opId={startupRegistryOpId} action=add success=true");
                }
                else
                {
                    removeFromStartup();
                    logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistryUpdate} | opId={startupRegistryOpId} action=remove success=true");
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Error("AppViewModel", "Failed to update startup state", methodName, ex);
                return false;
            }
        }

        private static List<string> CloneRoleSelections(IEnumerable<string>? roles, IReadOnlyList<string> fallback)
        {
            if (roles == null)
            {
                return [.. fallback];
            }

            var cloned = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in roles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                string trimmed = role.Trim();
                if (seen.Add(trimmed))
                {
                    cloned.Add(trimmed);
                }
            }

            return cloned.Count > 0 ? cloned : [.. fallback];
        }
    }
}
