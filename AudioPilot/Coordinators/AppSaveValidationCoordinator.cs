using AudioPilot.Constants;

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
        string WarningCaption);

    internal static class AppSaveValidationCoordinator
    {
        public static SaveValidationResult Validate(SaveValidationInput input)
        {
            bool outputSwitchConfigured = input.OutputHotkeysEnabled && input.HasOutputHotkey;
            bool inputSwitchConfigured = input.InputHotkeysEnabled && input.HasInputHotkey;

            if (input.EditState.OutputCleared && outputSwitchConfigured)
            {
                return CreateFailure("output-cycle-empty", "Please add at least one output device before saving.");
            }

            if (input.EditState.InputCleared && inputSwitchConfigured)
            {
                return CreateFailure("input-cycle-empty", "Please add at least one input device before saving.");
            }

            if (input.EditState.OutputEdited &&
                !input.EditState.OutputCleared &&
                !IsInactiveEmptyCycle(input.OutputCycleCount, outputSwitchConfigured))
            {
                if (input.OutputCycleCount == 0)
                {
                    return CreateFailure("output-cycle-empty", "Please add at least one output device before saving.");
                }

                if (input.OutputCycleCount < 2)
                {
                    return CreateFailure("output-cycle-insufficient", "Please add at least two output devices before saving.");
                }

            }

            if (input.EditState.InputEdited &&
                !input.EditState.InputCleared &&
                !IsInactiveEmptyCycle(input.InputCycleCount, inputSwitchConfigured))
            {
                if (input.InputCycleCount == 0)
                {
                    return CreateFailure("input-cycle-empty", "Please add at least one input device before saving.");
                }

                if (input.InputCycleCount < 2)
                {
                    return CreateFailure("input-cycle-insufficient", "Please add at least two input devices before saving.");
                }
            }

            if (!input.OverlayDurationValid)
            {
                return new SaveValidationResult(
                    IsValid: false,
                    LogReason: "overlay-duration-invalid",
                    WarningMessage: DialogText.Messages.InvalidOverlayDuration,
                    WarningCaption: DialogText.Captions.InvalidOverlayDuration);
            }

            return new SaveValidationResult(true, null, null, DialogText.Captions.Warning);
        }

        private static bool IsInactiveEmptyCycle(int cycleCount, bool switchConfigured)
        {
            return !switchConfigured && cycleCount == 0;
        }

        private static SaveValidationResult CreateFailure(string logReason, string warningMessage)
        {
            return new SaveValidationResult(
                IsValid: false,
                LogReason: logReason,
                WarningMessage: warningMessage,
                WarningCaption: DialogText.Captions.Warning);
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
