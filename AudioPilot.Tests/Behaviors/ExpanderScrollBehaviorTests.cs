using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

[Collection("WpfApplicationIsolation")]
public sealed class ExpanderScrollBehaviorTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(400)]
    public async Task TogglingRevealsTheSectionStart_AndCollapsingKeepsTheHeaderVisible(double contentHeight)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("Hidden expander layout")
            {
                Width = 360,
                Height = 160,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000),
            });
            var expander = new Expander { Header = "Details", Content = new Border { Height = contentHeight } };
            ExpanderScrollBehavior.SetRevealOnToggle(expander, true);
            var panel = new StackPanel();
            panel.Children.Add(new Border { Height = 120 });
            panel.Children.Add(expander);
            panel.Children.Add(new Border { Height = 500 });
            var viewer = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            source.RootVisual = viewer;
            await LayoutAsync();
            Assert.True(expander.IsLoaded);
            expander.IsExpanded = true;
            await LayoutAsync();
            Assert.InRange(expander.TranslatePoint(new Point(), viewer).Y, -1, 120);
            Assert.True(viewer.VerticalOffset > 0);
            Assert.InRange(expander.TranslatePoint(new Point(0, Math.Min(expander.ActualHeight, viewer.ViewportHeight)), viewer).Y, 0, viewer.ViewportHeight + 1);

            viewer.ScrollToVerticalOffset(180);
            await LayoutAsync();
            expander.IsExpanded = false;
            await LayoutAsync();
            Assert.InRange(expander.TranslatePoint(new Point(), viewer).Y, -1, viewer.ViewportHeight - expander.ActualHeight + 1);

            double unchanged = viewer.VerticalOffset;
            expander.IsExpanded = true;
            expander.IsExpanded = false;
            await LayoutAsync();
            Assert.Equal(unchanged, viewer.VerticalOffset);
            ExpanderScrollBehavior.SetRevealOnToggle(expander, false);
            source.RootVisual = null;

            async Task LayoutAsync()
            {
                viewer.Measure(new Size(360, 160));
                viewer.Arrange(new Rect(0, 0, 360, 160));
                viewer.UpdateLayout();
                await viewer.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                viewer.UpdateLayout();
            }
        });
    }
}
