using System.Windows;

namespace AudioPilot.Behaviors;

public static class TextBoxPlaceholderBehavior
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(TextBoxPlaceholderBehavior), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsContextMenuOpenProperty = DependencyProperty.RegisterAttached(
        "IsContextMenuOpen", typeof(bool), typeof(TextBoxPlaceholderBehavior), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty ShowWhileFocusedProperty = DependencyProperty.RegisterAttached(
        "ShowWhileFocused", typeof(bool), typeof(TextBoxPlaceholderBehavior), new FrameworkPropertyMetadata(false));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);

    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    public static bool GetIsContextMenuOpen(DependencyObject obj) => (bool)obj.GetValue(IsContextMenuOpenProperty);

    public static void SetIsContextMenuOpen(DependencyObject obj, bool value) => obj.SetValue(IsContextMenuOpenProperty, value);

    public static bool GetShowWhileFocused(DependencyObject obj) => (bool)obj.GetValue(ShowWhileFocusedProperty);

    public static void SetShowWhileFocused(DependencyObject obj, bool value) => obj.SetValue(ShowWhileFocusedProperty, value);
}
