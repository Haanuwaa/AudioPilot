using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class ScrollBarJumpToPointBehaviorTests
{
    [Theory]
    [InlineData(Orientation.Vertical)]
    [InlineData(Orientation.Horizontal)]
    public void TrackClicks_SurviveTemplateReplacement_AndRespectThumbAndDisable(Orientation orientation)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var size = orientation == Orientation.Vertical ? new Size(12, 200) : new Size(200, 12);
            var scrollBar = new ScrollBar { Minimum = 0, Maximum = 100, Orientation = orientation };
            ScrollBarJumpToPointBehavior.SetIsEnabled(scrollBar, true);
            try
            {
                Track? previous = null;
                for (int index = 0; index < 3; index++)
                {
                    scrollBar.Template = (ControlTemplate)XamlReader.Parse("""
                        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                         TargetType="ScrollBar">
                            <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True">
                                <Track.DecreaseRepeatButton><RepeatButton /></Track.DecreaseRepeatButton>
                                <Track.Thumb><Thumb Height="20" /></Track.Thumb>
                                <Track.IncreaseRepeatButton><RepeatButton /></Track.IncreaseRepeatButton>
                            </Track>
                        </ControlTemplate>
                        """);
                    scrollBar.ApplyTemplate();
                    scrollBar.Measure(size);
                    scrollBar.Arrange(new Rect(new Point(), size));
                    if (index == 0) scrollBar.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var track = Assert.IsType<Track>(scrollBar.Template.FindName("PART_Track", scrollBar));
                    Assert.NotSame(previous, track);
                    Assert.True(Click(track.IncreaseRepeatButton), $"Track click was not handled for template {index}.");
                    Assert.False(Click(track.Thumb));
                    scrollBar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    scrollBar.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert.True(Click(track.IncreaseRepeatButton));
                    scrollBar.Visibility = Visibility.Collapsed;
                    scrollBar.Visibility = Visibility.Visible;
                    Assert.True(Click(track.IncreaseRepeatButton));
                    previous = track;
                }

                ScrollBarJumpToPointBehavior.SetIsEnabled(scrollBar, false);
                Assert.False(Click(previous!.IncreaseRepeatButton));
                ScrollBarJumpToPointBehavior.SetIsEnabled(scrollBar, true);
                Assert.True(Click(previous.IncreaseRepeatButton));
            }
            finally
            {
                ScrollBarJumpToPointBehavior.SetIsEnabled(scrollBar, false);
            }
        });

        static bool Click(UIElement target)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            };
            target.RaiseEvent(args);
            return args.Handled;
        }
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsMinimum_AtTopOfVerticalReversedTrack()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 0),
            new Size(10, 200),
            new Size(10, 40),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.Equal(0, value);
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsMaximum_AtBottomOfVerticalReversedTrack()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 200),
            new Size(10, 200),
            new Size(10, 40),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.Equal(100, value);
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsMidpoint_ForHorizontalTrack()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(60, 0),
            new Size(120, 10),
            new Size(20, 10),
            Orientation.Horizontal,
            10,
            30,
            isDirectionReversed: false);

        Assert.Equal(20, value);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(250, 100)]
    public void CalculateValueFromClickPosition_ClampsOutOfRangeCoordinates(double y, double expected)
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, y),
            new Size(10, 200),
            new Size(10, 40),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.Equal(expected, value);
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsNaN_WhenTrackLengthIsZero()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 10),
            new Size(0, 0),
            Size.Empty,
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.True(double.IsNaN(value));
    }

    [Fact]
    public void CalculateValueFromClickPosition_AlignsThumbCenterWithPointer()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 40),
            new Size(10, 200),
            new Size(10, 40),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.Equal(12.5, value);
    }

    [Theory]
    [InlineData(Orientation.Vertical, false, 75)]
    [InlineData(Orientation.Vertical, true, 25)]
    [InlineData(Orientation.Horizontal, false, 25)]
    [InlineData(Orientation.Horizontal, true, 75)]
    public void CalculateValueFromClickPosition_HonorsTrackDirection(
        Orientation orientation,
        bool isDirectionReversed,
        double expected)
    {
        Point clickPoint = orientation == Orientation.Vertical
            ? new Point(0, 60)
            : new Point(60, 0);
        Size trackSize = orientation == Orientation.Vertical
            ? new Size(10, 200)
            : new Size(200, 10);
        Size thumbSize = orientation == Orientation.Vertical
            ? new Size(10, 40)
            : new Size(40, 10);

        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            clickPoint,
            trackSize,
            thumbSize,
            orientation,
            0,
            100,
            isDirectionReversed);

        Assert.Equal(expected, value);
    }

    [Fact]
    public void CalculateValueFromClickPosition_ReturnsNaN_WhenThumbCannotTravel()
    {
        double value = ScrollBarJumpToPointBehavior.CalculateValueFromClickPosition(
            new Point(0, 50),
            new Size(10, 100),
            new Size(10, 100),
            Orientation.Vertical,
            0,
            100,
            isDirectionReversed: true);

        Assert.True(double.IsNaN(value));
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void ShouldHandleTrackClickCore_ReflectsEnabledTrackRangeAndThumbOrigin(
        bool isScrollBarEnabled,
        bool hasTrack,
        bool hasScrollableRange,
        bool isThumbOrigin,
        bool expected)
    {
        bool result = ScrollBarJumpToPointBehavior.ShouldHandleTrackClickCore(
            isScrollBarEnabled,
            hasTrack,
            hasScrollableRange,
            isThumbOrigin);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetScrollCommand_ReturnsVerticalOffsetCommand_ForVerticalOrientation()
    {
        RoutedCommand command = ScrollBarJumpToPointBehavior.GetScrollCommand(Orientation.Vertical);

        Assert.Same(ScrollBar.ScrollToVerticalOffsetCommand, command);
    }

    [Fact]
    public void GetScrollCommand_ReturnsHorizontalOffsetCommand_ForHorizontalOrientation()
    {
        RoutedCommand command = ScrollBarJumpToPointBehavior.GetScrollCommand(Orientation.Horizontal);

        Assert.Same(ScrollBar.ScrollToHorizontalOffsetCommand, command);
    }
}
