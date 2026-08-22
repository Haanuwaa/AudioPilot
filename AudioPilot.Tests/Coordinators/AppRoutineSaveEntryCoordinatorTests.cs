using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRoutineSaveEntryCoordinatorTests
{
    [Fact]
    public void ValidateSave_Fails_WhenSettingsAreUnavailable()
    {
        RoutineSaveValidationResult result = AppRoutineSaveEntryCoordinator.ValidateSave(
            cachedSettings: null,
            newSettings: null,
            _ => new AppViewModel.SettingsCommitValidationResult(false, []));

        Assert.False(result.CanProceed);
        Assert.Contains("Settings are not loaded yet", result.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSave_Fails_WhenCommitValidationFindsBlockingIssues()
    {
        RoutineSaveValidationResult result = AppRoutineSaveEntryCoordinator.ValidateSave(
            new Settings(),
            new Settings(),
            _ => new AppViewModel.SettingsCommitValidationResult(true, ["- invalid routine"]));

        Assert.False(result.CanProceed);
        Assert.Contains("Please fix these settings before saving", result.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSaveSuccessSideEffects_AppliesSideEffects_AndShowsSuccess()
    {
        int persistCalls = 0;
        Settings? registeredSettings = null;
        Settings? appliedSettings = null;
        (string message, string caption)? success = null;
        Settings newSettings = new()
        {
            Routines = new RoutinesSettings { Items = [new AudioRoutine { Id = "routine-1", Name = "Routine" }] }
        };

        AppRoutineSaveEntryCoordinator.RunSaveSuccessSideEffects(
            newSettings,
            () => persistCalls++,
            settings => registeredSettings = settings,
            routines => appliedSettings = new Settings { Routines = new RoutinesSettings { Items = AudioPilot.ViewModels.AppViewModel.CloneRoutines(routines) } },
            (message, caption) => success = (message, caption));

        Assert.Equal(1, persistCalls);
        Assert.Same(newSettings, registeredSettings);
        Assert.NotNull(appliedSettings);
        Assert.Single(appliedSettings!.Routines.Items);
        Assert.Equal(("Routine changes applied successfully.", "Success"), success);
    }

    [Fact]
    public void ValidateSave_RequestsConfirmation_WhenRoutineConflictsExist()
    {
        Settings settings = new()
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.DeviceChange,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                    },
                    new AudioRoutine
                    {
                        Id = "routine-2",
                        Name = "Headset",
                        Enabled = true,
                        TriggerKind = RoutineTriggerKind.DeviceChange,
                        OutputDeviceId = "out-2",
                        OutputDeviceName = "Headset",
                    }
                ]
            }
        };

        RoutineSaveValidationResult result = AppRoutineSaveEntryCoordinator.ValidateSave(
            settings,
            settings,
            _ => new AppViewModel.SettingsCommitValidationResult(false, []));

        Assert.True(result.CanProceed);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("can conflict at runtime", result.ConfirmationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldProceedWithConfirmation_ReturnsFalse_WhenUserDeclines()
    {
        RoutineSaveValidationResult validationResult = new(
            CanProceed: true,
            WarningMessage: null,
            WarningCaption: DialogText.Captions.Success,
            RequiresConfirmation: true,
            ConfirmationMessage: "conflict",
            ConfirmationCaption: DialogText.Captions.Warning);

        bool result = await AppRoutineSaveEntryCoordinator.ShouldProceedWithConfirmationAsync(
            validationResult,
            static (_, _) => Task.FromResult(AppDialogResult.Declined));

        Assert.False(result);
    }
}
