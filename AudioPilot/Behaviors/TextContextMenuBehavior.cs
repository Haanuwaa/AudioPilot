using System.Windows;
using System.Windows.Controls.Primitives;

namespace AudioPilot.Behaviors;

/// <summary>Suppresses text-editor menus while preserving selection and keyboard copy commands.</summary>
public static class TextContextMenuBehavior
{
    public static readonly DependencyProperty IsDisabledProperty = DependencyProperty.RegisterAttached(
        "IsDisabled", typeof(bool), typeof(TextContextMenuBehavior), new PropertyMetadata(false, OnIsDisabledChanged));

    public static bool GetIsDisabled(DependencyObject element) => (bool)element.GetValue(IsDisabledProperty);

    public static void SetIsDisabled(DependencyObject element, bool value) => element.SetValue(IsDisabledProperty, value);

    private static void OnIsDisabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBoxBase textBox) return;
        // A template's null is not a local value; WPF's editor specifically checks ReadLocalValue.
        if ((bool)args.NewValue) textBox.ContextMenu = null;
        else if (textBox.ReadLocalValue(FrameworkElement.ContextMenuProperty) == null)
            textBox.ClearValue(FrameworkElement.ContextMenuProperty);
    }
}
