using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class SliderKeyboardAccelerationBehavior : Behavior<Slider>
    {
        private DispatcherTimer? _rampTimer;
        private DispatcherTimer? _continuousTimer;
        private Key? _heldKey;
        private DateTime _keyPressStartTime;
        private int _currentRampLevel;
        private const double SinglePressStep = 0.1;
        private const double BaseContinuousStep = 0.1;
        private const double RampStepIncrement = 0.05;
        private const double HoldDetectionDelayMs = 400;
        private const double RampIntervalMs = 600;
        private const int MaxRampLevel = 5;
        private const double ContinuousIntervalMs = 80;

        public ICommand? MuteCommand
        {
            get => (ICommand?)GetValue(MuteCommandProperty);
            set => SetValue(MuteCommandProperty, value);
        }

        public static readonly DependencyProperty MuteCommandProperty =
            DependencyProperty.Register(
                nameof(MuteCommand),
                typeof(ICommand),
                typeof(SliderKeyboardAccelerationBehavior),
                new PropertyMetadata(null));

        public object? MuteCommandParameter
        {
            get => GetValue(MuteCommandParameterProperty);
            set => SetValue(MuteCommandParameterProperty, value);
        }

        public static readonly DependencyProperty MuteCommandParameterProperty =
            DependencyProperty.Register(
                nameof(MuteCommandParameter),
                typeof(object),
                typeof(SliderKeyboardAccelerationBehavior),
                new PropertyMetadata(null));

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
            AssociatedObject.PreviewKeyUp += OnPreviewKeyUp;
        }

        protected override void OnDetaching()
        {
            StopRampTimer();
            StopContinuousTimer();
            if (AssociatedObject != null)
            {
                AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
                AssociatedObject.PreviewKeyUp -= OnPreviewKeyUp;
            }
            base.OnDetaching();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                ExecuteMuteCommand();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Left && e.Key != Key.Right)
            {
                return;
            }

            if (_heldKey == e.Key)
            {
                return;
            }

            _heldKey = e.Key;
            _keyPressStartTime = DateTime.Now;
            _currentRampLevel = 0;

            MoveSlider(SinglePressStep);
            e.Handled = true;

            StartHoldDetectionTimer();
        }

        private void ExecuteMuteCommand()
        {
            ICommand? command = MuteCommand;
            object? parameter = ResolveMuteCommandParameter();
            if (command?.CanExecute(parameter) == true)
            {
                command.Execute(parameter);
            }
        }

        private object? ResolveMuteCommandParameter()
        {
            return ReadLocalValue(MuteCommandParameterProperty) == DependencyProperty.UnsetValue
                ? AssociatedObject.DataContext
                : MuteCommandParameter;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != _heldKey)
            {
                return;
            }

            StopRampTimer();
            StopContinuousTimer();
            _heldKey = null;
        }

        private void StartHoldDetectionTimer()
        {
            StopRampTimer();

            _rampTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(HoldDetectionDelayMs)
            };
            _rampTimer.Tick += OnHoldDetectionTick;
            _rampTimer.Start();
        }

        private void StopRampTimer()
        {
            if (_rampTimer != null)
            {
                _rampTimer.Stop();
                _rampTimer.Tick -= OnHoldDetectionTick;
                _rampTimer = null;
            }
        }

        private void OnHoldDetectionTick(object? sender, EventArgs e)
        {
            StopRampTimer();
            StartContinuousMovement();
            StartRampTimer();
        }

        private void StartRampTimer()
        {
            StopRampTimer();

            _rampTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(RampIntervalMs)
            };
            _rampTimer.Tick += OnRampTick;
            _rampTimer.Start();
        }

        private void OnRampTick(object? sender, EventArgs e)
        {
            if (_currentRampLevel < MaxRampLevel)
            {
                _currentRampLevel++;
            }
        }

        private void StartContinuousMovement()
        {
            StopContinuousTimer();

            _continuousTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ContinuousIntervalMs)
            };
            _continuousTimer.Tick += OnContinuousTick;
            _continuousTimer.Start();
        }

        private void StopContinuousTimer()
        {
            if (_continuousTimer != null)
            {
                _continuousTimer.Stop();
                _continuousTimer.Tick -= OnContinuousTick;
                _continuousTimer = null;
            }
        }

        private void OnContinuousTick(object? sender, EventArgs e)
        {
            double step = BaseContinuousStep + (_currentRampLevel * RampStepIncrement);
            MoveSlider(step);
        }

        private void MoveSlider(double step)
        {
            if (AssociatedObject == null || _heldKey == null)
            {
                return;
            }

            double newValue = AssociatedObject.Value + (_heldKey == Key.Right ? step : -step);
            newValue = Math.Max(AssociatedObject.Minimum, Math.Min(AssociatedObject.Maximum, newValue));
            AssociatedObject.Value = newValue;
        }
    }
}
