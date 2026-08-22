namespace AudioPilot.ViewModels
{
    internal static class AppViewModelRoutineOverlayHelper
    {
        internal readonly record struct RoutineSuccessOverlayPlan(
            bool ShowCombined,
            OverlayDeviceKind Kind,
            string Header,
            string? DeviceName,
            string? OutputDeviceName = null,
            string? InputDeviceName = null);

        internal readonly record struct RoutineFailureOverlayPlan(
            bool IsPartial,
            OverlayDeviceKind Kind,
            string Header,
            string? DeviceName,
            string? SuccessfulOutputName = null,
            string? SuccessfulInputName = null,
            string? FailedOutputName = null,
            string? FailedInputName = null);

        public static string BuildRoutinePartialOverlayHeader(string? routineName)
        {
            return $"{NormalizeRoutineOverlayName(routineName)} - Partial";
        }

        public static AppViewModel.RoutineOverlayDisplay BuildRoutineOverlayDisplay(string? routineName, string? outputDeviceName, string? inputDeviceName)
        {
            string? normalizedOutput = NormalizeRoutineOverlayLine(outputDeviceName);
            string? normalizedInput = NormalizeRoutineOverlayLine(inputDeviceName);
            bool hasOutput = !string.IsNullOrWhiteSpace(normalizedOutput);
            bool hasInput = !string.IsNullOrWhiteSpace(normalizedInput);

            string trimmedName = NormalizeRoutineOverlayName(routineName);
            string suffix = hasOutput && hasInput
                ? "Output/Input"
                : hasOutput
                    ? "Output"
                    : hasInput
                        ? "Input"
                        : "Routine";

            return new AppViewModel.RoutineOverlayDisplay($"{trimmedName} - {suffix}", normalizedOutput, normalizedInput);
        }

        public static bool TryBuildRoutineSuccessOverlayPlan(
            string? routineName,
            string? outputDeviceName,
            string? inputDeviceName,
            out RoutineSuccessOverlayPlan plan)
        {
            AppViewModel.RoutineOverlayDisplay display = BuildRoutineOverlayDisplay(routineName, outputDeviceName, inputDeviceName);
            bool hasOutput = !string.IsNullOrWhiteSpace(display.OutputDeviceName);
            bool hasInput = !string.IsNullOrWhiteSpace(display.InputDeviceName);

            if (hasOutput && hasInput)
            {
                plan = new RoutineSuccessOverlayPlan(
                    ShowCombined: true,
                    Kind: OverlayDeviceKind.Output,
                    Header: display.Header,
                    DeviceName: null,
                    OutputDeviceName: display.OutputDeviceName,
                    InputDeviceName: display.InputDeviceName);
                return true;
            }

            if (hasOutput)
            {
                plan = new RoutineSuccessOverlayPlan(
                    ShowCombined: false,
                    Kind: OverlayDeviceKind.Output,
                    Header: display.Header,
                    DeviceName: display.OutputDeviceName);
                return true;
            }

            if (hasInput)
            {
                plan = new RoutineSuccessOverlayPlan(
                    ShowCombined: false,
                    Kind: OverlayDeviceKind.Input,
                    Header: display.Header,
                    DeviceName: display.InputDeviceName);
                return true;
            }

            plan = default;
            return false;
        }

        public static bool TryBuildRoutineRestoreOverlayPlan(
            string? outputDeviceName,
            string? inputDeviceName,
            out RoutineSuccessOverlayPlan plan)
        {
            string? normalizedOutput = NormalizeRoutineOverlayLine(outputDeviceName);
            string? normalizedInput = NormalizeRoutineOverlayLine(inputDeviceName);
            bool hasOutput = !string.IsNullOrWhiteSpace(normalizedOutput);
            bool hasInput = !string.IsNullOrWhiteSpace(normalizedInput);

            if (hasOutput && hasInput)
            {
                plan = new RoutineSuccessOverlayPlan(
                    ShowCombined: true,
                    Kind: OverlayDeviceKind.Output,
                    Header: "Restored audio",
                    DeviceName: null,
                    OutputDeviceName: normalizedOutput,
                    InputDeviceName: normalizedInput);
                return true;
            }

            if (hasOutput)
            {
                plan = new RoutineSuccessOverlayPlan(
                    ShowCombined: false,
                    Kind: OverlayDeviceKind.Output,
                    Header: "Restored output device",
                    DeviceName: normalizedOutput);
                return true;
            }

            if (hasInput)
            {
                plan = new RoutineSuccessOverlayPlan(
                    ShowCombined: false,
                    Kind: OverlayDeviceKind.Input,
                    Header: "Restored input device",
                    DeviceName: normalizedInput);
                return true;
            }

            plan = default;
            return false;
        }

        public static string NormalizeRoutineOverlayName(string? routineName)
        {
            return string.IsNullOrWhiteSpace(routineName)
                ? "Routine"
                : string.Join(" ", routineName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string? ResolveRoutineOverlayDeviceName(bool shouldInclude, string? preferredName, string? configuredName, string fallbackLabel)
        {
            if (!shouldInclude)
            {
                return null;
            }

            string? resolved = NormalizeRoutineOverlayLine(preferredName) ?? NormalizeRoutineOverlayLine(configuredName);
            return resolved ?? fallbackLabel;
        }

        public static bool TryBuildRoutineFailureOverlayPlan(
            string? routineName,
            string? configuredOutputName,
            string? configuredInputName,
            string? appliedOutputDeviceName,
            string? appliedInputDeviceName,
            bool? outputSucceeded,
            bool? inputSucceeded,
            out RoutineFailureOverlayPlan plan)
        {
            string? successfulOutputName = ResolveRoutineOverlayDeviceName(
                outputSucceeded == true,
                appliedOutputDeviceName,
                configuredOutputName,
                fallbackLabel: "Output device");
            string? successfulInputName = ResolveRoutineOverlayDeviceName(
                inputSucceeded == true,
                appliedInputDeviceName,
                configuredInputName,
                fallbackLabel: "Input device");
            string? failedOutputName = ResolveRoutineOverlayDeviceName(
                outputSucceeded == false,
                preferredName: null,
                configuredName: configuredOutputName,
                fallbackLabel: "Output device");
            string? failedInputName = ResolveRoutineOverlayDeviceName(
                inputSucceeded == false,
                preferredName: null,
                configuredName: configuredInputName,
                fallbackLabel: "Input device");

            if ((outputSucceeded == true && inputSucceeded == false) || (outputSucceeded == false && inputSucceeded == true))
            {
                plan = new RoutineFailureOverlayPlan(
                    IsPartial: true,
                    Kind: OverlayDeviceKind.Error,
                    Header: BuildRoutinePartialOverlayHeader(routineName),
                    DeviceName: null,
                    SuccessfulOutputName: successfulOutputName,
                    SuccessfulInputName: successfulInputName,
                    FailedOutputName: failedOutputName,
                    FailedInputName: failedInputName);
                return true;
            }

            if (outputSucceeded == false && inputSucceeded == false)
            {
                plan = new RoutineFailureOverlayPlan(
                    IsPartial: false,
                    Kind: OverlayDeviceKind.Error,
                    Header: "Routine output/input failed",
                    DeviceName: routineName);
                return true;
            }

            if (outputSucceeded == false)
            {
                plan = new RoutineFailureOverlayPlan(
                    IsPartial: false,
                    Kind: OverlayDeviceKind.Error,
                    Header: "Routine output failed",
                    DeviceName: failedOutputName ?? "Output device");
                return true;
            }

            if (inputSucceeded == false)
            {
                plan = new RoutineFailureOverlayPlan(
                    IsPartial: false,
                    Kind: OverlayDeviceKind.Error,
                    Header: "Routine input failed",
                    DeviceName: failedInputName ?? "Input device");
                return true;
            }

            plan = default;
            return false;
        }

        private static string? NormalizeRoutineOverlayLine(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
