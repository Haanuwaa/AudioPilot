using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AudioPilot.Behaviors
{
    /// <summary>Places the initial keyboard caret after existing text while preserving pointer placement and later editing positions.</summary>
    public static class TextBoxFocusBehavior
    {
        public static readonly DependencyProperty CaretAtEndOnFirstFocusProperty = DependencyProperty.RegisterAttached(
            "CaretAtEndOnFirstFocus", typeof(bool), typeof(TextBoxFocusBehavior), new PropertyMetadata(false, OnEnabledChanged));

        private static readonly DependencyProperty HasInteractedProperty = DependencyProperty.RegisterAttached(
            "HasInteracted", typeof(bool), typeof(TextBoxFocusBehavior), new PropertyMetadata(false));

        public static bool GetCaretAtEndOnFirstFocus(DependencyObject element) => (bool)element.GetValue(CaretAtEndOnFirstFocusProperty);

        public static void SetCaretAtEndOnFirstFocus(DependencyObject element, bool value) => element.SetValue(CaretAtEndOnFirstFocusProperty, value);

        private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            textBox.GotKeyboardFocus -= OnGotKeyboardFocus;
            textBox.RemoveHandler(Mouse.PreviewMouseDownEvent, (MouseButtonEventHandler)OnPreviewMouseDown);
            if ((bool)e.NewValue && !(bool)textBox.GetValue(HasInteractedProperty))
            {
                textBox.GotKeyboardFocus += OnGotKeyboardFocus;
                textBox.AddHandler(Mouse.PreviewMouseDownEvent, (MouseButtonEventHandler)OnPreviewMouseDown, handledEventsToo: true);
            }
        }

        private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            CompleteInitialInteraction((TextBox)sender);
        }

        private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var textBox = (TextBox)sender;
            if (!ReferenceEquals(e.NewFocus, textBox))
            {
                return;
            }

            CompleteInitialInteraction(textBox);
            if (!textBox.IsReadOnly && !textBox.AcceptsReturn && textBox.SelectionLength == 0 && textBox.CaretIndex == 0)
            {
                textBox.CaretIndex = textBox.Text.Length;
            }
        }

        private static void CompleteInitialInteraction(TextBox textBox)
        {
            textBox.SetValue(HasInteractedProperty, true);
            textBox.GotKeyboardFocus -= OnGotKeyboardFocus;
            textBox.RemoveHandler(Mouse.PreviewMouseDownEvent, (MouseButtonEventHandler)OnPreviewMouseDown);
        }
    }
}
