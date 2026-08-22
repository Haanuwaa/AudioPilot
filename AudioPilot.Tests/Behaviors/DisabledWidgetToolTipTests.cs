using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

[Collection("WpfApplicationIsolation")]
public sealed class DisabledWidgetToolTipTests
{
    [Theory]
    [InlineData("button")]
    [InlineData("combobox")]
    [InlineData("textbox")]
    public void ExplanatoryHelpWorksForOtherControlTypes(string kind)
    {
        TestExecutionGuards.RunSta(() =>
        {
            Control control = kind switch { "button" => new Button(), "combobox" => new ComboBox(), _ => new TextBox() };
            control.IsEnabled = false;
            var behavior = new HoverInfoPopupBehavior { Text = "Requirement explanation" };
            behavior.Attach(control);
            try
            {
                Assert.Equal(behavior.Text, Assert.IsType<TextBlock>(Assert.IsType<ToolTip>(control.ToolTip).Content).Text);
                Assert.True(ToolTipService.GetShowOnDisabled(control));
                Assert.False(control.IsEnabled);
            }
            finally
            {
                behavior.Detach();
            }
            Assert.Null(control.ToolTip);
        });
    }

    [Fact]
    public void DisabledSliderReportsUpdatedValueAndRestoresNormalBehaviorWhenEnabled()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var slider = new Slider { IsEnabled = false, Maximum = 100, Value = 25 };
            var behavior = new SliderValuePopupBehavior();
            behavior.Attach(slider);
            try
            {
                ToolTip tooltip = Assert.IsType<ToolTip>(slider.ToolTip);
                var text = Assert.IsType<TextBlock>(tooltip.Content);
                Assert.Equal($"Volume: {25:F1}", text.Text);
                slider.Value = 75;
                Assert.Same(tooltip, slider.ToolTip);
                Assert.Equal($"Volume: {75:F1}", text.Text);
                Assert.False(slider.IsEnabled);

                slider.IsEnabled = true;
                Assert.Null(slider.ToolTip);
                Assert.Equal(75, slider.Value);
            }
            finally
            {
                behavior.Detach();
            }
        });
    }

    [Fact]
    public void DisabledTruncatedNameRefreshesTextAndTrimmingBeforeShowing()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var text = new TextBlock { Text = "A long application name that does not fit", TextTrimming = TextTrimming.CharacterEllipsis };
            var button = new Button { IsEnabled = false, Content = text };
            text.Measure(new Size(70, 30));
            text.Arrange(new Rect(0, 0, 70, 30));
            var behavior = new TrimmedTextPopupBehavior();
            behavior.Attach(button);
            try
            {
                ToolTip tooltip = Assert.IsType<ToolTip>(button.ToolTip);
                var content = Assert.IsType<TextBlock>(tooltip.Content);
                Assert.Equal(text.Text, content.Text);
                text.Text = "Short";
                Assert.True(RaiseToolTipOpening(button).Handled);
                Assert.Empty(content.Text);
                Assert.False(tooltip.IsOpen);

                text.Text = "A different long application name that does not fit";
                Assert.False(RaiseToolTipOpening(button).Handled);
                Assert.Same(tooltip, button.ToolTip);
                Assert.Equal(text.Text, content.Text);
            }
            finally
            {
                behavior.Detach();
            }
            Assert.Null(button.ToolTip);
        });
    }

    [Fact]
    public void UnloadedTargetCannotRecreateTooltipUntilReloadedAndDisposalDetachesEvents()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var target = new Button { IsEnabled = false };
            using var support = new DisabledToolTipSupport(target, () => "Help");
            Assert.True(support.Refresh());
            target.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Null(target.ToolTip);
            Assert.False(support.Refresh());
            Assert.Null(target.ToolTip);

            target.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.IsType<ToolTip>(target.ToolTip);
            support.Dispose();
            target.IsEnabled = true;
            target.IsEnabled = false;
            target.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Null(target.ToolTip);
        });
    }

    [Fact]
    public void FailedDynamicContentIsSuppressedAndCanRecoverOnNextHover()
    {
        TestExecutionGuards.RunSta(() =>
        {
            bool fail = true;
            var target = new Button { IsEnabled = false };
            using var support = new DisabledToolTipSupport(target, () => fail ? throw new InvalidOperationException("Fixture failure") : "Recovered help");
            Assert.False(support.Refresh());
            ToolTip tooltip = Assert.IsType<ToolTip>(target.ToolTip);
            Assert.False(tooltip.IsOpen);
            Assert.True(RaiseToolTipOpening(target).Handled);
            fail = false;
            Assert.False(RaiseToolTipOpening(target).Handled);
            Assert.Equal("Recovered help", Assert.IsType<TextBlock>(tooltip.Content).Text);
        });
    }

    private static ToolTipEventArgs RaiseToolTipOpening(FrameworkElement target)
    {
        var args = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [true], culture: null)!;
        target.RaiseEvent(args);
        return args;
    }
}
