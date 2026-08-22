using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal static class AppSettingsEntryCoordinator
    {
        public static bool TryValidateCommit(
            Settings candidate,
            Func<IReadOnlyList<string>, string> buildValidationMessage,
            Func<Settings, AppViewModel.SettingsCommitValidationResult> validateSettingsForCommit,
            Action<string, string> showWarning,
            string invalidCaption)
        {
            AppViewModel.SettingsCommitValidationResult commitValidation = validateSettingsForCommit(candidate);
            if (!commitValidation.HasBlockingIssues)
            {
                return true;
            }

            showWarning(buildValidationMessage(commitValidation.BlockingMessages), invalidCaption);
            return false;
        }

        public static void ShowApplyResult(
            ApplySettingsSideEffectResult applyEffects,
            Action<string, string> showWarning,
            Action<string, string> showSuccess,
            string warningCaption,
            string successCaption)
        {
            if (applyEffects.Warnings.Count > 0)
            {
                showWarning(
                    Services.UI.DialogText.Messages.BuildSettingsAppliedWithWarnings(applyEffects.Warnings),
                    warningCaption);
                return;
            }

            showSuccess("Settings applied successfully.", successCaption);
        }

        public static void ShowSaveResult(
            SaveSettingsSideEffectResult saveEffects,
            Action<string, string> showWarning,
            Action<string, string> showSuccess,
            string warningCaption,
            string successCaption)
        {
            if (saveEffects.Warnings.Count > 0)
            {
                string warningMessage = Services.UI.DialogText.Messages.BuildSettingsSavedWithWarnings(saveEffects.Warnings);
                showWarning(warningMessage, warningCaption);
                return;
            }

            string successMessage = Services.UI.DialogText.Messages.BuildSettingsSavedSuccessfully();
            showSuccess(successMessage, successCaption);
        }

        public static Task<AppDialogResult> ShowApplyResultAsync(
            ApplySettingsSideEffectResult applyEffects,
            IAppDialogService dialogs,
            string warningCaption,
            string successCaption)
        {
            return applyEffects.Warnings.Count > 0
                ? dialogs.ShowWarningAsync(Services.UI.DialogText.Messages.BuildSettingsAppliedWithWarnings(applyEffects.Warnings), warningCaption)
                : dialogs.ShowSuccessAsync("Settings applied successfully.", successCaption);
        }

        public static Task<AppDialogResult> ShowSaveResultAsync(
            SaveSettingsSideEffectResult saveEffects,
            IAppDialogService dialogs,
            string warningCaption,
            string successCaption)
        {
            return saveEffects.Warnings.Count > 0
                ? dialogs.ShowWarningAsync(Services.UI.DialogText.Messages.BuildSettingsSavedWithWarnings(saveEffects.Warnings), warningCaption)
                : dialogs.ShowSuccessAsync(Services.UI.DialogText.Messages.BuildSettingsSavedSuccessfully(), successCaption);
        }
    }
}
