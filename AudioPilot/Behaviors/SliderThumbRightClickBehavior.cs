using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class SliderThumbRightClickBehavior : Behavior<Slider>
    {
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
            AssociatedObject.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
        }

        protected override void OnDetaching()
        {
            AssociatedObject.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
            base.OnDetaching();
        }

        private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AssociatedObject.Template?.FindName("PART_Track", AssociatedObject) is not Track { Thumb: { } thumb }
                || e.OriginalSource is not DependencyObject source
                || (!ReferenceEquals(source, thumb) && !thumb.IsAncestorOf(source)))
            {
                return;
            }

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
