using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Services.UI;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Hardcodet.Wpf.TaskbarNotification;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowTrayMenuTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, true)]
    public void ShouldShowSwitchMenuItem_RequiresDevicesAndEnabledState(int cycleDeviceCount, bool hotkeysEnabled, bool expected)
    {
        bool actual = AppTrayMenuBuilder.ShouldShowSwitchMenuItem(cycleDeviceCount, hotkeysEnabled);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildTrayMenuEntries_WhenWindowIsHidden_UsesSingleShowActionAndStableFooter()
    {
        IReadOnlyList<AppTrayMenuBuilder.TrayMenuEntry> entries = AppTrayMenuBuilder.BuildTrayMenuEntries(
            isWindowVisible: false,
            toggleAppVisibilityHotkey: null,
            hasOutputCycle: false,
            outputDeviceName: null,
            outputSwitchHotkey: null,
            hasInputCycle: false,
            inputDeviceName: null,
            inputSwitchHotkey: null,
            routines: []);

        Assert.Collection(
            entries,
            entry => Assert.Equal(AppTrayMenuBuilder.TrayMenuEntryKind.ShowWindow, entry.Kind),
            entry => Assert.Equal(AppTrayMenuBuilder.TrayMenuEntryKind.Separator, entry.Kind),
            entry => Assert.Equal(AppTrayMenuBuilder.TrayMenuEntryKind.Settings, entry.Kind),
            entry => Assert.Equal(AppTrayMenuBuilder.TrayMenuEntryKind.Exit, entry.Kind));
        Assert.Equal("Show AudioPilot", entries[0].Label);
    }

    [Fact]
    public void BuildTrayMenuEntries_WhenVisibleWithDevicesAndRoutine_PreservesDetailsAndHotkey()
    {
        AudioRoutine routine = new()
        {
            Id = "desk-routine",
            Name = "  Desk  ",
            Hotkey = "  Ctrl+F8  ",
        };

        IReadOnlyList<AppTrayMenuBuilder.TrayMenuEntry> entries = AppTrayMenuBuilder.BuildTrayMenuEntries(
            isWindowVisible: true,
            toggleAppVisibilityHotkey: "  Ctrl+Alt+H  ",
            hasOutputCycle: true,
            outputDeviceName: "  Headphones  ",
            outputSwitchHotkey: "  Ctrl+Alt+Multiply  ",
            hasInputCycle: true,
            inputDeviceName: null,
            inputSwitchHotkey: "  Ctrl+Prior  ",
            routines: [routine]);

        Assert.Equal(AppTrayMenuBuilder.TrayMenuEntryKind.HideWindow, entries[0].Kind);
        Assert.Equal("Hide AudioPilot", entries[0].Label);
        Assert.Equal("Ctrl+Alt+H", entries[0].GestureText);

        AppTrayMenuBuilder.TrayMenuEntry output = Assert.Single(entries, entry => entry.Kind == AppTrayMenuBuilder.TrayMenuEntryKind.SwitchOutput);
        Assert.Equal("Headphones", output.Detail);
        Assert.Equal("Ctrl+Alt+Num *", output.GestureText);

        AppTrayMenuBuilder.TrayMenuEntry input = Assert.Single(entries, entry => entry.Kind == AppTrayMenuBuilder.TrayMenuEntryKind.SwitchInput);
        Assert.Equal("Unavailable", input.Detail);
        Assert.Equal("Ctrl+Page Up", input.GestureText);

        AppTrayMenuBuilder.TrayMenuEntry routineEntry = Assert.Single(entries, entry => entry.Kind == AppTrayMenuBuilder.TrayMenuEntryKind.Routine);
        Assert.Equal("Desk", routineEntry.Label);
        Assert.Equal("Ctrl+F8", routineEntry.GestureText);
        Assert.Equal("desk-routine", routineEntry.RoutineId);
    }

    [Fact]
    public void BuildTrayMenuEntries_IgnoresInvalidRoutineWithoutLeavingDuplicateSeparators()
    {
        IReadOnlyList<AppTrayMenuBuilder.TrayMenuEntry> entries = AppTrayMenuBuilder.BuildTrayMenuEntries(
            isWindowVisible: false,
            toggleAppVisibilityHotkey: " ",
            hasOutputCycle: false,
            outputDeviceName: null,
            outputSwitchHotkey: " Ctrl+Alt+O ",
            hasInputCycle: false,
            inputDeviceName: null,
            inputSwitchHotkey: " Ctrl+Alt+I ",
            routines: [new AudioRoutine { Id = "", Name = "Invalid" }]);

        Assert.DoesNotContain(entries, entry => entry.Kind == AppTrayMenuBuilder.TrayMenuEntryKind.Routine);
        Assert.Equal(1, entries.Count(entry => entry.Kind == AppTrayMenuBuilder.TrayMenuEntryKind.Separator));
    }

    [Theory]
    [InlineData(1040, 96u, 560d)]
    [InlineData(1040, 144u, 560d)]
    [InlineData(600, 144u, 376d)]
    [InlineData(200, 192u, 76d)]
    [InlineData(0, 96u, 560d)]
    [InlineData(1040, 0u, 560d)]
    public void CalculateTrayMenuMaxHeight_UsesMonitorDpiAndSafeBounds(int workAreaHeightPx, uint dpiY, double expected)
    {
        double actual = AppTrayMenuBuilder.CalculateTrayMenuMaxHeight(workAreaHeightPx, dpiY);

        Assert.Equal(expected, actual, precision: 8);
    }

    [Fact]
    public void CreateTrayMenuItem_ProvidesAutomationMetadataAndDeviceHelpText()
    {
        TestExecutionGuards.RunSta(() =>
        {
            AppTrayMenuBuilder.TrayMenuEntry entry = new(
                AppTrayMenuBuilder.TrayMenuEntryKind.SwitchOutput,
                "Switch output",
                "Headphones",
                "Ctrl+F8");

            MenuItem item = AppTrayMenuBuilder.CreateTrayMenuItem(entry);

            Assert.Equal("Switch output", AutomationProperties.GetName(item));
            Assert.Equal("Current device: Headphones", AutomationProperties.GetHelpText(item));
            Assert.Equal("Ctrl+F8", item.InputGestureText);
            System.Windows.Shapes.Path glyph = Assert.IsType<System.Windows.Shapes.Path>(item.Icon);
            Assert.Equal(16d, glyph.Width);
            Assert.Equal(16d, glyph.Height);
            StackPanel header = Assert.IsType<StackPanel>(item.Header);
            Assert.Equal(2, header.Children.Count);
            Assert.Equal(HorizontalAlignment.Left, header.HorizontalAlignment);
        });
    }

    [Fact]
    public void CreateTrayMenuItem_ReusesFrozenGlyphGeometryAcrossMenuRefreshes()
    {
        TestExecutionGuards.RunSta(() =>
        {
            AppTrayMenuBuilder.TrayMenuEntry entry = new(AppTrayMenuBuilder.TrayMenuEntryKind.SwitchOutput, "Switch output");

            MenuItem first = AppTrayMenuBuilder.CreateTrayMenuItem(entry);
            MenuItem second = AppTrayMenuBuilder.CreateTrayMenuItem(entry);

            System.Windows.Shapes.Path firstGlyph = Assert.IsType<System.Windows.Shapes.Path>(first.Icon);
            System.Windows.Shapes.Path secondGlyph = Assert.IsType<System.Windows.Shapes.Path>(second.Icon);
            Assert.Same(firstGlyph.Data, secondGlyph.Data);
            Assert.True(firstGlyph.Data.IsFrozen);
        });
    }

    [Fact]
    public void SchedulePresentationPrewarm_RunsOnceAtDispatcherIdleWithoutCreatingTrayIcon()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            WindowThemeHelper.ApplyApplicationThemeResources(AppTheme.Dark);
            var service = new AppTrayIconService(application, new RecordingAppDialogService());

            service.SchedulePresentationPrewarm();
            service.SchedulePresentationPrewarm();
            Task idle = application.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.SystemIdle).Task;
            TestPrivateAccess.RunTaskOnDispatcher(idle);

            Assert.True(service.IsPresentationPrewarmedForTests);
            Assert.Equal(1, service.PresentationPrewarmAttemptCountForTests);
            Assert.False(service.IsReady);
            service.Dispose();
        });
    }

    [Fact]
    public void ExecuteEntryAsync_Show_UsesWindowManagerWithoutPrecreatingAWindow()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions();
            var manager = new RecordingMainWindowManager();
            var service = CreateTrayService(actions, manager);

            bool result = service.ExecuteEntryAsync(
                new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.ShowWindow, "Show AudioPilot"))
                .GetAwaiter()
                .GetResult();

            Assert.True(result);
            Assert.Equal(1, actions.ShowRequests);
            Assert.Equal([MainWindowOpenTarget.Default], manager.ShowTargets);
            Assert.False(manager.IsCreated);
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void TaskbarDoubleClick_UsesTheSameShowPathAsTheMenuCommand()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(TaskbarDoubleClick_UsesTheSameShowPathAsTheMenuCommand)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions();
            var manager = new RecordingMainWindowManager();
            using var service = CreateTrayService(actions, manager);

            Assert.True(service.EnsureVisible());
            TaskbarIcon taskbarIcon = Assert.IsType<TaskbarIcon>(service.TaskbarIconForTests);

            taskbarIcon.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayMouseDoubleClickEvent));
            Assert.Empty(manager.ShowTargets);

            Task dispatcherDrain = Dispatcher.CurrentDispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle).Task;
            TestPrivateAccess.RunTaskOnDispatcher(dispatcherDrain);

            Assert.Equal([MainWindowOpenTarget.Default], manager.ShowTargets);

            taskbarIcon.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayMouseDoubleClickEvent));
            service.BeginShutdown();
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(
                static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Equal([MainWindowOpenTarget.Default], manager.ShowTargets);
        });
    }

    [Fact]
    public void ExecuteEntryAsync_Settings_SelectsSettingsBeforeRequestingTheTargetWindow()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions();
            var manager = new RecordingMainWindowManager();
            var service = CreateTrayService(actions, manager);

            bool result = service.ExecuteEntryAsync(
                new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.Settings, "Settings"))
                .GetAwaiter()
                .GetResult();

            Assert.True(result);
            Assert.Equal(1, actions.SettingsSelections);
            Assert.Equal(1, actions.ShowRequests);
            Assert.Equal([MainWindowOpenTarget.Settings], manager.ShowTargets);
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MenuClick_WaitsForNativeCloseAndHonorsShutdown(bool settings, bool shutdown)
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(MenuClick_WaitsForNativeCloseAndHonorsShutdown)))
        {
            Assert.Skip(TestExecutionGuards.GetVisualWpfSkipReason());
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions();
            var manager = new RecordingMainWindowManager();
            using var service = CreateTrayService(actions, manager);
            Assert.True(service.EnsureVisible());
            TaskbarIcon icon = Assert.IsType<TaskbarIcon>(service.TaskbarIconForTests);
            ContextMenu menu = icon.ContextMenu;
            bool menuClosed = false;
            long closeStarted = 0;
            bool? menuClosedWhenShowRequested = null;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            menu.Closed += (_, _) =>
            {
                output.WriteLine($"Tray close completed in {Stopwatch.GetElapsedTime(closeStarted).TotalMilliseconds:F1} ms");
                menuClosed = true;
                closed.TrySetResult();
            };
            actions.ShowAsync = (target, token) =>
            {
                menuClosedWhenShowRequested = menuClosed;
                shown.TrySetResult();
                return manager.ShowAsync(target, token);
            };

            try
            {
                menu.IsOpen = true;
                icon.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayContextMenuOpenEvent));
                TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(
                    static () => { }, DispatcherPriority.ContextIdle).Task);
                Popup popup = Assert.IsType<Popup>(LogicalTreeHelper.GetParent(menu));
                Assert.Equal(PopupAnimation.None, popup.PopupAnimation);
                MenuItem item = menu.Items.OfType<MenuItem>().ElementAt(settings ? 1 : 0);
                // WPF starts closing the popup before dispatching Click; native teardown can still be pending.
                closeStarted = Stopwatch.GetTimestamp();
                menu.IsOpen = false;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                if (shutdown)
                {
                    service.BeginShutdown();
                    TestPrivateAccess.RunTaskOnDispatcher(closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                    TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(
                        static () => { }, DispatcherPriority.ContextIdle).Task);
                    Assert.Empty(manager.ShowTargets);
                    Assert.Equal(0, actions.ShowRequests);
                    Assert.Equal(0, actions.SettingsSelections);
                    return;
                }

                TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(closed.Task, shown.Task).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(SystemParameters.MenuPopupAnimation, popup.PopupAnimation);
                Assert.True(menuClosed);
                Assert.True(menuClosedWhenShowRequested);
                Assert.Equal([settings ? MainWindowOpenTarget.Settings : MainWindowOpenTarget.Default], manager.ShowTargets);
                Assert.Equal(settings ? 1 : 0, actions.SettingsSelections);
            }
            finally
            {
                menu.IsOpen = false;
            }
        });
    }

    [Fact]
    public void ExecuteEntryAsync_Hide_DoesNotCreateAWindow()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions();
            var manager = new RecordingMainWindowManager();
            var service = CreateTrayService(actions, manager);

            bool result = service.ExecuteEntryAsync(
                new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.HideWindow, "Hide AudioPilot"))
                .GetAwaiter()
                .GetResult();

            Assert.True(result);
            Assert.Equal(1, actions.HideRequests);
            Assert.Empty(manager.ShowTargets);
            Assert.False(manager.IsCreated);
        });
    }

    [Fact]
    public void ExecuteEntryAsync_RuntimeCommandsRemainWindowless()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions();
            var manager = new RecordingMainWindowManager();
            int shutdownRequests = 0;
            var service = CreateTrayService(actions, manager, () =>
            {
                shutdownRequests++;
                return Task.CompletedTask;
            });

            service.ExecuteEntryAsync(new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.SwitchOutput, "Switch output")).GetAwaiter().GetResult();
            service.ExecuteEntryAsync(new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.SwitchInput, "Switch input")).GetAwaiter().GetResult();
            service.ExecuteEntryAsync(new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.Routine, "Desk", RoutineId: "routine-1")).GetAwaiter().GetResult();
            service.ExecuteEntryAsync(new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.Exit, "Exit")).GetAwaiter().GetResult();

            Assert.Equal(1, actions.OutputSwitches);
            Assert.Equal(1, actions.InputSwitches);
            Assert.Equal(["routine-1"], actions.RoutineIds);
            Assert.Equal(1, shutdownRequests);
            Assert.Empty(manager.ShowTargets);
            Assert.False(manager.IsCreated);
        });
    }

    [Fact]
    public void ExecuteEntryAsync_AfterShutdownBarrierRejectsTrayActions()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions();
            var manager = new RecordingMainWindowManager();
            var service = CreateTrayService(actions, manager);

            service.BeginShutdown();
            bool showResult = service.ExecuteEntryAsync(
                new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.ShowWindow, "Show AudioPilot"))
                .GetAwaiter()
                .GetResult();
            bool switchResult = service.ExecuteEntryAsync(
                new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.SwitchOutput, "Switch output"))
                .GetAwaiter()
                .GetResult();

            Assert.False(showResult);
            Assert.False(switchResult);
            Assert.Equal(0, actions.ShowRequests);
            Assert.Equal(0, actions.OutputSwitches);
            Assert.Empty(manager.ShowTargets);
        });
    }

    [Fact]
    public void SelectRoutineListItem_SelectsBoundItem()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            AudioRoutine routine = new() { Id = "routine-1", Name = "Desk" };
            ListBoxItem item = new()
            {
                DataContext = routine,
            };

            bool selected = MainWindow.SelectRoutineListItem(item);

            Assert.True(selected);
            Assert.True(item.IsSelected);
        });
    }

    [Fact]
    public void SelectRoutineListItem_ReturnsFalse_WhenItemHasNoRoutine()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            bool selected = MainWindow.SelectRoutineListItem(new ListBoxItem());

            Assert.False(selected);
        });
    }

    [Fact]
    public void TryGetRoutineListItemFromEventSource_ResolvesDirectListBoxItem()
    {
        TestExecutionGuards.RunSta(() =>
        {
            AudioRoutine routine = new() { Id = "routine-1", Name = "Desk" };
            ListBoxItem item = new()
            {
                DataContext = routine,
            };

            bool resolved = MainWindow.TryGetRoutineListItemFromEventSource(item, out ListBoxItem? resolvedItem);

            Assert.True(resolved);
            Assert.Same(item, resolvedItem);
        });
    }

    private static AppTrayIconService CreateTrayService(
        RecordingTrayRuntimeActions actions,
        RecordingMainWindowManager manager,
        Func<Task>? shutdown = null)
    {
        Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var service = new AppTrayIconService(application, new RecordingAppDialogService());
        actions.ShowAsync = manager.ShowAsync;
        service.AttachRuntime(actions, manager, shutdown ?? (() => Task.CompletedTask));
        return service;
    }

    private sealed class RecordingTrayRuntimeActions : IAppTrayRuntimeActions
    {
        public int OutputCycleDeviceCount => 0;
        public int InputCycleDeviceCount => 0;
        public bool OutputHotkeysEnabled => false;
        public bool InputHotkeysEnabled => false;
        public string? ToggleVisibilityHotkey => null;
        public (string? Output, string? Input) SwitchHotkeys => (null, null);
        public IReadOnlyList<AudioRoutine> Routines => [];
        public int ShowRequests { get; private set; }
        public int HideRequests { get; private set; }
        public int SettingsSelections { get; private set; }
        public int OutputSwitches { get; private set; }
        public int InputSwitches { get; private set; }
        public List<string> RoutineIds { get; } = [];
        public Func<MainWindowOpenTarget, CancellationToken, Task<bool>>? ShowAsync { get; set; }
        public Task<bool> RequestShowAsync(MainWindowOpenTarget target = MainWindowOpenTarget.Default)
        {
            ShowRequests++;
            return ShowAsync?.Invoke(target, CancellationToken.None) ?? Task.FromResult(true);
        }
        public void RequestHide() => HideRequests++;
        public void SelectSettings() => SettingsSelections++;
        public Task SwitchOutputAsync() { OutputSwitches++; return Task.CompletedTask; }
        public Task SwitchInputAsync() { InputSwitches++; return Task.CompletedTask; }
        public Task RunRoutineAsync(string routineId) { RoutineIds.Add(routineId); return Task.CompletedTask; }
    }

    private sealed class RecordingMainWindowManager : IAppMainWindowManager
    {
        public bool IsCreated { get; private set; }
        public bool IsVisible => false;
        public MainWindow? CurrentWindow => null;
        public List<MainWindowOpenTarget> ShowTargets { get; } = [];
        public Task<bool> ShowAsync(MainWindowOpenTarget target = MainWindowOpenTarget.Default, CancellationToken cancellationToken = default) { ShowTargets.Add(target); return Task.FromResult(true); }
        public bool Hide() => true;
        public Task<bool> HideAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void BeginShutdown() { }
        public Task CloseForShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
