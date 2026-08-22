using AudioPilot.Constants;

namespace AudioPilot.Coordinators
{
    internal enum ResetDialogKind
    {
        None,
        Info,
        Success,
        Error,
    }

    internal readonly record struct ResetDefaultsPromptPlan(
        bool ShouldSkip,
        string? DialogMessage,
        string DialogCaption,
        AppDialogKind DialogKind);

    internal readonly record struct ResetPerAppRoutingDialogPlan(
        ResetDialogKind Kind,
        string Message,
        string Caption);

    internal static class AppResetCoordinator
    {
        public static ResetDefaultsPromptPlan BuildResetDefaultsPromptPlan(
            bool settingsFileExists,
            bool hasDevicesSelected,
            bool hasRoutines,
            bool hasHotkey,
            bool hasStartup,
            Func<bool, bool, bool, bool, bool, IReadOnlyList<string>> buildResetSummary)
        {
            if (AppSettingsWorkflowCoordinator.ShouldSkipReset(settingsFileExists, hasDevicesSelected, hasRoutines, hasHotkey, hasStartup))
            {
                return new ResetDefaultsPromptPlan(
                    ShouldSkip: true,
                    DialogMessage: "There is nothing to reset. No settings have been saved or configured.",
                    DialogCaption: DialogText.Captions.NothingToReset,
                    DialogKind: AppDialogKind.Information);
            }

            IReadOnlyList<string> resetSummary = buildResetSummary(hasDevicesSelected, hasRoutines, hasHotkey, hasStartup, settingsFileExists);
            return new ResetDefaultsPromptPlan(
                ShouldSkip: false,
                DialogMessage: "This will reset all settings to default:\n\n" + string.Join("\n", resetSummary) + "\n\nAre you sure you want to continue?",
                DialogCaption: DialogText.Captions.ResetToDefaults,
                DialogKind: AppDialogKind.Warning);
        }

        public static bool ShouldProceed(AppDialogResult result)
        {
            return result == AppDialogResult.Confirmed;
        }

        public static ResetPerAppRoutingDialogPlan BuildPerAppRoutingDialogPlan(PerAppAudioRoutingResetResult resetResult)
        {
            if (!resetResult.Success)
            {
                return new ResetPerAppRoutingDialogPlan(
                    ResetDialogKind.Error,
                    "Failed to reset per-application audio assignments.",
                    DialogText.Captions.ResetPerAppAudio);
            }

            if (!resetResult.HadAssignments)
            {
                return new ResetPerAppRoutingDialogPlan(
                    ResetDialogKind.Info,
                    "No per-application audio assignments were found in Windows.",
                    DialogText.Captions.ResetPerAppAudio);
            }

            return new ResetPerAppRoutingDialogPlan(
                ResetDialogKind.Success,
                "Per-application audio assignments were reset. Running applications may need to restart audio playback before they follow the default devices again.",
                DialogText.Captions.ResetPerAppAudio);
        }

        public static async Task ShowResetPerAppRoutingDialogAsync(
            ResetPerAppRoutingDialogPlan plan,
            Func<string, string, Task<AppDialogResult>> showInfo,
            Func<string, string, Task<AppDialogResult>> showSuccess,
            Func<string, string, Task<AppDialogResult>> showError)
        {
            switch (plan.Kind)
            {
                case ResetDialogKind.Info:
                    await showInfo(plan.Message, plan.Caption);
                    break;
                case ResetDialogKind.Success:
                    await showSuccess(plan.Message, plan.Caption);
                    break;
                case ResetDialogKind.Error:
                    await showError(plan.Message, plan.Caption);
                    break;
            }
        }

        public static string BuildResetSkipLogMessage()
        {
            return $"{AppConstants.Audio.LogEvents.ViewModel.App.ResetSkip} | reason=nothing-to-reset";
        }
    }
}
