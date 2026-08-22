using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class KeyboardFocusScrollBehavior : Behavior<ScrollViewer>
    {
        private const double FocusMargin = 12;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.GotKeyboardFocus += OnGotKeyboardFocus;
        }

        protected override void OnDetaching()
        {
            AssociatedObject.GotKeyboardFocus -= OnGotKeyboardFocus;
            base.OnDetaching();
        }

        private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice ||
                e.NewFocus is not FrameworkElement focusedElement)
            {
                return;
            }

            ScrollViewer viewer = AssociatedObject;
            _ = viewer.Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(AssociatedObject, viewer) ||
                    !ReferenceEquals(Keyboard.FocusedElement, focusedElement) ||
                    !viewer.IsVisible ||
                    !focusedElement.IsVisible ||
                    !viewer.IsAncestorOf(focusedElement))
                {
                    return;
                }

                double viewportHeight = viewer.ViewportHeight;
                if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
                {
                    return;
                }

                Rect bounds = focusedElement.TransformToAncestor(viewer)
                    .TransformBounds(new Rect(focusedElement.RenderSize));
                double margin = Math.Min(FocusMargin, viewportHeight / 4);
                if (bounds.Height > viewportHeight - (2 * margin))
                {
                    return;
                }

                if (bounds.Top < margin)
                {
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bounds.Top - margin);
                }
                else if (bounds.Bottom > viewportHeight - margin)
                {
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bounds.Bottom - viewportHeight + margin);
                }
            }, DispatcherPriority.ContextIdle);
        }
    }
}
