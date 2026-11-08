using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors;

/// <summary>Suppresses brief command-disable flashes while preserving immediate input and accessibility guards.</summary>
public sealed class DeferredDisabledAppearanceBehavior : Behavior<Button>
{
    private readonly Func<AnimationClock> _createHoldClock;
    private AnimationClock? _holdClock;

    public DeferredDisabledAppearanceBehavior() : this(static () =>
        new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop }.CreateClock())
    { }

    internal DeferredDisabledAppearanceBehavior(Func<AnimationClock> createHoldClock)
    {
        ArgumentNullException.ThrowIfNull(createHoldClock);
        _createHoldClock = createHoldClock;
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.IsEnabledChanged += OnIsEnabledChanged;
        AssociatedObject.IsVisibleChanged += OnIsVisibleChanged;
        AssociatedObject.Loaded += OnPresentationChanged;
        AssociatedObject.Unloaded += OnPresentationChanged;
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is { } button)
        {
            button.IsEnabledChanged -= OnIsEnabledChanged;
            button.IsVisibleChanged -= OnIsVisibleChanged;
            button.Loaded -= OnPresentationChanged;
            button.Unloaded -= OnPresentationChanged;
        }
        StopHold();
        base.OnDetaching();
    }

    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        StopHold();
        if (e.NewValue is not false || !AssociatedObject.IsVisible) return;

        _holdClock = _createHoldClock();
        AssociatedObject.ApplyAnimationClock(UIElement.OpacityProperty, _holdClock, HandoffBehavior.Compose);
        _holdClock.Controller?.Begin();
        _holdClock.Controller?.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) StopHold();
    }

    private void OnPresentationChanged(object sender, RoutedEventArgs e) => StopHold();

    private void StopHold()
    {
        if (_holdClock?.Controller is { } controller)
        {
            controller.SeekAlignedToLastTick(_holdClock.Timeline.Duration.TimeSpan + TimeSpan.FromTicks(1), TimeSeekOrigin.BeginTime);
            controller.Remove();
        }
        _holdClock = null;
    }
}
