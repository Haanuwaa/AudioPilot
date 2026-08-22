using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineCompletionDecisionHelper
{
    internal readonly record struct RoutineCompletionDecision(
        bool ShowSuccessOverlay = false,
        AppViewModelRoutineOverlayHelper.RoutineSuccessOverlayPlan SuccessOverlayPlan = default,
        bool ShowFailureOverlay = false,
        AppViewModelRoutineOverlayHelper.RoutineFailureOverlayPlan FailureOverlayPlan = default);

    internal static RoutineCompletionDecision Decide(
        bool showOverlay, string? routineName, string? configuredOutputName, string? configuredInputName,
        RoutineExecutionResult result)
    {
        if (!showOverlay) return default;
        if (!result.Success)
        {
            bool showFailure = AppViewModelRoutineOverlayHelper.TryBuildRoutineFailureOverlayPlan(
                routineName, configuredOutputName, configuredInputName, result.OutputDeviceName, result.InputDeviceName,
                result.OutputSucceeded, result.InputSucceeded, out var failurePlan);
            return new(ShowFailureOverlay: showFailure, FailureOverlayPlan: failurePlan);
        }
        if (result.AwaitingAppCompletion) return default;
        bool showSuccess = AppViewModelRoutineOverlayHelper.TryBuildRoutineSuccessOverlayPlan(
            routineName, result.OutputDeviceName, result.InputDeviceName, out var successPlan);
        return new(ShowSuccessOverlay: showSuccess, SuccessOverlayPlan: successPlan);
    }
}
