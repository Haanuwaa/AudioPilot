using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

[Collection("WpfApplicationIsolation")]
public sealed partial class WindowFirstPresentationHelperTests
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint windowHandle);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint windowHandle, int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint windowHandle, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint windowHandle, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint windowHandle);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [VisualIntegrationFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    public void FirstPresentation_CentersOnPrimaryAfterStagingAtEachConnectedMonitorDpi()
    {
        TestExecutionGuards.RunSta(() =>
        {
            nint previousContext = SetThreadDpiAwarenessContext(new nint(-4));
            SynchronizationContext? previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                Assert.NotEqual(nint.Zero, previousContext);
                var monitors = new List<nint>();
                bool Callback(nint monitor, nint hdc, nint rect, nint data) { monitors.Add(monitor); return true; }
                Assert.True(EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero));
                Assert.NotEmpty(monitors);
                nint primary = MonitorFromWindow(nint.Zero, 1);
                var primaryInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                Assert.True(GetMonitorInfo(primary, ref primaryInfo));
                foreach (nint monitor in monitors)
                {
                    var window = new Window
                    {
                        Width = 462,
                        Height = 378,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        ShowInTaskbar = false,
                        Content = new Border(),
                    };
                    try
                    {
                        WindowFirstPresentationHelper.Prepare(window);
                        WindowFirstPresentationHelper.StageOffscreenFirstRender(window);
                        nint handle = new WindowInteropHelper(window).EnsureHandle();
                        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                        Assert.True(GetMonitorInfo(monitor, ref info));
                        Assert.True(SetWindowPos(handle, nint.Zero, info.Work.Left + 16, info.Work.Top + 16, 0, 0, 0x0015));
                        uint stagingDpi = GetDpiForWindow(handle);
                        WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);
                        Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(window, activate: false, waitForFirstRender: true);
                        window.Show();
                        TestPrivateAccess.RunTaskOnDispatcher(reveal);
                        Assert.True(reveal.Result);
                        Assert.Equal(primary, MonitorFromWindow(handle, 2));
                        Assert.True(GetWindowRect(handle, out NativeRect bounds));
                        Console.WriteLine($"stagingDpi={stagingDpi} finalDpi={GetDpiForWindow(handle)} windowPx={bounds.Left},{bounds.Top},{bounds.Right - bounds.Left},{bounds.Bottom - bounds.Top}");
                        Assert.InRange(Math.Abs(bounds.Left + bounds.Right - primaryInfo.Work.Left - primaryInfo.Work.Right), 0, 1);
                        Assert.InRange(Math.Abs(bounds.Top + bounds.Bottom - primaryInfo.Work.Top - primaryInfo.Work.Bottom), 0, 1);

                        Assert.True(SetWindowPos(handle, nint.Zero, primaryInfo.Work.Left + 40, primaryInfo.Work.Top + 60, 0, 0, 0x0015));
                        window.Hide();
                        window.Show();
                        Task<bool> secondReveal = WindowFirstPresentationHelper.RevealAsync(window, activate: false);
                        TestPrivateAccess.RunTaskOnDispatcher(secondReveal);
                        Assert.True(secondReveal.Result);
                        Assert.True(GetWindowRect(handle, out bounds));
                        Assert.Equal(primaryInfo.Work.Left + 40, bounds.Left);
                        Assert.Equal(primaryInfo.Work.Top + 60, bounds.Top);
                    }
                    finally { window.Close(); }
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
                _ = SetThreadDpiAwarenessContext(previousContext);
            }
        });
    }

    [VisualIntegrationFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    public void Activate_RecoversHiddenWindowOutsideConnectedDisplays()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new Window
            {
                Width = 450,
                Height = 370,
                Left = SystemParameters.VirtualScreenLeft - 4000,
                Top = SystemParameters.VirtualScreenTop - 4000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                window.Hide();
                window.Show();
                nint handle = new WindowInteropHelper(window).Handle;
                Assert.Equal(nint.Zero, MonitorFromWindow(handle, 0));
                WindowFirstPresentationHelper.Activate(window);
                Assert.NotEqual(nint.Zero, MonitorFromWindow(handle, 0));
                Assert.False(IsIconic(handle));
                Assert.False(window.Topmost);
            }
            finally { window.Close(); }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Activate_RestoresNativeWindowAfterHide(bool minimize, bool hide)
    {
        if (!TestExecutionGuards.ShouldRunVisualWpfIntegration())
        {
            Assert.Skip(TestExecutionGuards.GetVisualWpfSkipReason());
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new Window
            {
                Title = "AudioPilot activation test",
                Width = 280,
                Height = 180,
                ShowActivated = false,
                ShowInTaskbar = true,
            };
            try
            {
                if (minimize && hide)
                {
                    window.StateChanged += (_, _) =>
                    {
                        if (window.WindowState == WindowState.Minimized && window.IsVisible)
                        {
                            window.Opacity = 0;
                            window.ShowInTaskbar = false;
                            window.Hide();
                            window.WindowState = WindowState.Normal;
                        }
                    };
                }
                window.Show();
                if (minimize)
                {
                    window.WindowState = WindowState.Minimized;
                }
                if (hide && !minimize)
                {
                    window.Opacity = 0;
                    window.ShowInTaskbar = false;
                    window.Hide();
                    window.WindowState = WindowState.Normal;
                }

                window.WindowState = WindowState.Normal;
                window.ShowInTaskbar = true;
                window.Opacity = 1;
                window.Show();
                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(window, activate: true);
                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                nint handle = new WindowInteropHelper(window).Handle;
                Assert.True(reveal.Result);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.False(IsIconic(handle));
                Assert.False(window.Topmost);
                Assert.Equal(0, GetWindowLong(handle, -20) & 0x0008);

                nint foreground = GetForegroundWindow();
                if (foreground != nint.Zero && foreground != handle)
                {
                    nint preceding = GetWindow(handle, 3);
                    while (preceding != nint.Zero && preceding != foreground)
                    {
                        preceding = GetWindow(preceding, 3);
                    }
                    Assert.Equal(foreground, preceding);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Prepare_HidesUnmaterializedWindowAndTaskbarEntry()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var window = new Window
            {
                Opacity = 1d,
                ShowActivated = true,
                ShowInTaskbar = true,
            };

            WindowFirstPresentationHelper.Prepare(window);

            Assert.Equal(0d, window.Opacity);
            Assert.False(window.ShowActivated);
            Assert.False(window.ShowInTaskbar);
        });
    }

    [Fact]
    public void Prepare_StandaloneDialogPreservesItsTaskbarPolicy()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var window = new Window { ShowInTaskbar = true };

            WindowFirstPresentationHelper.Prepare(window, hideFromTaskbar: false);

            Assert.True(window.ShowInTaskbar);
            Assert.Equal(0d, window.Opacity);
            Assert.False(window.ShowActivated);
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void NativeClientBackground_MatchesTheThemedWindowBeforeFirstShow()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(NativeClientBackground_MatchesTheThemedWindowBeforeFirstShow)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Color expected = Color.FromRgb(0x1E, 0x1E, 0x1E);
            var window = new Window
            {
                Background = new SolidColorBrush(expected),
                ShowInTaskbar = false,
            };

            try
            {
                nint handle = new WindowInteropHelper(window).EnsureHandle();

                Assert.True(WindowFirstPresentationHelper.TryApplyNativeClientBackground(window));
                HwndTarget target = Assert.IsType<HwndTarget>(HwndSource.FromHwnd(handle)?.CompositionTarget);
                Assert.Equal(expected, target.BackgroundColor);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void FirstPresentation_RendersOffscreenThenPublishesAtTheFinalPosition()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(FirstPresentation_RendersOffscreenThenPublishesAtTheFinalPosition)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            double finalLeft = SystemParameters.VirtualScreenLeft + 64d;
            double finalTop = SystemParameters.VirtualScreenTop + 64d;
            var window = new Window
            {
                Width = 280d,
                Height = 180d,
                Left = finalLeft,
                Top = finalTop,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Content = new Border(),
            };
            var positionsDuringRelocation = new List<Point>();
            window.LocationChanged += (_, _) =>
            {
                nint handle = new WindowInteropHelper(window).Handle;
                if (handle != nint.Zero)
                {
                    positionsDuringRelocation.Add(new Point(window.Left, window.Top));
                }
            };
            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                WindowFirstPresentationHelper.StageOffscreenFirstRender(window);
                window.ShowInTaskbar = true;
                WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);
                Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
                Assert.True(WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true));
                nint initialHandle = new WindowInteropHelper(window).Handle;
                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(
                    window,
                    activate: false,
                    waitForFirstRender: true);
                window.Show();

                Assert.True(window.Left < SystemParameters.VirtualScreenLeft);

                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Assert.True(reveal.Result);
                Assert.Equal(finalLeft, window.Left);
                Assert.Equal(finalTop, window.Top);
                Assert.True(window.ShowInTaskbar);
                Assert.Equal(1d, window.Opacity);
                Assert.Equal(initialHandle, new WindowInteropHelper(window).Handle);
                Assert.Contains(
                    positionsDuringRelocation,
                    position => Math.Abs(position.X - finalLeft) < 0.01d
                        && Math.Abs(position.Y - finalTop) < 0.01d);
                Assert.DoesNotContain(
                    positionsDuringRelocation,
                    position => (Math.Abs(position.X - finalLeft) < 0.01d)
                        != (Math.Abs(position.Y - finalTop) < 0.01d));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void FirstPresentation_UnpositionedWindowCreatesItsHandleOffscreenThenCentersOnReveal()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(FirstPresentation_UnpositionedWindowCreatesItsHandleOffscreenThenCentersOnReveal)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            const double width = 280d;
            const double height = 180d;
            var window = new Window
            {
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Content = new Border(),
            };

            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                WindowFirstPresentationHelper.StageOffscreenFirstRender(window);
                Assert.True(window.Left < SystemParameters.VirtualScreenLeft);
                Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);

                window.ShowInTaskbar = true;
                WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);
                Assert.True(WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true));
                nint initialHandle = new WindowInteropHelper(window).Handle;
                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(
                    window,
                    activate: false,
                    waitForFirstRender: true);
                window.Show();

                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Rect workArea = SystemParameters.WorkArea;
                Assert.True(reveal.Result);
                Assert.Equal(workArea.Left + ((workArea.Width - width) / 2d), window.Left, precision: 3);
                Assert.Equal(workArea.Top + ((workArea.Height - height) / 2d), window.Top, precision: 3);
                Assert.Equal(initialHandle, new WindowInteropHelper(window).Handle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void RevealAsync_WithdrawnVisibilityIntentNeverExposesThePreparedWindow()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(RevealAsync_WithdrawnVisibilityIntentNeverExposesThePreparedWindow)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new Window
            {
                Width = 280d,
                Height = 180d,
                Left = SystemParameters.VirtualScreenLeft + 64d,
                Top = SystemParameters.VirtualScreenTop + 64d,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
            };
            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                window.Show();

                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(
                    window,
                    activate: false,
                    canReveal: static () => false);
                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Assert.False(reveal.Result);
                Assert.Equal(0d, window.Opacity);
                Assert.False(window.ShowActivated);
                Assert.False(window.ShowInTaskbar);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void RevealAsync_CompletedFirstRenderPublishesOneVisibleInteractiveSurface()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(RevealAsync_CompletedFirstRenderPublishesOneVisibleInteractiveSurface)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new Window
            {
                Width = 280d,
                Height = 180d,
                Left = SystemParameters.VirtualScreenLeft + 64d,
                Top = SystemParameters.VirtualScreenTop + 64d,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
            };
            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                window.ShowInTaskbar = true;
                window.Opacity = 1d;
                window.Show();

                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(window, activate: false);
                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Assert.True(reveal.Result);
                Assert.True(window.IsVisible);
                Assert.Equal(Visibility.Visible, window.Visibility);
                Assert.Equal(1d, window.Opacity);
                Assert.False(window.ShowActivated);
                Assert.True(window.ShowInTaskbar);
            }
            finally
            {
                window.Close();
            }
        });
    }

}
