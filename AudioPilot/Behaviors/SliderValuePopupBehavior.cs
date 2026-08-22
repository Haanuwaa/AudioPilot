using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class SliderValuePopupBehavior : Behavior<Slider>
    {
        public double HorizontalOffset
        {
            get => (double)GetValue(HorizontalOffsetProperty);
            set => SetValue(HorizontalOffsetProperty, value);
        }

        public static readonly DependencyProperty HorizontalOffsetProperty =
            DependencyProperty.Register(nameof(HorizontalOffset),
                typeof(double),
                typeof(SliderValuePopupBehavior),
                new PropertyMetadata(0.0));

        public double VerticalOffset
        {
            get => (double)GetValue(VerticalOffsetProperty);
            set => SetValue(VerticalOffsetProperty, value);
        }

        public static readonly DependencyProperty VerticalOffsetProperty =
            DependencyProperty.Register(nameof(VerticalOffset),
                typeof(double),
                typeof(SliderValuePopupBehavior),
                new PropertyMetadata(-30.0));

        private Thumb? _thumb;
        private bool _isDragging;
        private CancellationTokenSource? _hoverDelayCts;
        private static readonly Logger _logger = Logger.Instance;
        private static InfoPopupService PopupService => InfoPopupService.Instance;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.Loaded += OnLoaded;
            AssociatedObject.Unloaded += OnUnloaded;
            AssociatedObject.ValueChanged += OnValueChanged;
            AssociatedObject.MouseEnter += OnMouseEnter;
            AssociatedObject.MouseLeave += OnMouseLeave;
            AssociatedObject.MouseMove += OnMouseMove;
        }

        protected override void OnDetaching()
        {
            Cleanup();
            base.OnDetaching();
        }

        private void Cleanup()
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);

            PopupService.Hide(AssociatedObject);

            AssociatedObject.Loaded -= OnLoaded;
            AssociatedObject.Unloaded -= OnUnloaded;
            AssociatedObject.ValueChanged -= OnValueChanged;
            AssociatedObject.MouseEnter -= OnMouseEnter;
            AssociatedObject.MouseLeave -= OnMouseLeave;
            AssociatedObject.MouseMove -= OnMouseMove;

            DetachThumb();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            AttachThumb();
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            PopupService.Hide(AssociatedObject);
            _isDragging = false;
            DetachThumb();
        }

        private void AttachThumb()
        {
            DetachThumb();
            _thumb = FindThumb(AssociatedObject);
            if (_thumb != null)
            {
                _thumb.DragStarted += OnDragStarted;
                _thumb.DragDelta += OnDragDelta;
                _thumb.DragCompleted += OnDragCompleted;
            }
        }

        private void DetachThumb()
        {
            if (_thumb != null)
            {
                _thumb.DragStarted -= OnDragStarted;
                _thumb.DragDelta -= OnDragDelta;
                _thumb.DragCompleted -= OnDragCompleted;
                _thumb = null;
            }
        }

        private void OnDragStarted(object? sender, DragStartedEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            _isDragging = true;
            ShowPopup(mouseBased: false);
        }

        private void OnDragDelta(object? sender, DragDeltaEventArgs e)
        {
            UpdatePopupPosition(mouseBased: false);
        }

        private void OnDragCompleted(object? sender, DragCompletedEventArgs e)
        {
            _isDragging = false;
            if (!IsMouseOverSlider())
            {
                PopupService.Hide(AssociatedObject);
            }
        }

        private async void OnMouseEnter(object? sender, MouseEventArgs e)
        {
            if (_isDragging) return;

            CancellationToken hoverDelayToken = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);

            await HoverDelayCoordinator.ExecuteAfterDelayAsync(
                AppConstants.Timing.TooltipHoverDelayMs,
                () => !_isDragging && IsMouseOverSlider(),
                () => ShowPopup(mouseBased: true),
                ex => _logger.Warning("SliderValuePopupBehavior", "Failed to process slider value popup", nameof(OnMouseEnter), ex),
                hoverDelayToken);
        }

        private void OnMouseLeave(object? sender, MouseEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            if (_isDragging) return;

            PopupService.Hide(AssociatedObject);
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            if (_isDragging) return;
            if (PopupService.IsActiveFor(AssociatedObject))
            {
                UpdatePopupPosition(mouseBased: true);
            }
        }

        private void OnValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PopupService.IsActiveFor(AssociatedObject))
            {
                PopupService.UpdateValue(AssociatedObject.Value);
            }
        }

        private void ShowPopup(bool mouseBased)
        {
            var (x, y) = CalculateOffset(mouseBased);
            PopupService.Show(AssociatedObject, AssociatedObject.Value, x, y);
        }

        private void UpdatePopupPosition(bool mouseBased)
        {
            var (x, y) = CalculateOffset(mouseBased);
            PopupService.UpdatePosition(x, y);
            PopupService.UpdateValue(AssociatedObject.Value);
        }

        private (double x, double y) CalculateOffset(bool mouseBased)
        {
            double x;
            if (mouseBased)
            {
                var p = Mouse.GetPosition(AssociatedObject);
                x = p.X;
            }
            else if (_thumb != null)
            {
                var pos = _thumb.TranslatePoint(new Point(_thumb.ActualWidth / 2, 0), AssociatedObject);
                x = pos.X;
            }
            else
            {
                var ratio = (AssociatedObject.Value - AssociatedObject.Minimum) /
                            Math.Max(1.0, AssociatedObject.Maximum - AssociatedObject.Minimum);
                x = ratio * AssociatedObject.ActualWidth;
            }

            return (x + HorizontalOffset, VerticalOffset);
        }

        private bool IsMouseOverSlider()
        {
            var p = Mouse.GetPosition(AssociatedObject);
            return new Rect(0, 0, AssociatedObject.ActualWidth, AssociatedObject.ActualHeight).Contains(p);
        }

        private static Thumb? FindThumb(Slider slider)
        {
            if (slider.Template == null) return null;
            slider.ApplyTemplate();
            var track = slider.Template.FindName("PART_Track", slider) as Track;
            return track?.Thumb;
        }
    }
}
