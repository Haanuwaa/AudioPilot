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
            hasStartup: false);

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
            hasStartup: false);

        Assert.False(plan.ShouldSkip);
        Assert.Equal(DialogText.Captions.ResetToDefaults, plan.DialogCaption);
        Assert.Contains("- Clear configured output and input device lists", plan.DialogMessage, StringComparison.Ordinal);
        Assert.Equal(AppDialogKind.Warning, plan.DialogKind);
    }

    [Fact]
    public void BuildResetSummary_SkipsRoutines_WhenNoRoutinesExist()
    {
        List<string> summary = AppSettingsWorkflowCoordinator.BuildResetSummary(
            hasDevicesSelected: false,
            hasRoutines: false,
            hasStartup: false,
            settingsFileExists: true);

        Assert.Contains("- Delete saved settings and settings recovery backups", summary);
        Assert.DoesNotContain("- Delete all routines", summary);
    }

    [Fact]
    public void BuildResetSummary_IncludesSavedRoutines_WhenRoutinesExist()
    {
        List<string> summary = AppSettingsWorkflowCoordinator.BuildResetSummary(
            hasDevicesSelected: false,
            hasRoutines: true,
            hasStartup: false,
            settingsFileExists: true);

        Assert.Contains("- Delete saved settings and settings recovery backups", summary);
        Assert.Contains("- Delete all routines", summary);
    }

    [Fact]
    public void ResetPrompt_ListsRemovalBeforeDefaultsAndStartup_AndExplainsRetainedData()
    {
        ResetDefaultsPromptPlan plan = AppResetCoordinator.BuildResetDefaultsPromptPlan(true, true, true, true, true, hasUnsavedChanges: true);
        Assert.Equal("Reset AudioPilot to its defaults?\n\nThis will:\n" +
            "- Delete saved settings and settings recovery backups\n" +
            "- Delete all routines\n" +
            "- Clear configured output and input device lists\n" +
            "- Discard unsaved changes\n" +
            "- Restore default app options and hotkeys\n" +
            "- Turn off starting AudioPilot with Windows\n\nWindows audio levels and logs will not be reset.", plan.DialogMessage);
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
