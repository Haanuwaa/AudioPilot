using AudioPilot.Constants;
using AudioPilot.Localization;

namespace AudioPilot.Coordinators
{
    internal readonly record struct SaveValidationInput(
        SaveEditState EditState,
        int OutputCycleCount,
        int InputCycleCount,
        bool OutputHotkeysEnabled,
        bool InputHotkeysEnabled,
        bool HasOutputHotkey,
        bool HasInputHotkey,
        bool OverlayDurationValid);

    internal readonly record struct SaveValidationResult(
        bool IsValid,
        string? LogReason,
        string? WarningMessage,
        string WarningCaption)
    {
        internal LocalizedText? WarningText { get; init; }
    }

    internal static class AppSaveValidationCoordinator
    {
        public static SaveValidationResult Validate(SaveValidationInput input)
        {
            bool outputSwitchConfigured = input.OutputHotkeysEnabled && input.HasOutputHotkey;
            bool inputSwitchConfigured = input.InputHotkeysEnabled && input.HasInputHotkey;

            if (input.EditState.OutputCleared && outputSwitchConfigured)
            {
                return CreateFailure("output-cycle-empty", new("Validation_AddOneOutputDevice"));
            }

            if (input.EditState.InputCleared && inputSwitchConfigured)
            {
                return CreateFailure("input-cycle-empty", new("Validation_AddOneInputDevice"));
            }

            if (input.EditState.OutputEdited &&
                !input.EditState.OutputCleared &&
                outputSwitchConfigured)
            {
                if (input.OutputCycleCount == 0)
                {
                    return CreateFailure("output-cycle-empty", new("Validation_AddOneOutputDevice"));
                }

                if (input.OutputCycleCount < 2)
                {
                    return CreateFailure("output-cycle-insufficient", new("Validation_AddTwoOutputDevices"));
                }

            }

            if (input.EditState.InputEdited &&
                !input.EditState.InputCleared &&
                inputSwitchConfigured)
            {
                if (input.InputCycleCount == 0)
                {
                    return CreateFailure("input-cycle-empty", new("Validation_AddOneInputDevice"));
                }

                if (input.InputCycleCount < 2)
                {
                    return CreateFailure("input-cycle-insufficient", new("Validation_AddTwoInputDevices"));
                }
            }

            if (!input.OverlayDurationValid)
            {
                return new SaveValidationResult(
                    IsValid: false,
                    LogReason: "overlay-duration-invalid",
                    WarningMessage: DialogText.Messages.InvalidOverlayDuration,
                    WarningCaption: DialogText.Captions.InvalidOverlayDuration)
                {
                    WarningText = new("Dialog_PleaseEnterAValidOverlayDurationIn"),
                };
            }

            return new SaveValidationResult(true, null, null, DialogText.Captions.Warning);
        }

        private static SaveValidationResult CreateFailure(string logReason, LocalizedText warningMessage)
        {
            return new SaveValidationResult(
                IsValid: false,
                LogReason: logReason,
                WarningMessage: LocalizationService.English.Format(warningMessage),
                WarningCaption: DialogText.Captions.Warning)
            {
                WarningText = warningMessage,
            };
        }

        public static void LogFailure(SaveValidationResult result, Logging.ILogger logger)
        {
            if (result.IsValid || string.IsNullOrWhiteSpace(result.LogReason))
            {
                return;
            }

            logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.SaveValidationFailed} | reason={result.LogReason}");
        }
    }
}
