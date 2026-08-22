using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSaveValidationCoordinatorTests
{
    [Fact]
    public void Validate_Succeeds_WhenEditedOutputCycleHasNoDevicesAndNoRemainingSwitchHotkey()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "", OutputEdited: true, InputEdited: false, OutputCleared: false, InputCleared: false),
                OutputCycleCount: 0,
                InputCycleCount: 0,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                HasOutputHotkey: false,
                HasInputHotkey: false,
                OverlayDurationValid: true));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Fails_WhenOutputCycleIsClearedButOutputHotkeysRemainEnabled()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("Ctrl+Alt+O", "", OutputEdited: true, InputEdited: false, OutputCleared: true, InputCleared: false),
                OutputCycleCount: 0,
                InputCycleCount: 2,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                HasOutputHotkey: true,
                HasInputHotkey: true,
                OverlayDurationValid: true));

        Assert.False(result.IsValid);
        Assert.Equal("output-cycle-empty", result.LogReason);
        Assert.Equal("Please add at least one output device before saving.", result.WarningMessage);
    }

    [Fact]
    public void Validate_Fails_WhenEditedInputCycleNeedsHotkey()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "", OutputEdited: false, InputEdited: true, OutputCleared: false, InputCleared: false),
                OutputCycleCount: 2,
                InputCycleCount: 2,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                HasOutputHotkey: true,
                HasInputHotkey: false,
                OverlayDurationValid: true));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Fails_WhenInputCycleIsClearedButInputHotkeysRemainEnabled()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "Ctrl+Alt+I", OutputEdited: false, InputEdited: true, OutputCleared: false, InputCleared: true),
                OutputCycleCount: 2,
                InputCycleCount: 0,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                HasOutputHotkey: true,
                HasInputHotkey: true,
                OverlayDurationValid: true));

        Assert.False(result.IsValid);
        Assert.Equal("input-cycle-empty", result.LogReason);
        Assert.Equal("Please add at least one input device before saving.", result.WarningMessage);
    }

    [Fact]
    public void Validate_Fails_WhenOverlayDurationIsInvalid()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "", OutputEdited: false, InputEdited: false, OutputCleared: false, InputCleared: false),
                OutputCycleCount: 2,
                InputCycleCount: 2,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                HasOutputHotkey: true,
                HasInputHotkey: true,
                OverlayDurationValid: false));

        Assert.False(result.IsValid);
        Assert.Equal("overlay-duration-invalid", result.LogReason);
    }

    [Fact]
    public void Validate_Succeeds_WhenEditedCyclesAreValid()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "", OutputEdited: true, InputEdited: true, OutputCleared: false, InputCleared: false),
                OutputCycleCount: 2,
                InputCycleCount: 2,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                HasOutputHotkey: true,
                HasInputHotkey: true,
                OverlayDurationValid: true));

        Assert.True(result.IsValid);
        Assert.Null(result.LogReason);
    }

    [Fact]
    public void Validate_Succeeds_WhenInputCycleIsDisabledAndEmpty()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "", OutputEdited: false, InputEdited: true, OutputCleared: false, InputCleared: false),
                OutputCycleCount: 2,
                InputCycleCount: 0,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: false,
                HasOutputHotkey: true,
                HasInputHotkey: true,
                OverlayDurationValid: true));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Succeeds_WhenOutputCycleIsDisabledAndEmpty()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "", OutputEdited: true, InputEdited: false, OutputCleared: false, InputCleared: false),
                OutputCycleCount: 0,
                InputCycleCount: 2,
                OutputHotkeysEnabled: false,
                InputHotkeysEnabled: true,
                HasOutputHotkey: true,
                HasInputHotkey: true,
                OverlayDurationValid: true));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Succeeds_WhenClearedOutputCycleHasNoRemainingSwitchHotkeys()
    {
        SaveValidationResult result = AppSaveValidationCoordinator.Validate(
            new SaveValidationInput(
                new SaveEditState("", "", OutputEdited: true, InputEdited: false, OutputCleared: true, InputCleared: false),
                OutputCycleCount: 0,
                InputCycleCount: 2,
                OutputHotkeysEnabled: true,
                InputHotkeysEnabled: true,
                HasOutputHotkey: false,
                HasInputHotkey: true,
                OverlayDurationValid: true));

        Assert.True(result.IsValid);
    }
}
