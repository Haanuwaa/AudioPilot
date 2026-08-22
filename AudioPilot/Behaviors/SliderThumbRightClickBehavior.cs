using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class SliderThumbRightClickBehavior : Behavior<Slider>
    {
        private Thumb? _thumb;

        public ICommand? Command
        {
            get => (ICommand?)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(
                nameof(Command),
                typeof(ICommand),
                typeof(SliderThumbRightClickBehavior),
                new PropertyMetadata(null));

        public object? CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        public static readonly DependencyProperty CommandParameterProperty =
            DependencyProperty.Register(
                nameof(CommandParameter),
                typeof(object),
                typeof(SliderThumbRightClickBehavior),
                new PropertyMetadata(null));

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.Loaded += OnLoaded;
            AssociatedObject.Unloaded += OnUnloaded;
        }

        protected override void OnDetaching()
        {
            DetachThumb();
            if (AssociatedObject != null)
            {
                AssociatedObject.Loaded -= OnLoaded;
                AssociatedObject.Unloaded -= OnUnloaded;
            }
            base.OnDetaching();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            AttachThumb();
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            DetachThumb();
        }

        private void AttachThumb()
        {
            DetachThumb();
            if (AssociatedObject.Template == null)
            {
                return;
            }

            AssociatedObject.ApplyTemplate();
            Track? track = AssociatedObject.Template.FindName("PART_Track", AssociatedObject) as Track;
            _thumb = track?.Thumb;
            _thumb?.PreviewMouseRightButtonDown += OnThumbPreviewMouseRightButtonDown;
        }

        private void DetachThumb()
        {
            if (_thumb == null)
            {
                return;
            }

            _thumb.PreviewMouseRightButtonDown -= OnThumbPreviewMouseRightButtonDown;
            _thumb = null;
        }

        private void OnThumbPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            ICommand? command = Command;
            object? parameter = ResolveCommandParameter();
            if (command?.CanExecute(parameter) != true)
            {
                return;
            }

            command.Execute(parameter);
            e.Handled = true;
        }

        private object? ResolveCommandParameter()
        {
            return ReadLocalValue(CommandParameterProperty) == DependencyProperty.UnsetValue
                ? AssociatedObject.DataContext
                : CommandParameter;
        }
    }
}
