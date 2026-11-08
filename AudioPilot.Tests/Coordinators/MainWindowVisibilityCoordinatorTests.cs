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

    [Fact]
    public void HandleVisibleChanged_SchedulesScroll_WhenReturningFromTray_WithConfiguredDevices_AndAutoScrollEnabled()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visible-scroll.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int scheduledScrolls = 0;

        coordinator.HandleWindowStateChanged(WindowState.Minimized, isWindowVisible: true, static () => { });
        coordinator.HandleVisibleChanged(
            isVisible: true,
            isEditorTabActive: static () => true,
            isAutoScrollEnabled: static () => true,
            scheduleScroll: () => scheduledScrolls++);

        Assert.Equal(1, scheduledScrolls);
    }

    [Fact]
    public void HandleVisibleChanged_DoesNotScheduleScroll_WhenActiveTabIsNotOutputOrInput()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visible-no-devices.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int scheduledScrolls = 0;

        coordinator.HandleWindowStateChanged(WindowState.Minimized, isWindowVisible: true, static () => { });
        coordinator.HandleVisibleChanged(
            isVisible: true,
            isEditorTabActive: static () => false,
            isAutoScrollEnabled: static () => true,
            scheduleScroll: () => scheduledScrolls++);

        Assert.Equal(0, scheduledScrolls);
    }

    [Fact]
    public void HandleVisibleChanged_AutoScrollsOnEveryMinimizeRestoreCycle()
    {
        using var loggerScope = new TestLoggerScope(nameof(MainWindowVisibilityCoordinatorTests), "main-window-visible-once.log");
        MainWindowVisibilityCoordinator coordinator = new(loggerScope.Logger);
        int scheduledScrolls = 0;

        coordinator.HandleWindowStateChanged(WindowState.Minimized, isWindowVisible: true, static () => { });
        coordinator.HandleVisibleChanged(
            isVisible: true,
            isEditorTabActive: static () => true,
            isAutoScrollEnabled: static () => true,
            scheduleScroll: () => scheduledScrolls++);

        coordinator.HandleWindowStateChanged(WindowState.Minimized, isWindowVisible: true, static () => { });
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
