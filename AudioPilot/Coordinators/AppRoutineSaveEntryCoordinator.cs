using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal readonly record struct RoutineSaveValidationResult(
        bool CanProceed,
        string? WarningMessage,
        string WarningCaption,
        bool RequiresConfirmation = false,
        string? ConfirmationMessage = null,
        string ConfirmationCaption = DialogText.Captions.Confirm);

    internal static class AppRoutineSaveEntryCoordinator
    {
        public static RoutineSaveValidationResult ValidateSave(
            Settings? cachedSettings,
            Settings? newSettings,
            Func<Settings, AppViewModel.SettingsCommitValidationResult> validateSettingsForCommit)
        {
            if (cachedSettings == null)
            {
                return new RoutineSaveValidationResult(
                    false,
                    "Settings are not loaded yet. Please try again after initialization completes.",
                    DialogText.Captions.Warning);
            }

            ArgumentNullException.ThrowIfNull(newSettings);

            AppViewModel.SettingsCommitValidationResult commitValidation = validateSettingsForCommit(newSettings);
            if (commitValidation.HasBlockingIssues)
            {
                return new RoutineSaveValidationResult(
                    false,
                    DialogText.Messages.BuildInvalidSettingsBeforeSaving(commitValidation.BlockingMessages),
                    DialogText.Captions.InvalidSettings);
            }

            IReadOnlyList<string> conflictWarnings = AppViewModelRoutineConflictHelper.BuildDistinctConflictWarnings(newSettings.Routines.Items);
            if (conflictWarnings.Count > 0)
            {
                return new RoutineSaveValidationResult(
                    true,
                    null,
                    DialogText.Captions.Success,
                    RequiresConfirmation: true,
                    ConfirmationMessage: DialogText.Messages.BuildRoutineConflictSaveConfirmation(conflictWarnings),
                    ConfirmationCaption: DialogText.Captions.Warning);
            }

            return new RoutineSaveValidationResult(true, null, DialogText.Captions.Success);
        }

        public static void RunSaveSuccessSideEffects(
            Settings newSettings,
            Action persistUiState,
            Action<Settings> registerRoutineHotkeys,
            Action<IEnumerable<AudioRoutine>?> applyRoutinesFromSettings,
            Action<string, string> showSuccess)
        {
            AppRoutineSettingsCoordinator.RunSaveSideEffects(
                newSettings,
                persistUiState,
                registerRoutineHotkeys,
                applyRoutinesFromSettings);
            showSuccess("Routine changes applied successfully.", DialogText.Captions.Success);
        }

        public static void ShowSaveFailure(Action<string> logError, Action<string> showError)
        {
            logError("save-routines-failed");
            showError("Failed to apply routine changes.");
        }

        public static void ShowValidationWarning(RoutineSaveValidationResult validationResult, Action<string, string> showWarning)
        {
            if (!validationResult.CanProceed)
            {
                showWarning(validationResult.WarningMessage ?? string.Empty, validationResult.WarningCaption);
            }
        }

        public static async Task<bool> ShouldProceedWithConfirmationAsync(RoutineSaveValidationResult validationResult, Func<string, string, Task<AppDialogResult>> showConfirmation)
        {
            if (!validationResult.RequiresConfirmation)
            {
                return true;
            }

            return await showConfirmation(
                validationResult.ConfirmationMessage ?? string.Empty,
                validationResult.ConfirmationCaption) == AppDialogResult.Confirmed;
        }
    }
}
