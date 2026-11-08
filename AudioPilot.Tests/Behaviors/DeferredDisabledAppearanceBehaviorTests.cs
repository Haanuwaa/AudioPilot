using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

[Collection("WpfApplicationIsolation")]
public sealed class DeferredDisabledAppearanceBehaviorTests
{
    [Theory]
    [InlineData("Dark", false)]
    [InlineData("Light", false)]
    [InlineData("Dark", true)]
    [InlineData("Light", true)]
    public async Task BriefDisableKeepsAppearance_WhileLongDisableAndInputGuardsRemainEffective(string theme, bool primary)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var resources = new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative) };
            var button = new Button { Resources = resources, Content = "Save", Width = 120, Height = 30 };
            if (primary) button.Style = (Style)resources["AppPrimaryButtonStyle"];
            using var source = new HwndSource(new HwndSourceParameters("Disabled button presentation")
            { Width = 120, Height = 30, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
            source.RootVisual = button;
            button.Measure(new Size(120, 30)); button.Arrange(new Rect(0, 0, 120, 30)); button.UpdateLayout();
            await button.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.True(button.IsVisible);
            double enabledOpacity = button.Opacity;
            button.IsEnabled = false;
            double disabledOpacity = button.Opacity;
            Assert.True(disabledOpacity < enabledOpacity);
            button.IsEnabled = true;

            var clocks = new List<AnimationClock>();
            var behavior = new DeferredDisabledAppearanceBehavior(() =>
            {
                var clock = new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop }.CreateClock();
                clocks.Add(clock);
                return clock;
            });
            behavior.Attach(button);
            try
            {
                button.IsEnabled = false;
                Assert.False(button.IsEnabled);
                Assert.Equal(enabledOpacity, button.Opacity);
                button.IsEnabled = true;
                Assert.Equal(enabledOpacity, button.Opacity);

                button.IsEnabled = false;
                Assert.Equal(enabledOpacity, button.Opacity);
                clocks[^1].Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(1), TimeSeekOrigin.BeginTime);
                Assert.False(button.IsEnabled);
                Assert.Equal(disabledOpacity, button.Opacity);

                button.IsEnabled = true;
                button.IsEnabled = false;
                Assert.Equal(enabledOpacity, button.Opacity);
                button.Visibility = Visibility.Hidden;
                Assert.Equal(disabledOpacity, button.Opacity);
                int hiddenCount = clocks.Count;
                button.IsEnabled = true;
                button.IsEnabled = false;
                Assert.Equal(hiddenCount, clocks.Count);
                button.Visibility = Visibility.Visible;
                Assert.Equal(disabledOpacity, button.Opacity);

                button.IsEnabled = true;
                button.IsEnabled = false;
                button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.Equal(disabledOpacity, button.Opacity);
                button.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(disabledOpacity, button.Opacity);

                button.IsEnabled = true;
                button.IsEnabled = false;
                behavior.Detach();
                Assert.Equal(disabledOpacity, button.Opacity);
                int created = clocks.Count;
                button.IsEnabled = true;
                button.IsEnabled = false;
                Assert.Equal(created, clocks.Count);
            }
            finally { behavior.Detach(); }
        });
    }
}
