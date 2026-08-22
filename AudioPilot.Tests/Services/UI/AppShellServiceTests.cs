using AudioPilot.Services.UI;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppShellServiceTests
{
    [Fact]
    public void PrepareHiddenStartup_HidesWithoutPublishingTrayOrCreatingWindow()
    {
        var manager = new TestAppMainWindowManager();
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(manager, tray);

        bool result = shell.PrepareHiddenStartup();

        Assert.True(result);
        Assert.Equal(0, tray.EnsureVisibleCalls);
        Assert.Equal(1, manager.HideCalls);
        Assert.False(manager.IsCreated);
        Assert.Empty(manager.ShowTargets);
    }

    [Fact]
    public async Task ShowWindowFrontAndCenter_DelegatesToLazyWindowManager()
    {
        var manager = new TestAppMainWindowManager();
        var shell = new AppShellService(manager, new TestAppTrayIconService());

        bool result = await shell.ShowWindowFrontAndCenterAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal([MainWindowOpenTarget.Default], manager.ShowTargets);
    }

    [Fact]
    public void MinimizeToTray_ShowsOneBalloonOnlyWhenRequested()
    {
        var manager = new TestAppMainWindowManager { IsVisibleValue = true };
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(manager, tray);

        Assert.True(shell.MinimizeToTray(showBalloon: true, appName: "AudioPilot"));

        Assert.Equal(1, manager.HideCalls);
        Assert.Equal(1, tray.BalloonCalls);
        Assert.False(manager.IsVisible);
    }

    [Fact]
    public void MinimizeToTray_DoesNotHideWhenTrayCannotBeCreated()
    {
        var manager = new TestAppMainWindowManager { IsVisibleValue = true };
        var tray = new TestAppTrayIconService { EnsureVisibleResult = false };
        var shell = new AppShellService(manager, tray);

        bool result = shell.MinimizeToTray();

        Assert.False(result);
        Assert.Equal(0, manager.HideCalls);
        Assert.True(manager.IsVisible);
    }
}
