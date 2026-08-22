using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineCompletionDecisionHelperTests
{
    [Fact]
    public void Decide_SuppressesOverlay_WhenAwaitingAppCompletion()
    {
        AppViewModelRoutineCompletionDecisionHelper.RoutineCompletionDecision decision = AppViewModelRoutineCompletionDecisionHelper.Decide(
            showOverlay: true,
            routineName: "Desk",
            configuredOutputName: "Speakers",
            configuredInputName: null,
            appliedOutputDeviceName: "Speakers",
            appliedInputDeviceName: null,
            awaitingAppCompletion: true,
            appOutputApplied: true,
            appInputApplied: false,
            outputSucceeded: true,
            inputSucceeded: null,
            masterVolumeSucceeded: null,
            micVolumeSucceeded: null);

        Assert.True(decision.Result.Success);
        Assert.True(decision.Result.AwaitingAppCompletion);
        Assert.False(decision.ShowFailureOverlay);
        Assert.False(decision.ShowSuccessOverlay);
    }

    [Fact]
    public void Decide_ReturnsFailedResult_WhenVolumeTargetFails()
    {
        AppViewModelRoutineCompletionDecisionHelper.RoutineCompletionDecision decision = AppViewModelRoutineCompletionDecisionHelper.Decide(
            showOverlay: false,
            routineName: "Desk",
            configuredOutputName: null,
            configuredInputName: null,
            appliedOutputDeviceName: null,
            appliedInputDeviceName: null,
            awaitingAppCompletion: false,
            appOutputApplied: false,
            appInputApplied: false,
            outputSucceeded: null,
            inputSucceeded: null,
            masterVolumeSucceeded: false,
            micVolumeSucceeded: true);

        Assert.False(decision.Result.Success);
        Assert.False(decision.ShowSuccessOverlay);
        Assert.False(decision.ShowFailureOverlay);
    }
}
