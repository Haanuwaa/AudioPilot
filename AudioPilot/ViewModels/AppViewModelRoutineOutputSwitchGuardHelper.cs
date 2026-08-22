namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineOutputSwitchGuardHelper
{
    internal enum OutputSwitchDecisionKind
    {
        Proceed,
        MissingDefaultDevice,
        AlreadyTarget,
    }

    internal readonly record struct OutputSwitchDecision(
        OutputSwitchDecisionKind Kind,
        AppViewModel.RoutineDeviceSwitchExecutionResult Result,
        string? CurrentDeviceId = null,
        string? CurrentDeviceName = null);

    internal static OutputSwitchDecision Evaluate(string? currentDeviceId, string? currentDeviceName, string? targetDeviceId)
    {
        if (string.IsNullOrWhiteSpace(currentDeviceId))
        {
            return new OutputSwitchDecision(
                OutputSwitchDecisionKind.MissingDefaultDevice,
                new AppViewModel.RoutineDeviceSwitchExecutionResult(false, null, FailureDetail: "No default output device is available."));
        }

        if (string.Equals(currentDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return new OutputSwitchDecision(
                OutputSwitchDecisionKind.AlreadyTarget,
                new AppViewModel.RoutineDeviceSwitchExecutionResult(true, currentDeviceName),
                currentDeviceId,
                currentDeviceName);
        }

        return new OutputSwitchDecision(
            OutputSwitchDecisionKind.Proceed,
            default,
            currentDeviceId,
            currentDeviceName);
    }
}
