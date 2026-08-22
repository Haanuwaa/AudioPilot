using AudioPilot.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Helpers;

public sealed class DialogWindowHelperTests
{
    [Fact]
    public void CalculateCenteredPosition_CentersWithinOwnerBounds()
    {
        DialogScreenPosition position = DialogWindowHelper.CalculateCenteredPosition(
            new DialogScreenRect(100, 100, 1100, 900),
            new DialogScreenRect(0, 0, 1920, 1040),
            dialogWidth: 600,
            dialogHeight: 400);

        Assert.Equal(new DialogScreenPosition(300, 300), position);
    }

    [Fact]
    public void CalculateCenteredPosition_ClampsDialogToMonitorWorkArea()
    {
        DialogScreenPosition position = DialogWindowHelper.CalculateCenteredPosition(
            new DialogScreenRect(1750, 900, 1950, 1100),
            new DialogScreenRect(0, 0, 1920, 1040),
            dialogWidth: 620,
            dialogHeight: 520);

        Assert.Equal(new DialogScreenPosition(1300, 520), position);
    }

    [Fact]
    public void CalculateCenteredPosition_AnchorsOversizedDialogAtWorkAreaOrigin()
    {
        DialogScreenPosition position = DialogWindowHelper.CalculateCenteredPosition(
            new DialogScreenRect(-1500, 100, -500, 900),
            new DialogScreenRect(-1600, 0, 0, 900),
            dialogWidth: 1800,
            dialogHeight: 1000);

        Assert.Equal(new DialogScreenPosition(-1600, 0), position);
    }

    [Fact]
    public void CalculateBoundedSize_LeavesNormalDialogUnchanged()
    {
        DialogScreenSize size = DialogWindowHelper.CalculateBoundedSize(
            new DialogScreenRect(0, 0, 1920, 1040),
            dialogWidth: 600,
            dialogHeight: 400);

        Assert.Equal(new DialogScreenSize(600, 400), size);
    }

    [Fact]
    public void CalculateBoundedSize_ReservesMarginOnSmallWorkArea()
    {
        DialogScreenSize size = DialogWindowHelper.CalculateBoundedSize(
            new DialogScreenRect(100, 50, 740, 410),
            dialogWidth: 960,
            dialogHeight: 900,
            margin: 16);

        Assert.Equal(new DialogScreenSize(608, 328), size);
    }

    [Fact]
    public void ResolveConfirmationDecision_ReturnsShouldConfirm_WhenPackagedAppSelectionIsAvailable()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.App", "Spotify.Package", "Spotify")
        ]);
        viewModel.TrySelectAppUserModelId("Spotify.App");

        DialogConfirmationDecision decision = DialogWindowHelper.ResolveConfirmationDecision<PackagedAppPickerViewModel>(
            viewModel,
            static current => current.CanConfirmSelection);

        Assert.True(decision.HasExpectedViewModel);
        Assert.True(decision.CanConfirm);
        Assert.True(decision.ShouldConfirm);
        Assert.Equal("Spotify.App", viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void ResolveConfirmationDecision_ReturnsCannotConfirm_WhenPackagedAppSelectionIsUnavailable()
    {
        var viewModel = new PackagedAppPickerViewModel([])
        {
            SelectedApp = null
        };

        DialogConfirmationDecision decision = DialogWindowHelper.ResolveConfirmationDecision<PackagedAppPickerViewModel>(
            viewModel,
            static current => current.CanConfirmSelection);

        Assert.True(decision.HasExpectedViewModel);
        Assert.False(decision.CanConfirm);
        Assert.False(decision.ShouldConfirm);
        Assert.Equal(string.Empty, viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void ResolveConfirmationDecision_ReturnsMissingViewModel_WhenDataContextTypeDoesNotMatch()
    {
        DialogConfirmationDecision decision = DialogWindowHelper.ResolveConfirmationDecision<PackagedAppPickerViewModel>(
            dataContext: new object(),
            static current => current.CanConfirmSelection);

        Assert.False(decision.HasExpectedViewModel);
        Assert.False(decision.CanConfirm);
        Assert.False(decision.ShouldConfirm);
    }
}
