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
        public SliderValuePopupBehavior() { }

        internal SliderValuePopupBehavior(InfoPopupService popupService) => _popupService = popupService;

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
        private bool _isKeyboardInteraction;
        private Point _keyboardMousePosition;
        private Window? _inputWindow;
        private CancellationTokenSource? _hoverDelayCts;
        private static readonly Logger _logger = Logger.Instance;
        private readonly InfoPopupService? _popupService;
        private InfoPopupService PopupService => _popupService ?? InfoPopupService.Instance;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.Loaded += OnLoaded;
            AssociatedObject.Unloaded += OnUnloaded;
            AssociatedObject.ValueChanged += OnValueChanged;
            AssociatedObject.MouseEnter += OnMouseEnter;
            AssociatedObject.MouseLeave += OnMouseLeave;
            AssociatedObject.PreviewMouseMove += OnMouseMove;
            AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
            AssociatedObject.PreviewMouseDown += OnPreviewMouseDown;
            AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
            if (AssociatedObject.IsLoaded) { AttachThumb(); }
        }

        protected override void OnDetaching()
        {
            Cleanup();
            base.OnDetaching();
        }

        private void Cleanup()
        {
            DismissPopup();
            _isDragging = false;

            AssociatedObject.Loaded -= OnLoaded;
            AssociatedObject.Unloaded -= OnUnloaded;
            AssociatedObject.ValueChanged -= OnValueChanged;
            AssociatedObject.MouseEnter -= OnMouseEnter;
            AssociatedObject.MouseLeave -= OnMouseLeave;
            AssociatedObject.PreviewMouseMove -= OnMouseMove;
            AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
            AssociatedObject.PreviewMouseDown -= OnPreviewMouseDown;
            AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;

            DetachThumb();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            AttachThumb();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None
                || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Space))
            {
                DismissPopup();
                return;
            }

            _isKeyboardInteraction = true;
            _keyboardMousePosition = Mouse.GetPosition(AssociatedObject);
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            ShowPopup();
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            _isKeyboardInteraction = false;
            if (e.ChangedButton is MouseButton.Left or MouseButton.Right) { ShowPopup(); }
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!AssociatedObject.IsKeyboardFocusWithin && !_isDragging)
            {
                DismissPopup();
            }
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            DismissPopup();
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
                _thumb.DragCompleted += OnDragCompleted;
            }
        }

        private void DetachThumb()
        {
            if (_thumb != null)
            {
                _thumb.DragStarted -= OnDragStarted;
                _thumb.DragCompleted -= OnDragCompleted;
                _thumb = null;
            }
        }

        private void OnDragStarted(object? sender, DragStartedEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            _isKeyboardInteraction = false;
            _isDragging = true;
            ShowPopup();
        }

        private void OnDragCompleted(object? sender, DragCompletedEventArgs e)
        {
            _isDragging = false;
            if (!IsMouseOverSlider())
            {
                DismissPopup();
            }
        }

        private async void OnMouseEnter(object? sender, MouseEventArgs e)
        {
            if (_isDragging || _isKeyboardInteraction) return;

            CancellationToken hoverDelayToken = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);

            await HoverDelayCoordinator.ExecuteAfterDelayAsync(
                AppConstants.Timing.TooltipHoverDelayMs,
                () => !_isDragging && !_isKeyboardInteraction && IsMouseOverSlider(),
                ShowPopup,
                ex => _logger.Warning("SliderValuePopupBehavior", "Failed to process slider value popup", nameof(OnMouseEnter), ex),
                hoverDelayToken);
        }

        private void OnMouseLeave(object? sender, MouseEventArgs e)
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            if (_isDragging || _isKeyboardInteraction) return;

            DismissPopup();
        }

        private void OnValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PopupService.IsActiveFor(AssociatedObject))
            {
                PopupService.UpdateValue(AssociatedObject.Value);
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            Point position = e.GetPosition(AssociatedObject);
            if (_isKeyboardInteraction && position == _keyboardMousePosition) { return; }

            _isKeyboardInteraction = false;
            if (PopupService.IsActiveFor(AssociatedObject))
            {
                PopupService.UpdatePosition(position.X + HorizontalOffset, VerticalOffset);
            }
        }

        private void ShowPopup()
        {
            if (_inputWindow == null && Window.GetWindow(AssociatedObject) is Window window)
            {
                _inputWindow = window;
                window.PreviewMouseDown += OnWindowMouseInput;
                window.PreviewMouseWheel += OnWindowMouseInput;
                window.Deactivated += OnWindowDeactivated;
            }

            double x = (_isKeyboardInteraction ? AssociatedObject.ActualWidth / 2 : Mouse.GetPosition(AssociatedObject).X) + HorizontalOffset;
            if (PopupService.IsActiveFor(AssociatedObject))
            {
                PopupService.UpdatePosition(x, VerticalOffset);
                PopupService.UpdateValue(AssociatedObject.Value);
                return;
            }

            PopupService.Show(AssociatedObject, AssociatedObject.Value, x, VerticalOffset);
        }

        private void OnWindowMouseInput(object sender, MouseEventArgs e)
        {
            if (e is MouseButtonEventArgs
                && e.OriginalSource is System.Windows.Media.Visual source
                && (ReferenceEquals(source, AssociatedObject) || AssociatedObject.IsAncestorOf(source)))
            {
                return;
            }

            DismissPopup();
        }

        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            _isDragging = false;
            DismissPopup();
        }

        private void DismissPopup()
        {
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            _isKeyboardInteraction = false;
            PopupService.Hide(AssociatedObject);
            if (_inputWindow != null)
            {
                _inputWindow.PreviewMouseDown -= OnWindowMouseInput;
                _inputWindow.PreviewMouseWheel -= OnWindowMouseInput;
                _inputWindow.Deactivated -= OnWindowDeactivated;
                _inputWindow = null;
            }
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
