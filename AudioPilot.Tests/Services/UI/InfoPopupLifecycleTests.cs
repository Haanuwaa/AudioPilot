using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AudioPilot.Behaviors;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed partial class InfoPopupLifecycleTests
{
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    [Theory]
    [InlineData("DarkTheme")]
    [InlineData("LightTheme")]
    public void Help_IsNativelyClickThrough_UsesOwnerTheme_AndReleasesClosedOwner(string themeName)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var logger = Logger.CreateInMemoryForTests();
            var service = new InfoPopupService(logger);
            var target = new Button { Content = "Help" };
            var window = new InvisibleWindow { Content = target };
            var theme = new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{themeName}.xaml", UriKind.Relative) };
            window.Resources.MergedDictionaries.Add(theme);
            ToolTip tooltip = TestPrivateAccess.GetField<ToolTip>(service, "_popup");
            tooltip.Opacity = 0;
            try
            {
                window.Show();
                window.UpdateLayout();
                service.ShowText(target, "Help text");
                Assert.True(tooltip.IsOpen);
                Assert.Same(theme[typeof(ToolTip)], tooltip.Style);
                var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(tooltip));
                Assert.NotEqual(0, GetWindowLong(source.Handle, -20) & 0x20);
                Assert.False(Assert.IsType<UIElement>(source.RootVisual, exactMatch: false).IsHitTestVisible);
                var border = Assert.IsType<Border>(System.Windows.Media.VisualTreeHelper.GetChild(tooltip, 0));
                Assert.Equal(8, border.CornerRadius.TopLeft);
                TextBlock value = TestPrivateAccess.GetField<TextBlock>(service, "_valueText");
                Assert.Same(theme["TextBrush"], value.Foreground);
                service.UpdateText("Warning", HotkeyWarningKind.Reserved);
                Assert.Same(theme["HotkeyReservedBrush"], value.Foreground);
                window.Close();
                Assert.False(tooltip.IsOpen);
                Assert.Null(tooltip.PlacementTarget);
                Assert.Null(TestPrivateAccess.GetField<Window?>(service, "_currentWindow"));
            }
            finally { service.Hide(target); window.Close(); }
        });
    }

    [Theory]
    [InlineData("deactivate")]
    [InlineData("hide")]
    [InlineData("minimize")]
    [InlineData("close")]
    public async Task PendingHelp_CompletesWithoutShowing_WhenOwnerBecomesUnavailable(string action)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var target = new Button();
            var window = new InvisibleWindow { Content = target };
            try
            {
                window.Show();
                window.UpdateLayout();
                bool shown = false;
                Exception? failure = null;
                Task pending = HoverDelayCoordinator.ExecuteAfterDelayAsync(target, Timeout.Infinite, () => true,
                    () => shown = true, ex => failure = ex, CancellationToken.None);
                switch (action)
                {
                    case "deactivate": window.DeactivateForTest(); break;
                    case "hide": window.Hide(); break;
                    case "minimize": window.WindowState = WindowState.Minimized; break;
                    default: window.Close(); break;
                }
                await pending.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(shown);
                Assert.Null(failure);
            }
            finally { window.Close(); }
        });
    }

    private sealed class InvisibleWindow : Window
    {
        internal InvisibleWindow()
        {
            Width = 200;
            Height = 100;
            Left = -30000;
            Top = -30000;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Opacity = 0;
            ShowActivated = false;
            ShowInTaskbar = false;
        }

        internal void DeactivateForTest() => OnDeactivated(EventArgs.Empty);
    }
}
