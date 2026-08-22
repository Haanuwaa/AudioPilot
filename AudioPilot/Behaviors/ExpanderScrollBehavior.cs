using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudioPilot.Behaviors;

/// <summary>Reveals a toggled section after layout without moving keyboard focus.</summary>
public static class ExpanderScrollBehavior
{
    public static readonly DependencyProperty RevealOnToggleProperty = DependencyProperty.RegisterAttached(
        "RevealOnToggle", typeof(bool), typeof(ExpanderScrollBehavior), new PropertyMetadata(false, OnRevealOnToggleChanged));

    private static readonly DependencyProperty PendingRevealProperty = DependencyProperty.RegisterAttached(
        "PendingReveal", typeof(DispatcherOperation), typeof(ExpanderScrollBehavior));

    public static bool GetRevealOnToggle(DependencyObject target) => (bool)target.GetValue(RevealOnToggleProperty);
    public static void SetRevealOnToggle(DependencyObject target, bool value) => target.SetValue(RevealOnToggleProperty, value);

    private static void OnRevealOnToggleChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Expander expander) return;
        if ((bool)args.NewValue)
        {
            expander.Expanded += OnToggled;
            expander.Collapsed += OnToggled;
            expander.Unloaded += OnUnloaded;
        }
        else
        {
            expander.Expanded -= OnToggled;
            expander.Collapsed -= OnToggled;
            expander.Unloaded -= OnUnloaded;
            CancelPending(expander);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args) => CancelPending((Expander)sender);

    private static void CancelPending(Expander expander)
    {
        (expander.GetValue(PendingRevealProperty) as DispatcherOperation)?.Abort();
        expander.ClearValue(PendingRevealProperty);
    }

    private static void OnToggled(object sender, RoutedEventArgs args)
    {
        var expander = (Expander)sender;
        if (!ReferenceEquals(args.OriginalSource, expander)) return;
        CancelPending(expander);
        if (!expander.IsLoaded || expander.Dispatcher.HasShutdownStarted) return;
        expander.SetValue(PendingRevealProperty, expander.Dispatcher.BeginInvoke(() =>
        {
            expander.ClearValue(PendingRevealProperty);
            if (!expander.IsLoaded || !GetRevealOnToggle(expander)) return;
            DependencyObject? parent = VisualTreeHelper.GetParent(expander);
            while (parent != null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
            if (parent is not ScrollViewer viewer || !double.IsFinite(viewer.ViewportHeight) || viewer.ViewportHeight <= 0) return;

            // A tall section should reveal its beginning rather than jump to its last control.
            expander.BringIntoView(new Rect(0, 0, expander.ActualWidth, Math.Min(expander.ActualHeight, viewer.ViewportHeight)));
        }, DispatcherPriority.Loaded));
    }
}
