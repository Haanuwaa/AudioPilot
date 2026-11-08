using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors;

/// <summary>Bounds a linked slider gesture so its original volume offsets survive clamping and reversal.</summary>
public sealed class MixerGroupSliderBehavior : Behavior<FrameworkElement>
{
    private AudioSessionItem? _gestureItem;
    public static readonly DependencyProperty MixerProperty = DependencyProperty.Register(nameof(Mixer), typeof(MixerViewModel), typeof(MixerGroupSliderBehavior),
        new PropertyMetadata(null, static (owner, args) => ((MixerGroupSliderBehavior)owner).End(args.OldValue as MixerViewModel)));
    public MixerViewModel? Mixer { get => (MixerViewModel?)GetValue(MixerProperty); set => SetValue(MixerProperty, value); }
    protected override void OnAttached()
    {
        AssociatedObject.PreviewMouseLeftButtonDown += MouseDown;
        AssociatedObject.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(MouseButtonUp), true);
        AssociatedObject.LostMouseCapture += MouseUp;
        AssociatedObject.PreviewKeyDown += KeyDown;
        AssociatedObject.PreviewKeyUp += KeyUp;
        AssociatedObject.LostKeyboardFocus += LostFocus;
        AssociatedObject.DataContextChanged += ContextChanged;
        AssociatedObject.Unloaded += Unloaded;
    }
    protected override void OnDetaching()
    {
        End();
        AssociatedObject.PreviewMouseLeftButtonDown -= MouseDown;
        AssociatedObject.RemoveHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(MouseButtonUp));
        AssociatedObject.LostMouseCapture -= MouseUp;
        AssociatedObject.PreviewKeyDown -= KeyDown;
        AssociatedObject.PreviewKeyUp -= KeyUp;
        AssociatedObject.LostKeyboardFocus -= LostFocus;
        AssociatedObject.DataContextChanged -= ContextChanged;
        AssociatedObject.Unloaded -= Unloaded;
    }
    private void Begin()
    {
        if (AssociatedObject.DataContext is AudioSessionItem item)
        {
            _gestureItem = item;
            Mixer?.BeginGroupVolumeEdit(item);
        }
    }
    private void End(MixerViewModel? mixer = null)
    {
        if (_gestureItem == null) return;
        (mixer ?? Mixer)?.EndGroupVolumeEdit(_gestureItem);
        _gestureItem = null;
    }
    private bool IsSliderInput(object source)
    {
        for (DependencyObject? current = source as DependencyObject; current != null && !ReferenceEquals(current, AssociatedObject);)
        {
            if (current is Slider) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
    private void MouseDown(object sender, MouseButtonEventArgs e) { if (IsSliderInput(e.OriginalSource)) Begin(); }
    private void MouseUp(object sender, MouseEventArgs e) => End();
    private void MouseButtonUp(object sender, MouseButtonEventArgs e) => End();
    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e) => End();
    private void Unloaded(object sender, RoutedEventArgs e) => End();
    private void LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not DependencyObject next || !AssociatedObject.IsAncestorOf(next)) End();
    }
    private void KeyDown(object sender, KeyEventArgs e)
    {
        if (!e.IsRepeat && IsVolumeKey(e.Key) && IsSliderInput(e.OriginalSource)) Begin();
    }
    private void KeyUp(object sender, KeyEventArgs e) { if (IsVolumeKey(e.Key)) End(); }
    private static bool IsVolumeKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;
}
