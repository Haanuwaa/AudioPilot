using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AudioPilot.Behaviors
{
    public static class DeferredTabContentBehavior
    {
        private static readonly RoutedEventHandler SelectedEventHandler = OnTabItemSelected;

        public static readonly DependencyProperty TemplateProperty =
            DependencyProperty.RegisterAttached(
                "Template",
                typeof(DataTemplate),
                typeof(DeferredTabContentBehavior),
                new PropertyMetadata(null, OnTemplateChanged));

        public static void SetTemplate(DependencyObject element, DataTemplate? value)
        {
            ArgumentNullException.ThrowIfNull(element);
            element.SetValue(TemplateProperty, value);
        }

        public static DataTemplate? GetTemplate(DependencyObject element)
        {
            ArgumentNullException.ThrowIfNull(element);
            return (DataTemplate?)element.GetValue(TemplateProperty);
        }

        internal static bool EnsureContentLoaded(TabItem tabItem)
        {
            ArgumentNullException.ThrowIfNull(tabItem);

            DataTemplate? template = GetTemplate(tabItem);
            if (template == null)
            {
                return false;
            }

            if (tabItem.Content == null)
            {
                object content = template.LoadContent();
                tabItem.SetCurrentValue(ContentControl.ContentProperty, content);
            }

            tabItem.ClearValue(TemplateProperty);
            return true;
        }

        private static void OnTemplateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is not TabItem tabItem)
            {
                return;
            }

            if (args.OldValue is DataTemplate)
            {
                tabItem.RemoveHandler(Selector.SelectedEvent, SelectedEventHandler);
            }

            if (args.NewValue is not DataTemplate)
            {
                return;
            }

            tabItem.AddHandler(Selector.SelectedEvent, SelectedEventHandler);
            if (tabItem.IsSelected)
            {
                EnsureContentLoaded(tabItem);
            }
        }

        private static void OnTabItemSelected(object sender, RoutedEventArgs args)
        {
            if (sender is TabItem tabItem)
            {
                EnsureContentLoaded(tabItem);
            }
        }
    }
}
