using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioPilot.Behaviors;

/// <summary>Hands wheel input to the containing page when a compact list reaches its boundary.</summary>
public static class NestedListScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NestedListScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        list.PreviewMouseWheel -= OnMouseWheel;
        if (e.NewValue is true) list.PreviewMouseWheel += OnMouseWheel;
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ListBox list || FindScrollViewer(list) is not { } inner) return;
        if (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight) return;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(list); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer outer) continue;
            e.Handled = true;
            outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = outer,
            });
            return;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
