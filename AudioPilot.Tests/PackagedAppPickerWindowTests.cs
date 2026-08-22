using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

public sealed class PackagedAppPickerWindowTests
{
    [Fact]
    public void ResetAppsForClose_ClearsViewModelApps()
    {
        var apps =
            new[]
            {
                new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify!App", "Spotify", "App"),
                new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord!App", "Discord", "App"),
            };
        var viewModel = new PackagedAppPickerViewModel(apps);

        Assert.Equal(2, viewModel.FilteredApps.Count);

        PackagedAppPickerWindow.ResetAppsForClose(viewModel);

        Assert.Empty(viewModel.FilteredApps);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void ShouldClearSelectionForPreviewMouseDown_ReturnsExpectedDecision(
        bool sourceIsInAppsList,
        bool sourceIsInAppsFrame,
        bool expected)
    {
        bool actual = PackagedAppPickerWindow.ShouldClearSelectionForPreviewMouseDownForTests(
            sourceIsInAppsList,
            sourceIsInAppsFrame);

        Assert.Equal(expected, actual);
    }
}
