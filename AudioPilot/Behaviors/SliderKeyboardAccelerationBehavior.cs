using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    /// <summary>
    /// Provides fine arrow-key adjustments with bounded hold acceleration. Native key repeats are consumed
    /// so they cannot add a second stream of volume changes alongside the dispatcher timer.
    /// </summary>
    public sealed class SliderKeyboardAccelerationBehavior : Behavior<Slider>
    {
        private DispatcherTimer? _movementTimer;
        private Key? _heldKey;
        private bool _spaceHeld;
        private long _keyPressTimestamp;
        private const double SinglePressStep = 0.1;
        private const double MaximumHoldStep = 1.25;
        private const double HoldDetectionDelayMs = 400;
        private const double RampDurationMs = 3000;
        private const double ContinuousIntervalMs = 80;

        public ICommand? MuteCommand
        {
            get => (ICommand?)GetValue(MuteCommandProperty);
            set => SetValue(MuteCommandProperty, value);
        }

        public static readonly DependencyProperty MuteCommandProperty =
            DependencyProperty.Register(
                nameof(MuteCommand), typeof(ICommand), typeof(SliderKeyboardAccelerationBehavior), new PropertyMetadata(null));

        public object? MuteCommandParameter
        {
            get => GetValue(MuteCommandParameterProperty);
            set => SetValue(MuteCommandParameterProperty, value);
        }

        public static readonly DependencyProperty MuteCommandParameterProperty =
            DependencyProperty.Register(
                nameof(MuteCommandParameter), typeof(object), typeof(SliderKeyboardAccelerationBehavior), new PropertyMetadata(null));

        protected override void OnAttached()
        {
            base.OnAttached();
            _movementTimer = new DispatcherTimer(DispatcherPriority.Input, AssociatedObject.Dispatcher);
            _movementTimer.Tick += OnMovementTick;
            AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
            AssociatedObject.PreviewKeyUp += OnPreviewKeyUp;
            AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
            AssociatedObject.Unloaded += OnUnloaded;
            AssociatedObject.IsEnabledChanged += OnIsEnabledChanged;
        }

        protected override void OnDetaching()
        {
            ResetInput();
            _movementTimer?.Tick -= OnMovementTick;
            _movementTimer = null;
            AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
            AssociatedObject.PreviewKeyUp -= OnPreviewKeyUp;
            AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;
            AssociatedObject.Unloaded -= OnUnloaded;
            AssociatedObject.IsEnabledChanged -= OnIsEnabledChanged;
            base.OnDetaching();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = HandleKeyDown(e.Key, e.IsRepeat, Keyboard.Modifiers);
        }

        internal bool HandleKeyDown(Key key, bool isRepeat, ModifierKeys modifiers)
        {
            if (AssociatedObject == null || !AssociatedObject.IsEnabled || modifiers != ModifierKeys.None)
            {
                ResetInput();
                return false;
            }

            if (key == Key.Space && MuteCommand != null)
            {
                if (!_spaceHeld && !isRepeat)
                {
                    _spaceHeld = true;
                    object? parameter = ReadLocalValue(MuteCommandParameterProperty) == DependencyProperty.UnsetValue
                        ? AssociatedObject.DataContext
                        : MuteCommandParameter;
                    if (MuteCommand.CanExecute(parameter)) { MuteCommand.Execute(parameter); }
                }
                return true;
            }

            if (key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            {
                ResetInput();
                return false;
            }

            if (_heldKey == key || isRepeat) { return true; }

            _movementTimer?.Stop();
            _heldKey = key;
            _keyPressTimestamp = Stopwatch.GetTimestamp();
            if (MoveSlider(SinglePressStep) && _movementTimer != null)
            {
                _movementTimer.Interval = TimeSpan.FromMilliseconds(HoldDetectionDelayMs);
                _movementTimer.Start();
            }
            return true;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e) => HandleKeyUp(e.Key);

        internal void HandleKeyUp(Key key)
        {
            if (key == Key.Space) { _spaceHeld = false; }
            if (key == _heldKey)
            {
                _movementTimer?.Stop();
                _heldKey = null;
            }
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!AssociatedObject.IsKeyboardFocusWithin) { ResetInput(); }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => ResetInput();

        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!AssociatedObject.IsEnabled) { ResetInput(); }
        }

        private void ResetInput()
        {
            _movementTimer?.Stop();
            _heldKey = null;
            _spaceHeld = false;
        }

        private void OnMovementTick(object? sender, EventArgs e)
        {
            if (AssociatedObject == null || !AssociatedObject.IsKeyboardFocusWithin || !AssociatedObject.IsEnabled
                || _heldKey is not Key key || !Keyboard.IsKeyDown(key) || Keyboard.Modifiers != ModifierKeys.None)
            {
                ResetInput();
                return;
            }

            double elapsedMs = Stopwatch.GetElapsedTime(_keyPressTimestamp).TotalMilliseconds;
            _movementTimer!.Interval = TimeSpan.FromMilliseconds(ContinuousIntervalMs);
            if (!MoveSlider(GetHoldStep(elapsedMs))) { _movementTimer.Stop(); }
        }

        internal static double GetHoldStep(double elapsedMs)
        {
            double progress = Math.Clamp((elapsedMs - HoldDetectionDelayMs) / RampDurationMs, 0, 1);
            double easedProgress = progress * progress * (3 - 2 * progress);
            return SinglePressStep + (MaximumHoldStep - SinglePressStep) * easedProgress;
        }

        private bool MoveSlider(double step)
        {
            if (AssociatedObject == null || _heldKey is not Key key) { return false; }

            bool increase = key is Key.Right or Key.Up;
            bool reversed = AssociatedObject.IsDirectionReversed;
            if (key is Key.Left or Key.Right && AssociatedObject.FlowDirection == FlowDirection.RightToLeft) { reversed = !reversed; }
            double newValue = Math.Clamp(AssociatedObject.Value + (increase != reversed ? step : -step),
                AssociatedObject.Minimum, AssociatedObject.Maximum);
            if (newValue == AssociatedObject.Value) { return false; }

            AssociatedObject.SetCurrentValue(Slider.ValueProperty, newValue);
            return newValue > AssociatedObject.Minimum && newValue < AssociatedObject.Maximum;
        }
    }
}
