using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineOverlayHelperTests
{
    [Fact]
    public void BuildRoutineOverlayDisplay_PutsOutputFirst_AndAddsCombinedSuffix()
    {
        AppViewModel.RoutineOverlayDisplay display = AppViewModelRoutineOverlayHelper.BuildRoutineOverlayDisplay(
            "Routine 1",
            "Speakers",
            "Microphone");

        Assert.Equal("Routine 1 - Output/Input", display.Header);
        Assert.Equal("Speakers", display.OutputDeviceName);
        Assert.Equal("Microphone", display.InputDeviceName);
    }

    [Fact]
    public void BuildRoutineOverlayDisplay_TrimsHeaderToFifteenCharacters()
    {
        AppViewModel.RoutineOverlayDisplay display = AppViewModelRoutineOverlayHelper.BuildRoutineOverlayDisplay(
            "12345678901234567890",
            "Speakers",
            null);

        Assert.Equal("12345678901234567890 - Output", display.Header);
    }

    [Fact]
    public void NormalizeRoutineOverlayName_FallsBackToRoutine_WhenNameMissing()
    {
        string trimmed = AppViewModelRoutineOverlayHelper.NormalizeRoutineOverlayName(null);

        Assert.Equal("Routine", trimmed);
    }

    [Theory]
    [InlineData("Routine 1", "Speakers", null, "Routine 1 - Output")]
    [InlineData("Routine 1", null, "Microphone", "Routine 1 - Input")]
    [InlineData("Routine 1", "Speakers", "Microphone", "Routine 1 - Output/Input")]
    public void BuildRoutineOverlayDisplay_UsesSuffixBasedOnAvailableTargets(
        string routineName,
        string? outputDeviceName,
        string? inputDeviceName,
        string expectedHeader)
    {
        AppViewModel.RoutineOverlayDisplay display = AppViewModelRoutineOverlayHelper.BuildRoutineOverlayDisplay(
            routineName,
            outputDeviceName,
            inputDeviceName);

        Assert.Equal(expectedHeader, display.Header);
    }

    [Fact]
    public void ResolveRoutineOverlayDeviceName_FallsBackToConfiguredName_WhenPreferredMissing()
    {
        string? result = AppViewModelRoutineOverlayHelper.ResolveRoutineOverlayDeviceName(
            shouldInclude: true,
            preferredName: null,
            configuredName: "Headset",
            fallbackLabel: "Output device");

        Assert.Equal("Headset", result);
    }

    [Fact]
    public void ResolveRoutineOverlayDeviceName_FallsBackToGenericLabel_WhenNamesMissing()
    {
        string? result = AppViewModelRoutineOverlayHelper.ResolveRoutineOverlayDeviceName(
            shouldInclude: true,
            preferredName: "  ",
            configuredName: null,
            fallbackLabel: "Input device");

        Assert.Equal("Input device", result);
    }

    [Fact]
    public void ResolveRoutineOverlayDeviceName_ReturnsNull_WhenDeviceShouldNotBeIncluded()
    {
        string? result = AppViewModelRoutineOverlayHelper.ResolveRoutineOverlayDeviceName(
            shouldInclude: false,
            preferredName: "Speakers",
            configuredName: "Headset",
            fallbackLabel: "Output device");

        Assert.Null(result);
    }

    [Fact]
    public void TryBuildRoutineSuccessOverlayPlan_ReturnsCombinedPlan_WhenBothDevicesPresent()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineSuccessOverlayPlan(
            "Routine 1",
            "Speakers",
            "Microphone",
            out var plan);

        Assert.True(built);
        Assert.True(plan.ShowCombined);
        Assert.Equal("Routine 1 - Output/Input", plan.Header);
        Assert.Equal("Speakers", plan.OutputDeviceName);
        Assert.Equal("Microphone", plan.InputDeviceName);
    }

    [Fact]
    public void TryBuildRoutineSuccessOverlayPlan_ReturnsSingleOutputPlan_WhenOnlyOutputPresent()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineSuccessOverlayPlan(
            "Routine 1",
            "Speakers",
            null,
            out var plan);

        Assert.True(built);
        Assert.False(plan.ShowCombined);
        Assert.Equal(OverlayDeviceKind.Output, plan.Kind);
        Assert.Equal("Routine 1 - Output", plan.Header);
        Assert.Equal("Speakers", plan.DeviceName);
    }

    [Fact]
    public void TryBuildRoutineSuccessOverlayPlan_ReturnsFalse_WhenNoDevicesPresent()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineSuccessOverlayPlan(
            "Routine 1",
            null,
            null,
            out var plan);

        Assert.False(built);
        Assert.Equal(default, plan);
    }

    [Fact]
    public void TryBuildRoutineRestoreOverlayPlan_ReturnsCombinedPlan_WhenBothDevicesRestored()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineRestoreOverlayPlan(
            "Speakers",
            "Microphone",
            out var plan);

        Assert.True(built);
        Assert.True(plan.ShowCombined);
        Assert.Equal("Restored audio", plan.Header);
        Assert.Equal("Speakers", plan.OutputDeviceName);
        Assert.Equal("Microphone", plan.InputDeviceName);
    }

    [Fact]
    public void TryBuildRoutineRestoreOverlayPlan_ReturnsSingleOutputPlan_WhenOnlyOutputRestored()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineRestoreOverlayPlan(
            "Speakers",
            null,
            out var plan);

        Assert.True(built);
        Assert.False(plan.ShowCombined);
        Assert.Equal(OverlayDeviceKind.Output, plan.Kind);
        Assert.Equal("Restored output device", plan.Header);
        Assert.Equal("Speakers", plan.DeviceName);
    }

    [Fact]
    public void TryBuildRoutineRestoreOverlayPlan_ReturnsFalse_WhenNoDevicesRestored()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineRestoreOverlayPlan(
            null,
            " ",
            out var plan);

        Assert.False(built);
        Assert.Equal(default, plan);
    }

    [Fact]
    public void TryBuildRoutineFailureOverlayPlan_ReturnsPartialPlan_ForMixedSuccess()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineFailureOverlayPlan(
            routineName: "Desk",
            configuredOutputName: "Speakers",
            configuredInputName: "Microphone",
            appliedOutputDeviceName: "Desk Speakers",
            appliedInputDeviceName: null,
            outputSucceeded: true,
            inputSucceeded: false,
            out var plan);

        Assert.True(built);
        Assert.True(plan.IsPartial);
        Assert.Equal("Desk - Partial", plan.Header);
        Assert.Equal("Desk Speakers", plan.SuccessfulOutputName);
        Assert.Null(plan.SuccessfulInputName);
        Assert.Null(plan.FailedOutputName);
        Assert.Equal("Microphone", plan.FailedInputName);
    }

    [Fact]
    public void TryBuildRoutineFailureOverlayPlan_ReturnsCombinedFailurePlan_WhenBothFail()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineFailureOverlayPlan(
            routineName: "Desk",
            configuredOutputName: "Speakers",
            configuredInputName: "Microphone",
            appliedOutputDeviceName: null,
            appliedInputDeviceName: null,
            outputSucceeded: false,
            inputSucceeded: false,
            out var plan);

        Assert.True(built);
        Assert.False(plan.IsPartial);
        Assert.Equal(OverlayDeviceKind.Error, plan.Kind);
    }

    [Fact]
    public void TryBuildRoutineFailureOverlayPlan_ReturnsSingleFailurePlan_WhenOnlyOutputFails()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineFailureOverlayPlan(
            routineName: "Desk",
            configuredOutputName: " Speakers ",
            configuredInputName: null,
            appliedOutputDeviceName: null,
            appliedInputDeviceName: null,
            outputSucceeded: false,
            inputSucceeded: null,
            out var plan);

        Assert.True(built);
        Assert.False(plan.IsPartial);
        Assert.Equal("Routine output failed", plan.Header);
        Assert.Equal("Speakers", plan.DeviceName);
    }

    [Fact]
    public void TryBuildRoutineFailureOverlayPlan_ReturnsFalse_WhenNoFailuresPresent()
    {
        bool built = AppViewModelRoutineOverlayHelper.TryBuildRoutineFailureOverlayPlan(
            routineName: "Desk",
            configuredOutputName: "Speakers",
            configuredInputName: "Microphone",
            appliedOutputDeviceName: "Speakers",
            appliedInputDeviceName: "Microphone",
            outputSucceeded: true,
            inputSucceeded: true,
            out var plan);

        Assert.False(built);
        Assert.Equal(default, plan);
    }
}
