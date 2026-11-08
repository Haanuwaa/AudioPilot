namespace AudioPilot.Coordinators
{
    internal static class AppSettingsEntryCoordinator
    {
        public static async Task<AppDialogResult> ShowApplyResultAsync(
            ApplySettingsSideEffectResult applyEffects,
            IAppDialogService dialogs,
            string warningCaption,
            string successCaption,
            Func<string, Task<AppDialogResult>>? showSuccess = null)
        {
            if (applyEffects.Warnings.Count > 0)
            {
                AppDialogResult result = await dialogs.ShowWarningAsync(Services.UI.DialogText.Messages.BuildSettingsAppliedWithWarnings(applyEffects.Warnings), warningCaption);
                if (showSuccess != null) await showSuccess("Settings applied with warnings.");
                return result;
            }
            return await (showSuccess != null ? showSuccess("Settings applied successfully.")
                : dialogs.ShowSuccessAsync("Settings applied successfully.", successCaption));
        }

        public static async Task<AppDialogResult> ShowSaveResultAsync(
            SaveSettingsSideEffectResult saveEffects,
            IAppDialogService dialogs,
            string warningCaption,
            string successCaption,
            Func<string, Task<AppDialogResult>>? showSuccess = null)
        {
            if (saveEffects.Warnings.Count > 0)
            {
                AppDialogResult result = await dialogs.ShowWarningAsync(Services.UI.DialogText.Messages.BuildSettingsSavedWithWarnings(saveEffects.Warnings), warningCaption);
                if (showSuccess != null) await showSuccess("Settings saved with warnings.");
                return result;
            }
            return await (showSuccess != null ? showSuccess(Services.UI.DialogText.Messages.BuildSettingsSavedSuccessfully())
                : dialogs.ShowSuccessAsync(Services.UI.DialogText.Messages.BuildSettingsSavedSuccessfully(), successCaption));
        }
    }
}
