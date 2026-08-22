namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineInputSwitchGuardHelper
{
    internal enum InputSwitchDecisionKind
    {
        Proceed,
        MissingDefaultDevice,
        AlreadyTarget,
    }

    internal readonly record struct InputSwitchDecision(
        InputSwitchDecisionKind Kind,
        AppViewModel.RoutineDeviceSwitchExecutionResult Result,
        string? CurrentDeviceId = null,
        string? CurrentDeviceName = null);

    internal static InputSwitchDecision Evaluate(string? currentDeviceId, string? currentDeviceName, string? targetDeviceId)
    {
        if (string.IsNullOrWhiteSpace(currentDeviceId))
        {
            return new InputSwitchDecision(
                InputSwitchDecisionKind.MissingDefaultDevice,
                new AppViewModel.RoutineDeviceSwitchExecutionResult(false, null, FailureDetail: "No default input device is available."));
        }

        if (string.Equals(currentDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return new InputSwitchDecision(
                InputSwitchDecisionKind.AlreadyTarget,
                new AppViewModel.RoutineDeviceSwitchExecutionResult(true, currentDeviceName),
                currentDeviceId,
                currentDeviceName);
        }

        return new InputSwitchDecision(
            InputSwitchDecisionKind.Proceed,
            default,
            currentDeviceId,
            currentDeviceName);
    }
}
