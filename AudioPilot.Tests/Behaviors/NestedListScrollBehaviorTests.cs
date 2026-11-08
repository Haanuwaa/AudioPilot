using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

[Collection("WpfApplicationIsolation")]
public sealed class NestedListScrollBehaviorTests
{
    [Fact]
    public void WheelHandsOffOnlyAtListBoundaries_WithoutChangingSelection()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var list = new ListBox { Height = 100 };
            NestedListScrollBehavior.SetIsEnabled(list, true);
            var panel = new StackPanel();
            panel.Children.Add(list);
            panel.Children.Add(new Border { Height = 1000 });
            var outer = new ScrollViewer { Content = panel };
            var window = TestWindowFactory.CreateOffscreenWindow();
            window.Content = outer;
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                outer.Measure(new Size(300, 300));
                outer.Arrange(new Rect(0, 0, 300, 300));
                outer.UpdateLayout();
                Assert.True(Wheel(-120));
                outer.UpdateLayout();
                Assert.True(outer.VerticalOffset > 0);
                foreach (int index in Enumerable.Range(0, 30)) list.Items.Add($"Device {index}");
                list.SelectedIndex = 10;
                outer.UpdateLayout();
                var inner = Assert.Single(WpfVisualTree.Descendants(list).OfType<ScrollViewer>());
                inner.ScrollToVerticalOffset(5);
                outer.UpdateLayout();
                Assert.InRange(inner.VerticalOffset, 1, inner.ScrollableHeight - 1);
                Assert.False(Wheel(-120));
                Assert.False(Wheel(120));
                inner.ScrollToBottom();
                outer.UpdateLayout();
                Assert.True(Wheel(-120));
                inner.ScrollToTop();
                outer.UpdateLayout();
                Assert.True(Wheel(120));
                Assert.Equal(10, list.SelectedIndex);
                NestedListScrollBehavior.SetIsEnabled(list, false);
                Assert.False(Wheel(120));

                bool Wheel(int delta)
                {
                    var input = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    list.RaiseEvent(input);
                    return input.Handled;
                }
            }
            finally { window.Close(); }
        });
    }
}
