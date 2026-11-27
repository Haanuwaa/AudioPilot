using System.Windows;
using System.Windows.Controls;
using AudioPilot.Localization;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors;

/// <summary>Updates shortcut guidance with capture focus and hides it while the context menu is open.</summary>
public sealed class HotkeyPlaceholderBehavior : Behavior<TextBox>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.ContextMenuOpening += OnContextMenuOpening;
        AssociatedObject.ContextMenuClosing += OnContextMenuClosing;
        AssociatedObject.GotKeyboardFocus += OnGotKeyboardFocus;
        AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
        AssociatedObject.IsVisibleChanged += OnIsVisibleChanged;
        AssociatedObject.Unloaded += OnUnloaded;
        AssociatedObject.Loaded += OnLoaded;
        RestorePlaceholderState();
    }

    protected override void OnDetaching()
    {
        AssociatedObject.ContextMenuOpening -= OnContextMenuOpening;
        AssociatedObject.ContextMenuClosing -= OnContextMenuClosing;
        AssociatedObject.GotKeyboardFocus -= OnGotKeyboardFocus;
        AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;
        AssociatedObject.IsVisibleChanged -= OnIsVisibleChanged;
        AssociatedObject.Unloaded -= OnUnloaded;
        AssociatedObject.Loaded -= OnLoaded;
        RestorePlaceholderState(false);
        base.OnDetaching();
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        TextBoxPlaceholderBehavior.SetIsContextMenuOpen(AssociatedObject, true);

    private void OnContextMenuClosing(object sender, ContextMenuEventArgs e) => RestorePlaceholderState();

    private void OnGotKeyboardFocus(object sender, RoutedEventArgs e) => RestorePlaceholderState(true);

    private void OnLostKeyboardFocus(object sender, RoutedEventArgs e)
    {
        if (AssociatedObject.ContextMenu?.IsOpen != true) RestorePlaceholderState(false);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) RestorePlaceholderState(false);
        else RestorePlaceholderState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        RestorePlaceholderState(false);

    private void OnLoaded(object sender, RoutedEventArgs e) => RestorePlaceholderState();

    private void RestorePlaceholderState(bool? isCapturing = null)
    {
        bool capturing = isCapturing ?? AssociatedObject.IsKeyboardFocusWithin;
        TextBoxPlaceholderBehavior.SetIsContextMenuOpen(AssociatedObject, false);
        TextBoxPlaceholderBehavior.SetShowWhileFocused(AssociatedObject, capturing);
        LocalizationContext.SetText(AssociatedObject, TextBoxPlaceholderBehavior.TextProperty,
            new(capturing ? "Hotkey_CapturePrompt" : "Hotkey_SetPrompt"));
    }
}
