using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineOutputSwitchGuardHelperTests
{
    [Fact]
    public void Evaluate_ReturnsMissingDefault_WhenCurrentDeviceMissing()
    {
        AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecision decision = AppViewModelRoutineOutputSwitchGuardHelper.Evaluate(
            currentDeviceId: null,
            currentDeviceName: null,
            targetDeviceId: "out-1");

        Assert.Equal(AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecisionKind.MissingDefaultDevice, decision.Kind);
        Assert.False(decision.Result.Success);
    }

    [Fact]
    public void Evaluate_ReturnsAlreadyTarget_WhenCurrentMatchesTarget()
    {
        AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecision decision = AppViewModelRoutineOutputSwitchGuardHelper.Evaluate(
            currentDeviceId: "out-1",
            currentDeviceName: "Speakers",
            targetDeviceId: "out-1");

        Assert.Equal(AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecisionKind.AlreadyTarget, decision.Kind);
        Assert.True(decision.Result.Success);
        Assert.Equal("Speakers", decision.CurrentDeviceName);
    }

    [Fact]
    public void Evaluate_ReturnsProceed_WhenSwitchShouldContinue()
    {
        AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecision decision = AppViewModelRoutineOutputSwitchGuardHelper.Evaluate(
            currentDeviceId: "out-1",
            currentDeviceName: "Speakers",
            targetDeviceId: "out-2");

        Assert.Equal(AppViewModelRoutineOutputSwitchGuardHelper.OutputSwitchDecisionKind.Proceed, decision.Kind);
        Assert.Equal("Speakers", decision.CurrentDeviceName);
    }
}
