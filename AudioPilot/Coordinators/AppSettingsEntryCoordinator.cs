namespace AudioPilot.Coordinators
{
    internal static class AppSettingsEntryCoordinator
    {
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
