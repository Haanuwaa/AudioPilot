using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioPilot.Localization;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors;

/// <summary>Restores the viewport after localized content changes height.</summary>
public sealed class KeepScrollOnLanguageChangeBehavior : Behavior<ScrollViewer>
{
    private LocalizationService? _localization;
    private DispatcherOperation? _restore;

    protected override void OnAttached()
    {
        AssociatedObject.Loaded += AttachLanguage;
        AssociatedObject.Unloaded += DetachLanguage;
        if (AssociatedObject.IsLoaded) AttachLanguage(AssociatedObject, new RoutedEventArgs());
    }

    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= AttachLanguage;
        AssociatedObject.Unloaded -= DetachLanguage;
        DetachLanguage(AssociatedObject, new RoutedEventArgs());
    }

    private void AttachLanguage(object sender, RoutedEventArgs args)
    {
        if (_localization != null) return;
        _localization = LocalizationContext.GetService(AssociatedObject);
        PropertyChangedEventManager.AddHandler(_localization, OnLanguageChanged, nameof(LocalizationService.Revision));
    }

    private void DetachLanguage(object sender, RoutedEventArgs args)
    {
        _restore?.Abort();
        _restore = null;
        if (_localization == null) return;
        PropertyChangedEventManager.RemoveHandler(_localization, OnLanguageChanged, nameof(LocalizationService.Revision));
        _localization = null;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        var viewer = AssociatedObject;
        if (viewer == null || viewer.Dispatcher.HasShutdownStarted || _restore?.Status == DispatcherOperationStatus.Pending) return;
        double offset = viewer.VerticalOffset;
        bool atBottom = viewer.ScrollableHeight > 0 && offset >= viewer.ScrollableHeight - 1;
        _restore = viewer.Dispatcher.BeginInvoke(() =>
        {
            _restore = null;
            if (!ReferenceEquals(AssociatedObject, viewer) || !viewer.IsLoaded) return;
            viewer.UpdateLayout();
            if (atBottom) viewer.ScrollToBottom();
            else viewer.ScrollToVerticalOffset(Math.Min(offset, viewer.ScrollableHeight));
        }, DispatcherPriority.Loaded);
    }
}
