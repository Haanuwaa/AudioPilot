using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioPilot.Behaviors;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

[Trait(TestCategories.Name, TestCategories.VisualWpf)]
[Collection("WpfApplicationIsolation")]
public sealed class SliderValuePopupBehaviorTests
{
    [VisualIntegrationFact]
    public void MouseInteraction_FollowsPointer_KeepsClicksOpen_AndDismissesOnLeave()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var service = new InfoPopupService(Logger.Instance);
            var slider = new Slider { Width = 130, Maximum = 100, Value = 50 };
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 300, height: 200);
            window.Content = slider;
            var behavior = new SliderValuePopupBehavior(service);
            behavior.Attach(slider);
            try
            {
                window.Show();
                TestWindowFactory.CompleteInitialLayout(window);
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Assert.True(service.IsActiveFor(slider));
                Popup popup = TestPrivateAccess.GetField<Popup>(service, "_popup");
                int closes = 0;
                popup.Closed += (_, _) => closes++;
                var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
                foreach (UIElement target in new UIElement[] { slider, track.Thumb })
                {
                    foreach (MouseButton button in new[] { MouseButton.Left, MouseButton.Right, MouseButton.Left })
                    {
                        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent });
                        Assert.True(service.IsActiveFor(slider));
                        Assert.Equal(0, closes);
                    }
                }

                popup.HorizontalOffset = Mouse.GetPosition(slider).X + 20;
                track.Thumb.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                Assert.Equal(Mouse.GetPosition(slider).X, popup.HorizontalOffset, 6);
                Assert.Equal(behavior.VerticalOffset, popup.VerticalOffset);
                Assert.Equal(0, closes);
                double anchor = popup.HorizontalOffset;
                slider.Value = 80;
                Assert.Equal(anchor, popup.HorizontalOffset);
                slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                Assert.False(service.IsActiveFor(slider));
                slider.Value = 30;
                Assert.False(service.IsActiveFor(slider));
            }
            finally
            {
                behavior.Detach();
                window.Close();
            }
        });
    }

    [VisualIntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyboardPopup_DismissesOnScrollbarInput_WithoutFocusChange(bool wheel)
    {
        TestExecutionGuards.RunSta(() =>
        {
            var service = new InfoPopupService(Logger.Instance);
            var slider = new Slider { Width = 130, Maximum = 100, Value = 50 };
            var scrollbar = new ScrollBar { Focusable = false };
            var panel = new StackPanel();
            panel.Children.Add(slider);
            panel.Children.Add(scrollbar);
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 300, height: 200);
            window.Content = panel;
            var behavior = new SliderValuePopupBehavior(service);
            behavior.Attach(slider);
            try
            {
                window.Show();
                TestWindowFactory.CompleteInitialLayout(window);
                slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Right)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.True(service.IsActiveFor(slider));
                slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                Assert.True(service.IsActiveFor(slider));
                if (wheel)
                {
                    scrollbar.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                }
                else
                {
                    scrollbar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                }
                Assert.False(service.IsActiveFor(slider));
                slider.Value = 70;
                Assert.False(service.IsActiveFor(slider));
                Assert.Null(TestPrivateAccess.GetField<Window?>(behavior, "_inputWindow"));
            }
            finally
            {
                behavior.Detach();
                window.Close();
            }
        });
    }
}
