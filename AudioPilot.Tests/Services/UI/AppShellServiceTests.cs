using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppShellServiceTests
{
    [Fact]
    public async Task ExpiredQueuedDeliveryCannotClearSuppressionForANewerFailure()
    {
        var oldGate = new TaskCompletionSource<bool>();
        var tray = new TestAppTrayIconService { BalloonGate = oldGate };
        using var shell = new AppShellService(new TestAppMainWindowManager(), tray);
        Task oldDelivery = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        shell.ClearBackgroundFailure(BackgroundFailureKind.AutoSave);
        var newGate = new TaskCompletionSource<bool>();
        tray.BalloonGate = newGate;
        Task newDelivery = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        oldGate.SetResult(true);
        await oldDelivery;
        newGate.SetResult(true);
        await newDelivery;
        await shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(1, tray.BalloonCalls);
    }

    [Fact]
    public async Task FailedNotificationDeliveryCanRetryWithoutRequiringAudioRecovery()
    {
        var tray = new TestAppTrayIconService { AcceptBalloons = false };
        using var shell = new AppShellService(new TestAppMainWindowManager(), tray);
        await shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(0, tray.BalloonCalls);
        tray.AcceptBalloons = true;
        await shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(1, tray.BalloonCalls);
    }

    [Theory]
    [InlineData("recovered")]
    [InlineData("visible")]
    [InlineData("disposed")]
    public async Task QueuedNotificationsRecheckRecoveryVisibilityAndShutdownBeforeDelivery(string transition)
    {
        var manager = new TestAppMainWindowManager();
        var gate = new TaskCompletionSource<bool>();
        var tray = new TestAppTrayIconService { BalloonGate = gate };
        using var shell = new AppShellService(manager, tray);
        Task pending = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        if (transition == "recovered") shell.ClearBackgroundFailure(BackgroundFailureKind.AutoSave);
        else if (transition == "visible") manager.IsVisibleValue = true;
        else shell.Dispose();
        gate.SetResult(true);
        await pending;
        Assert.Equal(0, tray.BalloonCalls);
        if (transition != "disposed")
        {
            manager.IsVisibleValue = false;
            await shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
            Assert.Equal(1, tray.BalloonCalls);
        }
    }

    [Fact]
    public void ScheduledReminders_UseOneInformationalBalloonAndOfferToOpenAudioPilot()
    {
        var manager = new TestAppMainWindowManager();
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(manager, tray);
        shell.NotifyUpcomingScheduledRoutines([]);
        Assert.Equal(0, tray.BalloonCalls);
        shell.NotifyUpcomingScheduledRoutines(["Bedtime", "Quiet hours"]);
        Assert.Equal(1, tray.BalloonCalls);
        Assert.False(tray.LastBalloonWarning);
        Assert.EndsWith("Click to open AudioPilot.", tray.LastBalloonMessage, StringComparison.Ordinal);
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
    public void BackgroundFailure_SuppressesUntilRecoveryAndKeepsOtherFailuresActionable()
    {
        var manager = new TestAppMainWindowManager();
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(manager, tray);

        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(1, tray.BalloonCalls);
        Assert.True(tray.LastBalloonWarning);
        Assert.Contains("review Settings", tray.LastBalloonMessage, StringComparison.Ordinal);

        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.ResumeRecovery);
        Assert.Equal(2, tray.BalloonCalls);
        Assert.Contains("Click to open AudioPilot", tray.LastBalloonMessage, StringComparison.Ordinal);
        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(2, tray.BalloonCalls);
        shell.ClearBackgroundFailure(BackgroundFailureKind.AutoSave);
        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(3, tray.BalloonCalls);
        Assert.Empty(manager.ShowTargets);
        Assert.False(manager.IsCreated);
    }

    [Fact]
    public void BackgroundFailure_StaysQuietWhileVisibleAndAfterDisposal()
    {
        var manager = new TestAppMainWindowManager { IsVisibleValue = true };
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(manager, tray);
        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(0, tray.BalloonCalls);
        manager.IsVisibleValue = false;
        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.AutoSave);
        Assert.Equal(1, tray.BalloonCalls);
        shell.Dispose();
        _ = shell.NotifyBackgroundFailureAsync(BackgroundFailureKind.ResumeRecovery);
        Assert.Equal(1, tray.BalloonCalls);
    }

    [Fact]
    public void RoutineFailures_GroupUntilAllRecoverAndIgnoreOptOutManualAndSkippedRuns()
    {
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(new TestAppMainWindowManager(), tray);
        shell.ReportRoutineResult("one", false, false, false, true);
        shell.ReportRoutineResult("one", true, false, true, true);
        shell.ReportRoutineResult("one", true, false, false, false);
        Assert.Equal(0, tray.BalloonCalls);
        shell.ReportRoutineResult("one", true, false, false, true);
        shell.ReportRoutineResult("two", true, false, false, true);
        Assert.Equal(1, tray.BalloonCalls);
        Assert.Contains("check Routines", tray.LastBalloonMessage, StringComparison.Ordinal);
        shell.ReportRoutineResult("one", true, true, false, false);
        shell.ReportRoutineResult("two", true, false, false, true);
        Assert.Equal(1, tray.BalloonCalls);
        shell.RetainRoutineFailures([]);
        shell.ReportRoutineResult("three", true, false, false, true);
        Assert.Equal(2, tray.BalloonCalls);
    }

    [Fact]
    public void HotkeyFailures_GroupIgnoreWorkingFallbackAndResetOnlyAfterRecovery()
    {
        var tray = new TestAppTrayIconService();
        var shell = new AppShellService(new TestAppMainWindowManager(), tray);
        AudioPilot.Logging.HotkeyRuntimeSnapshot snapshot = new(true, false, false, 2, true, true, true,
            [new(1, "Registered", null, 0), new(2, "Fallback", null, 0)]);
        shell.ReportHotkeyAvailability(snapshot);
        Assert.Equal(0, tray.BalloonCalls);
        var failed = snapshot with { Registrations = [new(1, "ExternalConflict", null, 1409), new(2, "KeyboardHookUnavailable", null, 0)] };
        shell.ReportHotkeyAvailability(failed with { CaptureSuspended = true });
        Assert.Equal(0, tray.BalloonCalls);
        shell.ReportHotkeyAvailability(failed);
        shell.ReportHotkeyAvailability(failed);
        Assert.Equal(1, tray.BalloonCalls);
        Assert.Contains("review your hotkey settings", tray.LastBalloonMessage, StringComparison.Ordinal);
        shell.ReportHotkeyAvailability(snapshot);
        shell.ReportHotkeyAvailability(failed);
        Assert.Equal(2, tray.BalloonCalls);
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
