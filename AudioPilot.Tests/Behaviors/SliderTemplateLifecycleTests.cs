using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using AudioPilot.Behaviors;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class SliderTemplateLifecycleTests
{
    [Fact]
    public void ThumbMute_SurvivesTemplateReplacement_AndDetaches()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var slider = new Slider { DataContext = new object() };
            int executions = 0;
            using var command = new RelayCommand(parameter =>
            {
                Assert.Same(slider.DataContext, parameter);
                executions++;
            });
            var behavior = new SliderThumbRightClickBehavior { Command = command };
            behavior.Attach(slider);
            try
            {
                for (int index = 0; index < 3; index++)
                {
                    Track track = ReplaceTemplate(slider);
                    if (index == 0) slider.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert.False(RightClick(track));
                    Assert.True(RightClick(track.Thumb), $"Thumb click was not handled for template {index}.");
                    Assert.Equal(index + 1, executions);
                }
            }
            finally { behavior.Detach(); }
            Assert.False(RightClick(((Track)slider.Template.FindName("PART_Track", slider)).Thumb));
            Assert.Equal(3, executions);
        });
    }

    [Fact]
    public void PopupDragLifecycle_SurvivesTemplateReplacement_AndDetaches()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var slider = new Slider { IsEnabled = false };
            var behavior = new SliderValuePopupBehavior(new InfoPopupService(Logger.Instance));
            behavior.Attach(slider);
            try
            {
                for (int index = 0; index < 3; index++)
                {
                    Track track = ReplaceTemplate(slider);
                    if (index == 0) slider.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
                    Assert.True(TestPrivateAccess.GetField<bool>(behavior, "_isDragging"), $"Drag start was missed for template {index}.");
                    track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
                    Assert.False(TestPrivateAccess.GetField<bool>(behavior, "_isDragging"));
                }
            }
            finally { behavior.Detach(); }
            ((Track)slider.Template.FindName("PART_Track", slider)).Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
            Assert.False(TestPrivateAccess.GetField<bool>(behavior, "_isDragging"));
        });
    }

    private static Track ReplaceTemplate(Slider slider)
    {
        slider.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
                <Track x:Name="PART_Track">
                    <Track.Thumb><Thumb /></Track.Thumb>
                </Track>
            </ControlTemplate>
            """);
        slider.ApplyTemplate();
        return (Track)slider.Template.FindName("PART_Track", slider);
    }

    private static bool RightClick(UIElement target)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        };
        target.RaiseEvent(args);
        return args.Handled;
    }
}
