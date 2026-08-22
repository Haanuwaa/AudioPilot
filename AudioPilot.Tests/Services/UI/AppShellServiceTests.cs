using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppShellServiceTests
{
    [Fact]
    public void ScheduledReminders_UseOneInformationalBalloonAndOpenRoutines()
    {
        var manager = new TestAppMainWindowManager();
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(manager, tray);
        shell.NotifyUpcomingScheduledRoutines([]);
        Assert.Equal(0, tray.BalloonCalls);
        shell.NotifyUpcomingScheduledRoutines(["Bedtime", "Quiet hours"]);
        Assert.Equal(1, tray.BalloonCalls);
        Assert.False(tray.LastBalloonWarning);
        Assert.Equal(MainWindowOpenTarget.Routines, tray.LastBalloonTarget);
        Assert.Empty(manager.ShowTargets);
    }

    [Fact]
    public void ScheduledReminders_BoundLongImportedNamesWithoutSplittingEmoji()
    {
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(new TestAppMainWindowManager(), tray);
        shell.NotifyUpcomingScheduledRoutines([
            string.Concat(Enumerable.Repeat("👩‍💻", 40)), new string('x', 1000), "Line\r\nbreak", "Fourth"]);
        Assert.StartsWith(string.Concat(Enumerable.Repeat("👩‍💻", 8)) + "…", tray.LastBalloonMessage, StringComparison.Ordinal);
        Assert.True(tray.LastBalloonMessage.Length <= 255);
        Assert.DoesNotContain('\n', tray.LastBalloonMessage);
        Assert.DoesNotContain('\r', tray.LastBalloonMessage);
        Assert.Contains("and 1 more", tray.LastBalloonMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundFailure_RateLimitsRetriesAndKeepsOtherFailureActionable()
    {
        var clock = new NotificationClock();
        var manager = new TestAppMainWindowManager();
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(manager, tray, clock);

        shell.NotifyBackgroundFailure(BackgroundFailureKind.AutoSave);
        shell.NotifyBackgroundFailure(BackgroundFailureKind.AutoSave);
        clock.Advance(TimeSpan.FromSeconds(59));
        shell.NotifyBackgroundFailure(BackgroundFailureKind.AutoSave);
        Assert.Equal(1, tray.BalloonCalls);
        Assert.True(tray.LastBalloonWarning);
        Assert.Equal(MainWindowOpenTarget.Settings, tray.LastBalloonTarget);

        shell.NotifyBackgroundFailure(BackgroundFailureKind.ResumeRecovery);
        Assert.Equal(2, tray.BalloonCalls);
        Assert.Equal(MainWindowOpenTarget.Default, tray.LastBalloonTarget);
        clock.Advance(TimeSpan.FromSeconds(1));
        shell.NotifyBackgroundFailure(BackgroundFailureKind.AutoSave);
        Assert.Equal(3, tray.BalloonCalls);
        Assert.Empty(manager.ShowTargets);
        Assert.False(manager.IsCreated);
    }

    private sealed class NotificationClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

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
