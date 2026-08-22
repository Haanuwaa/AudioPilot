using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace AudioPilot.Tests;

public sealed partial class MainWindowTrayMenuTests
{
    [VisualIntegrationFact]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    public void MenuPopulationFailure_PreservesWorkingShowAndExitActions()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var actions = new RecordingTrayRuntimeActions { ThrowOnReadRoutines = true };
            var manager = new RecordingMainWindowManager();
            bool exited = false;
            using var service = CreateTrayService(actions, manager, () => { exited = true; return Task.CompletedTask; });
            Assert.True(service.EnsureVisible());
            TaskbarIcon icon = Assert.IsType<TaskbarIcon>(service.TaskbarIconForTests);
            icon.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayContextMenuOpenEvent));
            MenuItem[] items = [.. icon.ContextMenu.Items.OfType<MenuItem>()];

            Assert.Equal(3, items.Length);
            Assert.False(items[0].IsEnabled);
            Assert.Equal("Show AudioPilot", AutomationProperties.GetName(items[1]));
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            TestPrivateAccess.RunTaskOnDispatcher(Dispatcher.CurrentDispatcher.InvokeAsync(
                static () => { }, DispatcherPriority.ContextIdle).Task);
            Assert.Single(manager.ShowTargets);

            Assert.Equal("Exit", AutomationProperties.GetName(items[2]));
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(exited);
        });
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendTrayMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterTrayMessage(string message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessInformation(nint process, int informationClass, ref ProcessPowerState state, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerState
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void NativeTray_RegistrationFailureIsRecoverableAndDoesNotEnableEfficiencyMode()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using Process process = Process.GetCurrentProcess();
            ProcessPriorityClass priority = process.PriorityClass;
            ProcessPowerState before = new() { Version = 1 };
            Assert.True(GetProcessInformation(process.Handle, 4, ref before, (uint)Marshal.SizeOf<ProcessPowerState>()));
            using var service = CreateTrayService(new RecordingTrayRuntimeActions(), new RecordingMainWindowManager());
            Assert.False(service.IsReady);
            Assert.True(service.EnsureVisible());
            TaskbarIcon icon = Assert.IsType<TaskbarIcon>(service.TaskbarIconForTests);
            nint window = icon.TrayIcon.WindowHandle;
            Assert.True(IsWindow(window));
            Assert.NotNull(icon.Icon);
            Assert.NotEqual(0, icon.TrayIcon.Icon);
            Assert.True(service.IsReady);
            Assert.True(Task.Run(() => service.IsReady).GetAwaiter().GetResult());

            service.Hide();
            Assert.False(service.IsReady);
            Assert.True(service.EnsureVisible());
            Assert.Same(icon, service.TaskbarIconForTests);

            Assert.True(icon.TrayIcon.TryRemove());
            Assert.False(service.IsReady);
            icon.TrayIcon.WindowHandle = -1;
            Assert.False(service.EnsureVisible());
            Assert.False(service.IsReady);
            Assert.True(icon.IsDisposed);
            Assert.False(IsWindow(window));
            Assert.Null(service.TaskbarIconForTests);
            Assert.True(service.EnsureVisible());

            ProcessPowerState after = new() { Version = 1 };
            Assert.True(GetProcessInformation(process.Handle, 4, ref after, (uint)Marshal.SizeOf<ProcessPowerState>()));
            Assert.Equal(before.ControlMask, after.ControlMask);
            Assert.Equal(before.StateMask, after.StateMask);
            Assert.Equal(priority, process.PriorityClass);

            service.Dispose();
            Assert.False(service.IsReady);
            Assert.False(service.EnsureVisible());
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void NativeTray_TaskbarRecreationAndDpiMessagesPreserveRegistrationAndShutdown()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var service = CreateTrayService(new RecordingTrayRuntimeActions(), new RecordingMainWindowManager());
            Assert.True(service.EnsureVisible());
            TaskbarIcon icon = Assert.IsType<TaskbarIcon>(service.TaskbarIconForTests);
            nint window = icon.TrayIcon.WindowHandle;
            Guid id = icon.Id;
            uint taskbarCreated = RegisterTrayMessage("TaskbarCreated");
            Assert.NotEqual(0u, taskbarCreated);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                _ = SendTrayMessage(window, taskbarCreated, 0, 0);
                _ = SendTrayMessage(window, 0x02E0, 144u | (144u << 16), 0);
                Assert.True(service.IsReady);
                Assert.Equal(id, icon.Id);
                Assert.NotEqual(0, icon.TrayIcon.Icon);
            }

            service.BeginShutdown();
            _ = SendTrayMessage(window, taskbarCreated, 0, 0);
            Assert.False(service.IsReady);
            Assert.Equal(IconVisibility.Hidden, icon.TrayIcon.Visibility);
            TestPrivateAccess.RunTaskOnDispatcher(Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(5)));
            service.Dispose();
            Assert.False(IsWindow(window));
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationTheory]
    [InlineData(0x007B, 120, 160)]
    [InlineData(0x007B, -120, -160)]
    [InlineData(0x0205, 120, 160)]
    [InlineData(0x0205, -120, -160)]
    public void NativeTray_ContextMenuUsesSignedAnchorAndSupportsKeyboard(int callback, short x, short y)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            WindowThemeHelper.ApplyApplicationThemeResources(AppTheme.Dark);
            using var service = CreateTrayService(new RecordingTrayRuntimeActions(), new RecordingMainWindowManager());
            Assert.True(service.EnsureVisible());
            TaskbarIcon icon = Assert.IsType<TaskbarIcon>(service.TaskbarIconForTests);
            ContextMenu menu = icon.ContextMenu;
            DpiScale dpi = VisualTreeHelper.GetDpi(menu);
            try
            {
                SendTrayCallback(icon, callback, x, y);
                DrainTrayDispatcher();
                Assert.True(menu.IsOpen);
                Assert.Equal(Math.Round(x / dpi.DpiScaleX), menu.HorizontalOffset);
                Assert.Equal(Math.Round(y / dpi.DpiScaleY), menu.VerticalOffset);
                MenuItem first = menu.Items.OfType<MenuItem>().First();
                Assert.True(first.IsKeyboardFocused);
                Assert.True(first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.NotSame(first, Keyboard.FocusedElement);
                HwndSource source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(menu));
                menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                });
                menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });
                DrainTrayDispatcher();
                Assert.False(menu.IsOpen);
            }
            finally
            {
                menu.IsOpen = false;
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationTheory]
    [InlineData(0x0401)]
    [InlineData(0x0203)]
    [InlineData(0x0405)]
    public void NativeTray_ActivationAndNotificationCallbacksUseWindowShowPath(int callback)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var manager = new RecordingMainWindowManager();
            using var service = CreateTrayService(new RecordingTrayRuntimeActions(), manager);
            Assert.True(service.EnsureVisible());
            TaskbarIcon icon = Assert.IsType<TaskbarIcon>(service.TaskbarIconForTests);
            TestPrivateAccess.SetField(service, "_balloonTarget", MainWindowOpenTarget.Routines);

            SendTrayCallback(icon, callback);
            DrainTrayDispatcher();
            Assert.Equal([callback == 0x0405 ? MainWindowOpenTarget.Routines : MainWindowOpenTarget.Default], manager.ShowTargets);

            service.BeginShutdown();
            SendTrayCallback(icon, callback);
            DrainTrayDispatcher();
            Assert.Single(manager.ShowTargets);
        });
    }

    private static void SendTrayCallback(TaskbarIcon icon, int callback, short x = 0, short y = 0)
    {
        uint coordinates = unchecked((ushort)x | ((uint)(ushort)y << 16));
        _ = SendTrayMessage(icon.TrayIcon.WindowHandle, MessageWindow.CallbackMessageId, coordinates, callback);
    }

    private static void DrainTrayDispatcher() => TestPrivateAccess.RunTaskOnDispatcher(
        Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task);
}
