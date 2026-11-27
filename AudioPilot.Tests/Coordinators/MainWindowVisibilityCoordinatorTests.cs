using System.Windows;
using AudioPilot.Coordinators;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class MainWindowVisibilityCoordinatorTests
{
    [Fact]
    public void HandleWindowStateChanged_MinimizesViewModel_WhenVisibleWindowIsMinimized()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visibility.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int minimizeCalls = 0;

        coordinator.HandleWindowStateChanged(
            WindowState.Minimized,
            isWindowVisible: true,
            () => minimizeCalls++);

        Assert.Equal(1, minimizeCalls);
        coordinator.HandleVisibleChanged(true, static () => true, static () => true,
            () => Assert.Fail("A minimize that did not hide the window must not arm auto-scroll."));
    }

    [Fact]
    public void HandleWindowStateChanged_DoesNotReenterMinimize_WhenWindowWasAlreadyHidden()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visibility-hidden.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int minimizeCalls = 0;

        coordinator.HandleWindowStateChanged(WindowState.Minimized, isWindowVisible: false, () => minimizeCalls++);

        Assert.Equal(0, minimizeCalls);
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public void HandleVisibleChanged_ConsumesThePendingScrollAndRespectsTabAndOption(bool deviceTab, bool enabled, int expected)
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visible-scroll.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int scheduledScrolls = 0;

        coordinator.MarkPendingAutoScrollOnNextShow();
        coordinator.HandleVisibleChanged(
            isVisible: true,
            isEditorTabActive: () => deviceTab,
            isAutoScrollEnabled: () => enabled,
            scheduleScroll: () => scheduledScrolls++);
        coordinator.HandleVisibleChanged(true, static () => true, static () => true, () => scheduledScrolls++);
        Assert.Equal(expected, scheduledScrolls);
    }

    [Fact]
    public void HandleVisibleChanged_AutoScrollsOnEveryMinimizeRestoreCycle()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visible-once.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int scheduledScrolls = 0;

        coordinator.HandleWindowStateChanged(WindowState.Minimized, isWindowVisible: true, coordinator.MarkPendingAutoScrollOnNextShow);
        coordinator.HandleVisibleChanged(
            isVisible: true,
            isEditorTabActive: static () => true,
            isAutoScrollEnabled: static () => true,
            scheduleScroll: () => scheduledScrolls++);

        coordinator.HandleWindowStateChanged(WindowState.Minimized, isWindowVisible: true, coordinator.MarkPendingAutoScrollOnNextShow);
        coordinator.HandleVisibleChanged(
            isVisible: true,
            isEditorTabActive: static () => true,
            isAutoScrollEnabled: static () => true,
            scheduleScroll: () => scheduledScrolls++);

        Assert.Equal(2, scheduledScrolls);
    }

    [Fact]
    public void HandleVisibleChanged_SchedulesScroll_OnFirstShowAfterStartupHiddenToTray()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visible-startup-hidden.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int scheduledScrolls = 0;

        coordinator.MarkPendingAutoScrollOnNextShow();
        coordinator.HandleVisibleChanged(
            isVisible: true,
            isEditorTabActive: static () => true,
            isAutoScrollEnabled: static () => true,
            scheduleScroll: () => scheduledScrolls++);

        Assert.Equal(1, scheduledScrolls);
    }
}
