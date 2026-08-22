namespace AudioPilot.ViewModels;

internal static class AppViewModelRoutineCompletionDecisionHelper
{
    internal readonly record struct RoutineCompletionDecision(
        AppViewModel.RoutineExecutionResult Result,
        bool ShowSuccessOverlay = false,
        AppViewModelRoutineOverlayHelper.RoutineSuccessOverlayPlan SuccessOverlayPlan = default,
        bool ShowFailureOverlay = false,
        AppViewModelRoutineOverlayHelper.RoutineFailureOverlayPlan FailureOverlayPlan = default);

    internal static RoutineCompletionDecision Decide(
        bool showOverlay,
        string? routineName,
        string? configuredOutputName,
        string? configuredInputName,
        string? appliedOutputDeviceName,
        string? appliedInputDeviceName,
        bool awaitingAppCompletion,
        bool appOutputApplied,
        bool appInputApplied,
        bool? outputSucceeded,
        bool? inputSucceeded,
        bool? masterVolumeSucceeded,
        bool? micVolumeSucceeded,
        string? outputFailureDetail = null,
        string? inputFailureDetail = null)
    {
        AppViewModel.RoutineExecutionResult initialResult = BuildResult(
            appliedOutputDeviceName,
            appliedInputDeviceName,
            awaitingAppCompletion,
            appOutputApplied,
            appInputApplied,
            outputSucceeded,
            inputSucceeded,
            masterVolumeSucceeded,
            micVolumeSucceeded,
            outputFailureDetail,
            inputFailureDetail);

        if (!showOverlay)
        {
            return new RoutineCompletionDecision(initialResult);
        }

        if (!initialResult.Success)
        {
            bool showFailureOverlay = AppViewModelRoutineOverlayHelper.TryBuildRoutineFailureOverlayPlan(
                routineName,
                configuredOutputName,
                configuredInputName,
                appliedOutputDeviceName,
                appliedInputDeviceName,
                outputSucceeded,
                inputSucceeded,
                out AppViewModelRoutineOverlayHelper.RoutineFailureOverlayPlan failureOverlayPlan);

            return new RoutineCompletionDecision(
                initialResult,
                ShowFailureOverlay: showFailureOverlay,
                FailureOverlayPlan: failureOverlayPlan);
        }

        AppViewModel.RoutineExecutionResult successResult = BuildSuccessResult(
            appliedOutputDeviceName,
            appliedInputDeviceName,
            awaitingAppCompletion,
            appOutputApplied,
            appInputApplied,
            outputSucceeded,
            inputSucceeded,
            masterVolumeSucceeded,
            micVolumeSucceeded,
            outputFailureDetail,
            inputFailureDetail);

        if (successResult.AwaitingAppCompletion)
        {
            return new RoutineCompletionDecision(successResult);
        }

        bool showSuccessOverlay = AppViewModelRoutineOverlayHelper.TryBuildRoutineSuccessOverlayPlan(
            routineName,
            appliedOutputDeviceName,
            appliedInputDeviceName,
            out AppViewModelRoutineOverlayHelper.RoutineSuccessOverlayPlan successOverlayPlan);

        return new RoutineCompletionDecision(
            successResult,
            ShowSuccessOverlay: showSuccessOverlay,
            SuccessOverlayPlan: successOverlayPlan);
    }

    private static AppViewModel.RoutineExecutionResult BuildResult(
        string? outputDeviceName,
        string? inputDeviceName,
        bool awaitingAppCompletion,
        bool appOutputApplied,
        bool appInputApplied,
        bool? outputSucceeded,
        bool? inputSucceeded,
        bool? masterVolumeSucceeded,
        bool? micVolumeSucceeded,
        string? outputFailureDetail,
        string? inputFailureDetail)
    {
        bool success = (outputSucceeded ?? true) && (inputSucceeded ?? true) && (masterVolumeSucceeded ?? true) && (micVolumeSucceeded ?? true);
        return new AppViewModel.RoutineExecutionResult(
            success,
            outputDeviceName,
            inputDeviceName,
            awaitingAppCompletion,
            appOutputApplied,
            appInputApplied,
            outputSucceeded,
            inputSucceeded,
            masterVolumeSucceeded,
                micVolumeSucceeded,
                OutputFailureDetail: outputFailureDetail,
                InputFailureDetail: inputFailureDetail);
    }

    private static AppViewModel.RoutineExecutionResult BuildSuccessResult(
        string? outputDeviceName,
        string? inputDeviceName,
        bool awaitingAppCompletion = false,
        bool appOutputApplied = false,
        bool appInputApplied = false,
        bool? outputSucceeded = null,
        bool? inputSucceeded = null,
        bool? masterVolumeSucceeded = null,
        bool? micVolumeSucceeded = null,
        string? outputFailureDetail = null,
        string? inputFailureDetail = null)
    {
        return new AppViewModel.RoutineExecutionResult(
            true,
            outputDeviceName,
            inputDeviceName,
            awaitingAppCompletion,
            appOutputApplied,
            appInputApplied,
            outputSucceeded,
            inputSucceeded,
            masterVolumeSucceeded,
            micVolumeSucceeded,
            OutputFailureDetail: outputFailureDetail,
            InputFailureDetail: inputFailureDetail);
    }
}
