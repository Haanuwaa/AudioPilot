using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Tests.Helpers;

internal static partial class TestWindowFactory
{
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint handle, int attribute, in int value, int size);

    internal static Window CreateOffscreenWindow(
        bool showInTaskbar = false,
        double width = 1,
        double height = 1,
        AppTheme theme = AppTheme.System)
    {
        bool showWindows = TestExecutionGuards.ShouldShowTestWindows();

        var window = new Window
        {
            Width = showWindows ? (width > 1 ? width : 320) : 1,
            Height = showWindows ? (height > 1 ? height : 180) : 1,
            ShowInTaskbar = showWindows && showInTaskbar,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = showWindows ? 40 : -10000,
            Top = showWindows ? 40 : -10000,
            Opacity = showWindows ? 1 : 0,
            WindowStyle = showWindows ? WindowStyle.SingleBorderWindow : WindowStyle.None,
            ResizeMode = showWindows ? ResizeMode.CanResize : ResizeMode.NoResize,
            AllowsTransparency = !showWindows,
        };
        if (showWindows)
        {
            if (Application.Current == null) TestExecutionGuards.EnsureSharedWpfApplication();
            AppTheme effectiveTheme = WindowThemeHelper.ApplyThemeResources(window.Resources,
                WindowThemeHelper.ResolveEffectiveTheme(theme), SystemParameters.HighContrast);
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
            window.Title = "AudioPilot UI test";
            window.SourceInitialized += ApplyChrome;

            void ApplyChrome(object? sender, EventArgs args)
            {
                window.SourceInitialized -= ApplyChrome;
                WindowThemeHelper.ApplyWindowChrome(window, effectiveTheme, SystemParameters.HighContrast);
                _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window);
            }
        }
        else
        {
            window.Background = Brushes.Transparent;
            window.Visibility = Visibility.Hidden;
        }
        return window;
    }

    internal static void ShowWindowForTest(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        bool showWindows = TestExecutionGuards.ShouldShowTestWindows();
        if (!showWindows)
        {
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowState = WindowState.Minimized;
            window.Left = -10000;
            window.Top = -10000;
            window.Show();
            CompleteInitialLayout(window);
            return;
        }

        bool activate = window.ShowActivated;
        WindowFirstPresentationHelper.Prepare(window, hideFromTaskbar: false);
        WindowFirstPresentationHelper.StageOffscreenFirstRender(window);
        WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);
        _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true);
        using var lifetime = new CancellationTokenSource();
        Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(window, activate,
            waitForFirstRender: window.Content != null, cancellationToken: lifetime.Token);
        try
        {
            window.Show();
            CompleteInitialLayout(window);
            TestPrivateAccess.RunTaskOnDispatcher(reveal);
            Assert.True(reveal.GetAwaiter().GetResult(),
                $"The test window did not complete its first presentation: loaded={window.IsLoaded}, visible={window.IsVisible}, visibility={window.Visibility}, opacity={window.Opacity}, state={window.WindowState}.");
            window.ShowActivated = activate;
        }
        finally { lifetime.Cancel(); }
    }

    /// <summary>Runs native layout without exposing a window or taking keyboard focus, even across hide/show cycles.</summary>
    internal static void ShowWindowForLayoutTest(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - (double.IsFinite(window.Width) ? Math.Max(480, window.Width) : 480) - 256;
        window.Top = SystemParameters.VirtualScreenTop - (double.IsFinite(window.Height) ? Math.Max(420, window.Height) : 420) - 256;
        _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true);
        int cloaked = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 13, in cloaked, sizeof(int)));
        window.Show();
        CompleteInitialLayout(window);
    }

    internal static void CompleteInitialLayout(Window window)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
    }
}
