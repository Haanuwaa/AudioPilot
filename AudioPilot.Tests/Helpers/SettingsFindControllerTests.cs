using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Helpers;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Tests.Helpers;

[Collection("WpfApplicationIsolation")]
public sealed class SettingsFindControllerTests
{
    [Fact]
    public async Task SearchUsesLabelsAndHelp_RevealsCollapsedControls_AndPreservesTheirValues()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var scope = new StackPanel();
            var hiddenPanel = new StackPanel { Visibility = Visibility.Collapsed };
            hiddenPanel.Children.Add(new Button { Content = "Seek duration" });
            scope.Children.Add(hiddenPanel);
            scope.Children.Add(new CheckBox { Content = "Seek duration", Visibility = Visibility.Hidden });
            var row = new StackPanel();
            row.Children.Add(new Label { Content = "Seek duration" });
            var field = new TextBox { Text = "30s" };
            row.Children.Add(field);
            var help = new HoverInfoPopupBehavior { Text = "Skip forward and backward by this duration." };
            Interaction.GetBehaviors(field).Add(help);
            var expander = new Expander { Header = "Seek", Content = row };
            scope.Children.Add(expander);
            var privacy = new CheckBox { Content = "Redact sensitive log content" };
            AutomationProperties.SetHelpText(privacy, "Protect private names and paths.");
            scope.Children.Add(privacy);
            using var find = new SettingsFindController(scope, new ScrollViewer { Content = scope });
            try
            {
                find.Search("seek duration");
                Assert.Same(field, find.CurrentMatch);
                Assert.True(expander.IsExpanded);
                Assert.Equal("1 of 1", find.Status);
                await scope.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.Equal("30s", field.Text);
                find.Search("private paths");
                Assert.Same(privacy, find.CurrentMatch);
                Assert.False(expander.IsExpanded);
                find.Search("not a setting");
                Assert.False(find.HasMatches);
                Assert.Null(find.CurrentMatch);
                find.Clear();
                Assert.Equal("30s", field.Text);
                Assert.False(expander.IsExpanded);
            }
            finally { Interaction.GetBehaviors(field).Remove(help); }
        });
    }

    [Fact]
    public async Task FindCentersExpandedMatches_AndClampsAtThePageEdges()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("Hidden settings find layout")
            {
                Width = 360,
                Height = 180,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000),
            });
            var first = new CheckBox { Content = "First setting", Height = 24 };
            var middle = new TextBox { Height = 40, Margin = new Thickness(0, 100, 0, 100) };
            AutomationProperties.SetName(middle, "Middle setting");
            var expander = new Expander { Header = "Details", Content = middle };
            ExpanderScrollBehavior.SetRevealOnToggle(expander, true);
            var last = new Button { Content = "Last setting", Height = 28 };
            var scope = new StackPanel();
            scope.Children.Add(first);
            scope.Children.Add(new Border { Height = 300 });
            scope.Children.Add(expander);
            scope.Children.Add(new Border { Height = 300 });
            scope.Children.Add(last);
            var viewer = new ScrollViewer { Content = scope, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            source.RootVisual = viewer;
            using var find = new SettingsFindController(scope, viewer);
            try
            {
                await LayoutAsync();
                find.Search("setting");
                await LayoutAsync();
                Assert.Same(first, find.CurrentMatch);
                Assert.Equal(0, viewer.VerticalOffset);

                find.Move(1);
                await LayoutAsync();
                Assert.True(expander.IsExpanded);
                Assert.Same(middle, find.CurrentMatch);
                double midpoint = middle.TranslatePoint(new Point(0, middle.ActualHeight / 2), scope).Y - viewer.VerticalOffset;
                Assert.InRange(Math.Abs(midpoint - viewer.ViewportHeight / 2), 0, 1);

                find.Move(1);
                await LayoutAsync();
                Assert.Same(last, find.CurrentMatch);
                Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset);

                find.Move(1);
                await LayoutAsync();
                Assert.Same(first, find.CurrentMatch);
                Assert.Equal(0, viewer.VerticalOffset);

                find.Move(-1);
                await LayoutAsync();
                Assert.Same(last, find.CurrentMatch);
                Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset);
            }
            finally
            {
                ExpanderScrollBehavior.SetRevealOnToggle(expander, false);
                source.RootVisual = null;
            }

            async Task LayoutAsync()
            {
                viewer.Measure(new Size(360, 180));
                viewer.Arrange(new Rect(0, 0, 360, 180));
                viewer.UpdateLayout();
                await viewer.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                viewer.UpdateLayout();
            }
        });
    }

    [Fact]
    public async Task NextAndPreviousWrap_AndDisposalCancelsPendingReveal()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var scope = new StackPanel();
            var first = new CheckBox { Content = "Output roles" };
            var second = new CheckBox { Content = "Input roles" };
            scope.Children.Add(first); scope.Children.Add(second);
            var find = new SettingsFindController(scope, new ScrollViewer { Content = scope });
            find.Search("roles");
            Assert.Same(first, find.CurrentMatch);
            find.Move(-1);
            Assert.Same(second, find.CurrentMatch);
            find.Move(1);
            Assert.Same(first, find.CurrentMatch);
            find.Dispose();
            await scope.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Null(find.CurrentMatch);
            Assert.Equal(Visibility.Visible, first.Visibility);
            Assert.Equal(Visibility.Visible, second.Visibility);
        });
    }
}
