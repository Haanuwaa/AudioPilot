using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class PackagedAppPickerViewModelTests
{
    [Fact]
    public void EmptySearchResults_AreDistinguishedFromAnEmptyInventory()
    {
        var viewModel = new PackagedAppPickerViewModel([new("Spotify", "Spotify!App", "Spotify", "App")]) { SearchText = "missing" };
        Assert.True(viewModel.HasNoFilteredApps);
        Assert.Equal("No apps match your search. Try a different name or app ID.", viewModel.EmptyStateText);
        viewModel.ReplaceApps([]);
        Assert.Contains("No packaged apps were detected", viewModel.EmptyStateText);
    }

    [Fact]
    public void Search_PreservesTypedSpacesAndAvoidsResettingUnchangedResults()
    {
        var app = new AudioDeviceHelper.PackagedAppIdentity("Music Player", "Music!App", "Music", "App");
        var viewModel = new PackagedAppPickerViewModel([app]) { SelectedApp = app };
        int notifications = 0;
        viewModel.FilteredApps.CollectionChanged += (_, _) => notifications++;
        viewModel.SearchText = "Music ";
        Assert.Equal("Music ", viewModel.SearchText);
        viewModel.SearchText += "Player";
        viewModel.ReplaceApps([app]);
        Assert.Equal(0, notifications);
        Assert.Equal(app, viewModel.SelectedApp);
        Assert.True(viewModel.CanConfirmSelection);
    }

    [Fact]
    public void Filtering_RetainsVisibleSelectionAndClearsSelectionWhenHidden()
    {
        AudioDeviceHelper.PackagedAppIdentity[] apps = [new("Music", "Music!App", "Music", "App"), new("Video", "Video!App", "Video", "App")];
        var viewModel = new PackagedAppPickerViewModel(apps) { SelectedApp = apps[1], SearchText = "video" };
        Assert.Equal(apps[1], viewModel.SelectedApp);
        Assert.Single(viewModel.FilteredApps);
        viewModel.SearchText = "music";
        Assert.Null(viewModel.SelectedApp);
        Assert.False(viewModel.CanConfirmSelection);
    }

    [Fact]
    public void ConfirmSelection_RejectsInvalidOrForeignRecords()
    {
        var viewModel = new PackagedAppPickerViewModel([default]);
        Assert.Empty(viewModel.FilteredApps);
        viewModel.SelectedApp = default(AudioDeviceHelper.PackagedAppIdentity);
        Assert.False(viewModel.ConfirmSelection());
        viewModel.SelectedApp = new("Missing", "Missing!App", "Missing", "App");
        Assert.False(viewModel.ConfirmSelection());
        Assert.Empty(viewModel.ConfirmedAppUserModelId);
    }

    [Fact]
    public void ReplaceApps_LeavesExistingInventoryUsableIfEnumerationFails()
    {
        var app = new AudioDeviceHelper.PackagedAppIdentity("Music", "Music!App", "Music", "App");
        var viewModel = new PackagedAppPickerViewModel([app]) { SelectedApp = app };
        Assert.Throws<InvalidOperationException>(() => viewModel.ReplaceApps(FailingInventory()));
        viewModel.SearchText = "music";
        Assert.Equal(app, Assert.Single(viewModel.FilteredApps));
        Assert.True(viewModel.ConfirmSelection());

        static IEnumerable<AudioDeviceHelper.PackagedAppIdentity> FailingInventory()
        {
            yield return new("Other", "Other!App", "Other", "App");
            throw new InvalidOperationException("Inventory failed");
        }
    }

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
