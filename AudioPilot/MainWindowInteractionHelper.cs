using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AudioPilot.Behaviors;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot
{
    internal static class MainWindowInteractionHelper
    {
        internal static void ClearWidgetFocus(Window window)
        {
            IInputElement? focused = FocusManager.GetFocusedElement(window);
            if (focused is TextBox field)
                Interaction.GetBehaviors(field).OfType<HotkeyCaptureBehavior>().FirstOrDefault()?.ReleaseCapture();
            if (window.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            if (focused is DependencyObject widget)
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(widget), null);
            FocusManager.SetFocusedElement(window, window);
            if (window.IsVisible && window.IsActive && window.WindowState != WindowState.Minimized)
                Keyboard.Focus(window);
        }

        public static bool ShouldClearRootFocus(DependencyObject? source, DependencyObject? root)
        {
            if (source == null)
            {
                return false;
            }

            if (IsAnyComboBoxDropDownOpen(root))
            {
                return false;
            }

            return !IsInteractiveElement(source);
        }

        public static double ClampMouseWheelOffset(double currentOffset, double scrollableHeight, int delta)
        {
            if (delta == 0)
            {
                return currentOffset;
            }

            double scrollStep = delta > 0 ? -48.0 : 48.0;
            return Math.Max(0, Math.Min(currentOffset + scrollStep, scrollableHeight));
        }

        public static bool UnselectListBoxIfSelected(ListBox? listBox)
        {
            if (listBox == null || listBox.SelectedIndex < 0)
            {
                return false;
            }

            listBox.UnselectAll();
            return true;
        }

        public static bool IsAnyComboBoxDropDownOpen(DependencyObject? root)
        {
            if (root == null)
            {
                return false;
            }

            if (root is ComboBox comboBox && comboBox.IsDropDownOpen)
            {
                return true;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                if (IsAnyComboBoxDropDownOpen(VisualTreeHelper.GetChild(root, i)))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsInteractiveElement(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase ||
                    source is TextBoxBase ||
                    source is ComboBox ||
                    source is ComboBoxItem ||
                    source is Hyperlink ||
                    source is TextElement ||
                    source is Popup ||
                    source is ListBoxItem ||
                    source is Slider ||
                    source is Thumb ||
                    source is CheckBox ||
                    source is TabItem ||
                    source is ScrollBar)
                {
                    return true;
                }

                if (source is FrameworkElement frameworkElement &&
                    (frameworkElement.TemplatedParent is ComboBoxItem || frameworkElement.TemplatedParent is ComboBox))
                {
                    return true;
                }

                source = GetParentObject(source);
            }

            return false;
        }

        private static DependencyObject? GetParentObject(DependencyObject source)
        {
            if (source is Visual || source is Visual3D)
            {
                DependencyObject? visualParent = VisualTreeHelper.GetParent(source);
                if (visualParent != null)
                {
                    return visualParent;
                }
            }

            if (source is FrameworkContentElement frameworkContentElement)
            {
                if (frameworkContentElement.Parent != null)
                {
                    return frameworkContentElement.Parent;
                }

                return frameworkContentElement.TemplatedParent;
            }

            if (source is ContentElement contentElement)
            {
                return ContentOperations.GetParent(contentElement);
            }

            return LogicalTreeHelper.GetParent(source);
        }
    }
}
