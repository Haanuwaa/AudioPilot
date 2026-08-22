namespace AudioPilot.Coordinators
{
    internal static class AppSettingsEntryCoordinator
    {
        public static Task<AppDialogResult> ShowApplyResultAsync(
            ApplySettingsSideEffectResult applyEffects,
            IAppDialogService dialogs,
            string warningCaption,
            string successCaption,
            Func<string, Task<AppDialogResult>>? showSuccess = null)
        {
            return applyEffects.Warnings.Count > 0
                ? dialogs.ShowWarningAsync(Services.UI.DialogText.Messages.BuildSettingsAppliedWithWarnings(applyEffects.Warnings), warningCaption)
                : showSuccess != null ? showSuccess("Settings applied successfully.")
                : dialogs.ShowSuccessAsync("Settings applied successfully.", successCaption);
        }

        public static Task<AppDialogResult> ShowSaveResultAsync(
            SaveSettingsSideEffectResult saveEffects,
            IAppDialogService dialogs,
            string warningCaption,
            string successCaption,
            Func<string, Task<AppDialogResult>>? showSuccess = null)
        {
            return saveEffects.Warnings.Count > 0
                ? dialogs.ShowWarningAsync(Services.UI.DialogText.Messages.BuildSettingsSavedWithWarnings(saveEffects.Warnings), warningCaption)
                : showSuccess != null ? showSuccess(Services.UI.DialogText.Messages.BuildSettingsSavedSuccessfully())
                : dialogs.ShowSuccessAsync(Services.UI.DialogText.Messages.BuildSettingsSavedSuccessfully(), successCaption);
        }
    }
}
