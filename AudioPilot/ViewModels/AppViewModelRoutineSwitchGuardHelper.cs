namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineSwitchGuardHelper
{
    internal enum SwitchDecisionKind
    {
        Proceed,
        MissingDefaultDevice,
        AlreadyTarget,
    }

    internal readonly record struct SwitchDecision(
        SwitchDecisionKind Kind,
        AppViewModel.RoutineDeviceSwitchExecutionResult Result,
        string? CurrentDeviceId = null,
        string? CurrentDeviceName = null);

    internal static SwitchDecision Evaluate(string? currentDeviceId, string? currentDeviceName, string? targetDeviceId, bool isOutput)
    {
        if (string.IsNullOrWhiteSpace(currentDeviceId))
        {
            return new SwitchDecision(
                SwitchDecisionKind.MissingDefaultDevice,
                new AppViewModel.RoutineDeviceSwitchExecutionResult(false, null, FailureDetail: $"No default {(isOutput ? "output" : "input")} device is available."));
        }

        if (string.Equals(currentDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return new SwitchDecision(
                SwitchDecisionKind.AlreadyTarget,
                new AppViewModel.RoutineDeviceSwitchExecutionResult(true, currentDeviceName),
                currentDeviceId,
                currentDeviceName);
        }

        return new SwitchDecision(
            SwitchDecisionKind.Proceed,
            default,
            currentDeviceId,
            currentDeviceName);
    }
}
