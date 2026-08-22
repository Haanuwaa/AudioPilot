using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AudioPilot.Behaviors;
using AudioPilot.Helpers;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class SliderKeyboardAccelerationBehaviorTests
{
    [Fact]
    public void HoldAcceleration_PreservesFineStart_AndSmoothlyReachesBoundedPeak()
    {
        Assert.Equal(0.1, SliderKeyboardAccelerationBehavior.GetHoldStep(0));
        Assert.Equal(0.1, SliderKeyboardAccelerationBehavior.GetHoldStep(400));
        Assert.Equal(0.675, SliderKeyboardAccelerationBehavior.GetHoldStep(1900), 6);
        Assert.Equal(1.25, SliderKeyboardAccelerationBehavior.GetHoldStep(3400));
        Assert.Equal(1.25, SliderKeyboardAccelerationBehavior.GetHoldStep(60000));
        double previous = 0.1;
        for (int elapsed = 480; elapsed <= 3440; elapsed += 80)
        {
            double current = SliderKeyboardAccelerationBehavior.GetHoldStep(elapsed);
            Assert.InRange(current - previous, 0, 0.047);
            previous = current;
        }
    }

    [Fact]
    public void ArrowRepeat_IsConsumedWithoutExtraSteps_AndPreservesTwoWayBinding()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var source = new Slider { Maximum = 100, Value = 50 };
            var slider = new Slider { Maximum = 100 };
            slider.SetBinding(Slider.ValueProperty, new Binding(nameof(Slider.Value)) { Source = source, Mode = BindingMode.TwoWay });
            var behavior = new SliderKeyboardAccelerationBehavior();
            behavior.Attach(slider);
            try
            {
                Assert.True(behavior.HandleKeyDown(Key.Right, false, ModifierKeys.None));
                Assert.True(behavior.HandleKeyDown(Key.Right, true, ModifierKeys.None));
                Assert.Equal(50.1, slider.Value, 6);
                Assert.Equal(50.1, source.Value, 6);
                Assert.True(BindingOperations.IsDataBound(slider, Slider.ValueProperty));
                source.Value = 70;
                Assert.Equal(70, slider.Value);
                Assert.True(behavior.HandleKeyDown(Key.Left, false, ModifierKeys.None));
                Assert.Equal(69.9, slider.Value, 6);
                behavior.HandleKeyUp(Key.Right);
                Assert.True(behavior.HandleKeyDown(Key.Left, true, ModifierKeys.None));
                Assert.Equal(69.9, slider.Value, 6);
                behavior.HandleKeyUp(Key.Left);
                Assert.True(behavior.HandleKeyDown(Key.Left, false, ModifierKeys.None));
                Assert.Equal(69.8, slider.Value, 6);
            }
            finally { behavior.Detach(); }
        });
    }

    [Theory]
    [InlineData(Key.Right, false, FlowDirection.LeftToRight, 50.1)]
    [InlineData(Key.Left, false, FlowDirection.LeftToRight, 49.9)]
    [InlineData(Key.Up, false, FlowDirection.LeftToRight, 50.1)]
    [InlineData(Key.Down, false, FlowDirection.LeftToRight, 49.9)]
    [InlineData(Key.Right, true, FlowDirection.LeftToRight, 49.9)]
    [InlineData(Key.Right, false, FlowDirection.RightToLeft, 49.9)]
    [InlineData(Key.Right, true, FlowDirection.RightToLeft, 50.1)]
    [InlineData(Key.Up, false, FlowDirection.RightToLeft, 50.1)]
    public void Arrows_RespectDirection(Key key, bool reversed, FlowDirection flow, double expected)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var slider = new Slider { Maximum = 100, Value = 50, IsDirectionReversed = reversed, FlowDirection = flow };
            var behavior = new SliderKeyboardAccelerationBehavior();
            behavior.Attach(slider);
            try
            {
                Assert.True(behavior.HandleKeyDown(key, false, ModifierKeys.None));
                Assert.Equal(expected, slider.Value, 6);
            }
            finally { behavior.Detach(); }
        });
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("unload")]
    [InlineData("disable")]
    [InlineData("detach")]
    public void InteractionEnds_ResetHeldKeyAndRequireFreshPress(string reason)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var slider = new Slider { Maximum = 100, Value = 50 };
            var behavior = new SliderKeyboardAccelerationBehavior();
            behavior.Attach(slider);
            try
            {
                Assert.True(behavior.HandleKeyDown(Key.Right, false, ModifierKeys.None));
                switch (reason)
                {
                    case "focus":
                        slider.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, slider, null)
                        { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                        break;
                    case "unload": slider.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
                    case "disable": slider.IsEnabled = false; slider.IsEnabled = true; break;
                    case "detach": behavior.Detach(); behavior.Attach(slider); break;
                }
                Assert.True(behavior.HandleKeyDown(Key.Right, true, ModifierKeys.None));
                Assert.Equal(50.1, slider.Value, 6);
                Assert.True(behavior.HandleKeyDown(Key.Right, false, ModifierKeys.None));
                Assert.Equal(50.2, slider.Value, 6);
            }
            finally { behavior.Detach(); }
        });
    }

    [Fact]
    public void Space_MutesOncePerPress_AndModifiedArrowsAreNotCaptured()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            int executions = 0;
            var slider = new Slider { Maximum = 100, Value = 50 };
            var behavior = new SliderKeyboardAccelerationBehavior { MuteCommand = new RelayCommand(() => executions++) };
            behavior.Attach(slider);
            try
            {
                Assert.True(behavior.HandleKeyDown(Key.Space, false, ModifierKeys.None));
                Assert.True(behavior.HandleKeyDown(Key.Space, true, ModifierKeys.None));
                Assert.Equal(1, executions);
                behavior.HandleKeyUp(Key.Space);
                Assert.True(behavior.HandleKeyDown(Key.Space, false, ModifierKeys.None));
                Assert.Equal(2, executions);
                Assert.False(behavior.HandleKeyDown(Key.Right, false, ModifierKeys.Control));
                Assert.False(behavior.HandleKeyDown(Key.Home, false, ModifierKeys.None));
                Assert.Equal(50, slider.Value);
                slider.Value = 100;
                Assert.True(behavior.HandleKeyDown(Key.Right, false, ModifierKeys.None));
                Assert.Equal(100, slider.Value);
            }
            finally { behavior.Detach(); }
        });
    }
}
