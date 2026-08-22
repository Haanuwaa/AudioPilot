using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class PackagedAppPickerViewModelTests
{
    [Fact]
    public void Constructor_DoesNotSelectAnyApp_WhenAppsExist()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.Package!App", "Spotify.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App")
        ]);

        Assert.Null(viewModel.SelectedApp);
        Assert.Equal(string.Empty, viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void SearchText_ClearsSelection_WhenSelectedAppIsFilteredOut()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.Package!App", "Spotify.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App")
        ])
        {
            SelectedApp = new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App"),
            SearchText = "Spot"
        };

        Assert.Null(viewModel.SelectedApp);
        Assert.Equal(string.Empty, viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void SearchText_DoesNotAutoSelectFirstItem_WhenSelectionIsEmpty()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.Package!App", "Spotify.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App")
        ])
        {
            SearchText = "Spot"
        };

        Assert.Null(viewModel.SelectedApp);
        Assert.Equal(string.Empty, viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void ReplaceApps_PreservesSelection_WhenMatchingAppStillExists()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.Package!App", "Spotify.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App")
        ]);

        viewModel.TrySelectAppUserModelId("Discord.Package!App");
        viewModel.ReplaceApps(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Contoso", "Contoso.Package!App", "Contoso.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App")
        ]);

        Assert.Equal("Discord.Package!App", viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void TrySelectAppUserModelId_SelectsMatchingApp_WhenPresent()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.Package!App", "Spotify.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App")
        ]);

        viewModel.TrySelectAppUserModelId("Discord.Package!App");

        Assert.Equal("Discord.Package!App", viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void ReplaceApps_PreservesProvidedOrder()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Zulu", "Zulu.Package!App", "Zulu.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Alpha", "Alpha.Package!App", "Alpha.Package", "App")
        ]);

        Assert.Collection(
            viewModel.FilteredApps,
            first => Assert.Equal("Zulu.Package!App", first.AppUserModelId),
            second => Assert.Equal("Alpha.Package!App", second.AppUserModelId));
    }

    [Fact]
    public void ConfirmSelection_PreservesConfirmedResult_WhenAppsAreClearedForWindowClose()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.Package!App", "Spotify.Package", "App"),
            new AudioDeviceHelper.PackagedAppIdentity("Discord", "Discord.Package!App", "Discord.Package", "App")
        ]);
        viewModel.TrySelectAppUserModelId("Spotify.Package!App");

        bool confirmed = viewModel.ConfirmSelection();
        viewModel.ReplaceApps([]);

        Assert.True(confirmed);
        Assert.Equal(string.Empty, viewModel.SelectedAppUserModelId);
        Assert.Equal("Spotify.Package!App", viewModel.ConfirmedAppUserModelId);
        Assert.Equal("Spotify", viewModel.ConfirmedDisplayName);
    }

    [Fact]
    public void ConfirmSelection_ReturnsFalse_WhenSelectionIsEmpty()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.Package!App", "Spotify.Package", "App")
        ]);

        bool confirmed = viewModel.ConfirmSelection();

        Assert.False(confirmed);
        Assert.Equal(string.Empty, viewModel.ConfirmedAppUserModelId);
        Assert.Equal(string.Empty, viewModel.ConfirmedDisplayName);
    }
}
