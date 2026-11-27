using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AudioPilot.Behaviors;
using AudioPilot.Localization;
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
                TestWindowFactory.SetActivationForTest(window, true);
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
    [InlineData("text", false)]
    [InlineData("value", false)]
    [InlineData("disabled", false)]
    [InlineData("text", true)]
    [InlineData("value", true)]
    [InlineData("disabled", true)]
    public void LeavingOrDeactivatingOwnerClosesHelp_AndItCannotReopenInBackground(string mode, bool deactivate)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var logger = Logger.CreateInMemoryForTests();
            var service = new InfoPopupService(logger);
            var target = new TextBox { Text = "Shortcut", IsEnabled = mode != "disabled" };
            var localization = new LocalizationService("en");
            LocalizationContext.SetService(target, localization);
            var window = new InvisibleWindow { Content = target };
            using var disabled = mode == "disabled" ? new DisabledToolTipSupport(target, () => "Disabled help") : null;
            try
            {
                window.Show();
                window.UpdateLayout();
                TestWindowFactory.SetActivationForTest(window, true);
                ToolTip tooltip;
                if (disabled != null)
                {
                    Assert.True(disabled.Refresh());
                    tooltip = Assert.IsType<ToolTip>(target.ToolTip);
                    tooltip.Opacity = 0;
                    tooltip.IsOpen = true;
                }
                else
                {
                    tooltip = TestPrivateAccess.GetField<ToolTip>(service, "_popup");
                    tooltip.Opacity = 0;
                    if (mode == "value") service.Show(target, 50, 0, 0);
                    else service.ShowText(target, "Help text");
                }
                Assert.True(tooltip.IsOpen);

                if (mode == "value")
                {
                    var caption = TestPrivateAccess.GetField<TextBlock>(service, "_labelText");
                    Assert.Equal("Volume: ", caption.Text);
                    localization.SetLanguage("es");
                    Assert.Equal("Volumen: ", caption.Text);
                    Assert.Equal($"{50d:F1}", TestPrivateAccess.GetField<TextBlock>(service, "_valueText").Text);
                    Assert.True(tooltip.IsOpen);
                }

                if (deactivate) TestWindowFactory.SetActivationForTest(window, false);
                else window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                Assert.False(tooltip.IsOpen);
                Assert.Equal(DependencyProperty.UnsetValue, TestPrivateAccess.GetField<TextBlock>(service, "_labelText").ReadLocalValue(LocalizationContext.ServiceProperty));
                Assert.Null(TestPrivateAccess.GetField<Window?>(disabled ?? (object)service,
                    disabled != null ? "_mouseWindow" : "_currentWindow"));
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                service.UpdateText("Changed while away", HotkeyWarningKind.None);
                service.UpdateValue(60);
                Assert.False(tooltip.IsOpen);

                if (!deactivate) return;
                ShowHelp();
                Assert.False(tooltip.IsOpen);
                TestWindowFactory.SetActivationForTest(window, true);
                Assert.False(tooltip.IsOpen);
                ShowHelp();
                Assert.True(tooltip.IsOpen);

                void ShowHelp()
                {
                    if (disabled != null) tooltip.IsOpen = true;
                    else if (mode == "value") service.Show(target, 50, 0, 0);
                    else service.ShowText(target, "Help text");
                }
            }
            finally { service.Hide(target); window.Close(); }
        });
    }

    [Theory]
    [InlineData("deactivate")]
    [InlineData("leave-window")]
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
                TestWindowFactory.SetActivationForTest(window, true);
                bool shown = false;
                Exception? failure = null;
                Task pending = HoverDelayCoordinator.ExecuteAfterDelayAsync(target, Timeout.Infinite, () => true,
                    () => shown = true, ex => failure = ex, CancellationToken.None);
                switch (action)
                {
                    case "deactivate": TestWindowFactory.SetActivationForTest(window, false); break;
                    case "leave-window": window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent }); break;
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

    [Fact]
    public async Task InactiveShortcutField_DoesNotStartDelayedHelp()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var target = new TextBox();
            var window = new InvisibleWindow { Content = target };
            try
            {
                window.Show();
                window.UpdateLayout();
                TestWindowFactory.SetActivationForTest(window, true);
                TestWindowFactory.SetActivationForTest(window, false);
                bool shown = false;
                await HoverDelayCoordinator.ExecuteAfterDelayAsync(target, 0, () => true,
                    () => shown = true, ex => Assert.Fail(ex.ToString()), CancellationToken.None);
                Assert.False(shown);
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
    }
}
