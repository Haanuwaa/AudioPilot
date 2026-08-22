using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSettingsEntryCoordinatorTests
{
    [Fact]
    public void TryValidateCommit_ShowsWarning_WhenBlockingIssuesExist()
    {
        string? message = null;
        string? caption = null;

        bool result = AppSettingsEntryCoordinator.TryValidateCommit(
            new Settings(),
            _ => "invalid-settings",
            _ => new AppViewModel.SettingsCommitValidationResult(true, ["- conflict"]),
            (warningMessage, warningCaption) =>
            {
                message = warningMessage;
                caption = warningCaption;
            },
            "Invalid Settings");

        Assert.False(result);
        Assert.Equal("invalid-settings", message);
        Assert.Equal("Invalid Settings", caption);
    }

    [Fact]
    public void ShowApplyResult_ShowsSuccess_WhenWarningsAreEmpty()
    {
        string? successMessage = null;
        string? successCaption = null;

        AppSettingsEntryCoordinator.ShowApplyResult(
            new ApplySettingsSideEffectResult([]),
            (_, _) => Assert.Fail("Warning should not be shown."),
            (message, caption) =>
            {
                successMessage = message;
                successCaption = caption;
            },
            "Warnings",
            "Success");

        Assert.Equal("Settings applied successfully.", successMessage);
        Assert.Equal("Success", successCaption);
    }

    [Fact]
    public void ShowSaveResult_ShowsWarning_WhenWarningsExist()
    {
        string? warningMessage = null;
        string? warningCaption = null;

        AppSettingsEntryCoordinator.ShowSaveResult(
            new SaveSettingsSideEffectResult(["Output disconnected: Headset"]),
            (message, caption) =>
            {
                warningMessage = message;
                warningCaption = caption;
            },
            (_, _) => Assert.Fail("Success should not be shown."),
            "Warnings",
            "Success");

        Assert.NotNull(warningMessage);
        Assert.Contains("Output disconnected: Headset", warningMessage, StringComparison.Ordinal);
        Assert.Equal("Warnings", warningCaption);
    }
}
