using System.Windows;
using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppResetCoordinatorTests
{
    [Fact]
    public void BuildResetDefaultsPromptPlan_ReturnsSkipPlan_WhenNothingToReset()
    {
        ResetDefaultsPromptPlan plan = AppResetCoordinator.BuildResetDefaultsPromptPlan(
            settingsFileExists: false,
            hasDevicesSelected: false,
            hasRoutines: false,
            hasHotkey: false,
            hasStartup: false,
            (_, _, _, _, _) => []);

        Assert.True(plan.ShouldSkip);
        Assert.Equal(DialogText.Captions.NothingToReset, plan.DialogCaption);
        Assert.Equal(AppDialogKind.Information, plan.DialogKind);
    }

    [Fact]
    public void BuildResetDefaultsPromptPlan_ReturnsConfirmationPlan_WhenResetIsAvailable()
    {
        ResetDefaultsPromptPlan plan = AppResetCoordinator.BuildResetDefaultsPromptPlan(
            settingsFileExists: true,
            hasDevicesSelected: true,
            hasRoutines: false,
            hasHotkey: false,
            hasStartup: false,
            (_, _, _, _, _) => ["- Devices"]);

        Assert.False(plan.ShouldSkip);
        Assert.Equal(DialogText.Captions.ResetToDefaults, plan.DialogCaption);
        Assert.Contains("- Devices", plan.DialogMessage, StringComparison.Ordinal);
        Assert.Equal(AppDialogKind.Warning, plan.DialogKind);
    }

    [Fact]
    public void BuildResetSummary_SkipsRoutines_WhenNoRoutinesExist()
    {
        List<string> summary = AppSettingsWorkflowCoordinator.BuildResetSummary(
            hasDevicesSelected: false,
            hasRoutines: false,
            hasHotkey: false,
            hasStartup: false,
            settingsFileExists: true);

        Assert.Contains("- Delete all saved settings", summary);
        Assert.DoesNotContain("- Delete all saved routines", summary);
    }

    [Fact]
    public void BuildResetSummary_IncludesSavedRoutines_WhenRoutinesExist()
    {
        List<string> summary = AppSettingsWorkflowCoordinator.BuildResetSummary(
            hasDevicesSelected: false,
            hasRoutines: true,
            hasHotkey: false,
            hasStartup: false,
            settingsFileExists: true);

        Assert.Contains("- Delete all saved settings", summary);
        Assert.Contains("- Delete all saved routines", summary);
    }

    [Theory]
    [InlineData(false, true, (int)ResetDialogKind.Error)]
    [InlineData(true, false, (int)ResetDialogKind.Info)]
    [InlineData(true, true, (int)ResetDialogKind.Success)]
    public void BuildPerAppRoutingDialogPlan_ReturnsExpectedDialogKind(bool success, bool hadAssignments, int expected)
    {
        ResetPerAppRoutingDialogPlan plan = AppResetCoordinator.BuildPerAppRoutingDialogPlan(
            new PerAppAudioRoutingResetResult(success, hadAssignments));

        Assert.Equal((ResetDialogKind)expected, plan.Kind);
        Assert.Equal(DialogText.Captions.ResetPerAppAudio, plan.Caption);
    }
}
