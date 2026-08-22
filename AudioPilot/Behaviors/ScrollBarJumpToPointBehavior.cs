using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioPilot.Behaviors
{
    public static class ScrollBarJumpToPointBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ScrollBarJumpToPointBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollBar scrollBar)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                scrollBar.PreviewMouseLeftButtonDown += OnScrollBarPreviewMouseLeftButtonDown;
            }
            else
            {
                scrollBar.PreviewMouseLeftButtonDown -= OnScrollBarPreviewMouseLeftButtonDown;
            }
        }

        private static void OnScrollBarPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ScrollBar scrollBar
                || scrollBar.Template?.FindName("PART_Track", scrollBar) is not Track track
                || e.OriginalSource is not DependencyObject source
                || (!ReferenceEquals(source, track) && !track.IsAncestorOf(source))
                || !ShouldHandleTrackClick(scrollBar, track, source))
            {
                return;
            }

            Point clickPoint = e.GetPosition(track);
            double targetValue = CalculateValueFromClickPosition(
                clickPoint,
                track.RenderSize,
                track.Thumb?.RenderSize ?? Size.Empty,
                scrollBar.Orientation,
                scrollBar.Minimum,
                scrollBar.Maximum,
                track.IsDirectionReversed);

            if (double.IsNaN(targetValue))
            {
                return;
            }

            ExecuteScroll(scrollBar, targetValue);
            e.Handled = true;
        }

        internal static bool ShouldHandleTrackClick(ScrollBar scrollBar, Track? track, DependencyObject? originalSource)
        {
            return ShouldHandleTrackClickCore(
                scrollBar.IsEnabled,
                track != null,
                scrollBar.Maximum > scrollBar.Minimum,
                IsThumbOrigin(originalSource));
        }

        internal static bool ShouldHandleTrackClickCore(bool isScrollBarEnabled, bool hasTrack, bool hasScrollableRange, bool isThumbOrigin)
        {
            return isScrollBarEnabled
                && hasTrack
                && hasScrollableRange
                && !isThumbOrigin;
        }

        internal static bool IsThumbOrigin(DependencyObject? source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is Thumb)
                {
                    return true;
                }

                current = current switch
                {
                    Visual visual => System.Windows.Media.VisualTreeHelper.GetParent(visual),
                    System.Windows.Media.Media3D.Visual3D visual3D => System.Windows.Media.VisualTreeHelper.GetParent(visual3D),
                    _ => LogicalTreeHelper.GetParent(current)
                };
            }

            return false;
        }

        internal static double CalculateValueFromClickPosition(
            Point clickPoint,
            Size trackSize,
            Size thumbSize,
            Orientation orientation,
            double minimum,
            double maximum,
            bool isDirectionReversed)
        {
            double range = maximum - minimum;
            if (range <= 0)
            {
                return minimum;
            }

            double trackLength = orientation == Orientation.Vertical ? trackSize.Height : trackSize.Width;
            if (!double.IsFinite(trackLength) || trackLength <= 0)
            {
                return double.NaN;
            }

            double thumbLength = orientation == Orientation.Vertical ? thumbSize.Height : thumbSize.Width;
            if (!double.IsFinite(thumbLength))
            {
                return double.NaN;
            }

            double availableTravel = trackLength - Math.Clamp(thumbLength, 0d, trackLength);
            if (availableTravel <= 0)
            {
                return double.NaN;
            }

            double coordinate = orientation == Orientation.Vertical ? clickPoint.Y : clickPoint.X;
            double normalized = (coordinate - (thumbLength / 2d)) / availableTravel;
            normalized = Math.Clamp(normalized, 0d, 1d);

            bool increasesWithCoordinate = orientation == Orientation.Horizontal
                ? !isDirectionReversed
                : isDirectionReversed;
            if (!increasesWithCoordinate)
            {
                normalized = 1d - normalized;
            }

            return minimum + (range * normalized);
        }

        internal static RoutedCommand GetScrollCommand(Orientation orientation)
        {
            return orientation == Orientation.Vertical
                ? ScrollBar.ScrollToVerticalOffsetCommand
                : ScrollBar.ScrollToHorizontalOffsetCommand;
        }

        private static void ExecuteScroll(ScrollBar scrollBar, double targetValue)
        {
            RoutedCommand command = GetScrollCommand(scrollBar.Orientation);
            if (command.CanExecute(targetValue, scrollBar))
            {
                command.Execute(targetValue, scrollBar);
                return;
            }

            scrollBar.Value = targetValue;
        }
    }
}
