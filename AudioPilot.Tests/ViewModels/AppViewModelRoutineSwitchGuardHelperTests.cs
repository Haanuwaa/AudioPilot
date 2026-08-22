using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineSwitchGuardHelperTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_ReturnsMissingDefault_WhenCurrentDeviceMissing(bool isOutput)
    {
        AppViewModelRoutineSwitchGuardHelper.SwitchDecision decision = AppViewModelRoutineSwitchGuardHelper.Evaluate(
            currentDeviceId: null,
            currentDeviceName: null,
            targetDeviceId: "OUT-1",
            isOutput: isOutput);

        Assert.Equal(AppViewModelRoutineSwitchGuardHelper.SwitchDecisionKind.MissingDefaultDevice, decision.Kind);
        Assert.False(decision.Result.Success);
        Assert.Equal($"No default {(isOutput ? "output" : "input")} device is available.", decision.Result.FailureDetail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_ReturnsAlreadyTarget_WhenCurrentMatchesTarget(bool isOutput)
    {
        AppViewModelRoutineSwitchGuardHelper.SwitchDecision decision = AppViewModelRoutineSwitchGuardHelper.Evaluate(
            currentDeviceId: "out-1",
            currentDeviceName: "Speakers",
            targetDeviceId: "OUT-1",
            isOutput: isOutput);

        Assert.Equal(AppViewModelRoutineSwitchGuardHelper.SwitchDecisionKind.AlreadyTarget, decision.Kind);
        Assert.True(decision.Result.Success);
        Assert.Equal("Speakers", decision.CurrentDeviceName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_ReturnsProceed_WhenSwitchShouldContinue(bool isOutput)
    {
        AppViewModelRoutineSwitchGuardHelper.SwitchDecision decision = AppViewModelRoutineSwitchGuardHelper.Evaluate(
            currentDeviceId: "out-1",
            currentDeviceName: "Speakers",
            targetDeviceId: "out-2",
            isOutput: isOutput);

        Assert.Equal(AppViewModelRoutineSwitchGuardHelper.SwitchDecisionKind.Proceed, decision.Kind);
        Assert.Equal("Speakers", decision.CurrentDeviceName);
    }
}
