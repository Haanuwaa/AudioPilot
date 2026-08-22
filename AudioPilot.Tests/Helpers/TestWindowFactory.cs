using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

internal static class TestWindowFactory
{
    internal static Window CreateOffscreenWindow(
        bool showInTaskbar = false,
        double width = 1,
        double height = 1)
    {
        bool showWindows = TestExecutionGuards.ShouldShowTestWindows();

        return new Window
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
            Background = showWindows ? SystemColors.WindowBrush : Brushes.Transparent,
            Visibility = Visibility.Hidden,
        };
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
