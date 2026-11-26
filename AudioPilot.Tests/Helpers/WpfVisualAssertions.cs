using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AudioPilot.Tests.Helpers;

internal static class WpfVisualAssertions
{
    internal static void NoBindingErrors(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var errors = new List<string>();
        int bindingCount = 0;
        pending.Push(root);
        while (pending.TryPop(out DependencyObject? element))
        {
            LocalValueEnumerator values = element.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                LocalValueEntry entry = values.Current;
                if (entry.Value is not BindingExpressionBase binding) continue;
                bindingCount++;
                if (binding.Status is BindingStatus.PathError or BindingStatus.UpdateSourceError or BindingStatus.UpdateTargetError)
                    errors.Add($"{element.GetType().Name} '{(element as FrameworkElement)?.Name}'.{entry.Property.Name}: {binding.Status}");
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                pending.Push(VisualTreeHelper.GetChild(element, index));
        }
        Assert.True(bindingCount > 0, "The rendered visual tree must contain bindings to validate.");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
