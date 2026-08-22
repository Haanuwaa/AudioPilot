using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Models;

namespace AudioPilot.Tests.Helpers;

internal static class TestWindowFactory
{
    internal static Window CreateOffscreenWindow(
        bool showInTaskbar = false,
        double width = 1,
        double height = 1,
        AppTheme theme = AppTheme.System)
    {
        bool showWindows = TestExecutionGuards.ShouldShowTestWindows();

        var window = new Window
        {
            Width = showWindows ? width : 1,
            Height = showWindows ? height : 1,
            ShowInTaskbar = showWindows && showInTaskbar,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = showWindows ? 40 : -10000,
            Top = showWindows ? 40 : -10000,
            Opacity = showWindows ? 1 : 0,
            WindowStyle = showWindows ? WindowStyle.SingleBorderWindow : WindowStyle.None,
            ResizeMode = showWindows ? ResizeMode.CanResize : ResizeMode.NoResize,
            AllowsTransparency = !showWindows,
            Visibility = Visibility.Hidden,
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
            }
        }
        else
        {
            window.Background = Brushes.Transparent;
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

        window.Show();
        CompleteInitialLayout(window);
    }

    internal static void CompleteInitialLayout(Window window)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
    }
}
