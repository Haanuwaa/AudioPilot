using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineCompletionDecisionHelperTests
{
    [Fact]
    public void Decide_SuppressesOverlay_WhenAwaitingAppCompletion()
    {
        var decision = AppViewModelRoutineCompletionDecisionHelper.Decide(true, "Desk", "Speakers", null,
            new RoutineExecutionResult(true, "Speakers", null, AwaitingAppCompletion: true, AppOutputApplied: true, OutputSucceeded: true));
        Assert.False(decision.ShowFailureOverlay);
        Assert.False(decision.ShowSuccessOverlay);
    }

    [Fact]
    public void Decide_SuppressesFailureOverlay_WhenOverlaysDisabled()
    {
        var decision = AppViewModelRoutineCompletionDecisionHelper.Decide(false, "Desk", null, null,
            new RoutineExecutionResult(false, null, null, MasterVolumeSucceeded: false, MicVolumeSucceeded: true));
        Assert.False(decision.ShowSuccessOverlay);
        Assert.False(decision.ShowFailureOverlay);
    }
}
