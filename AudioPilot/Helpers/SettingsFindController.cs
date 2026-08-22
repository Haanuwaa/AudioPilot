using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Helpers;

/// <summary>Finds settings by their labels and explanations without hiding controls or changing their values.</summary>
internal sealed class SettingsFindController(FrameworkElement scope, ScrollViewer viewer) : IDisposable
{
    private readonly Dictionary<Expander, bool> _expansion = [];
    private FrameworkElement[] _matches = [];
    private int _index = -1;
    private AdornerLayer? _layer;
    private MatchAdorner? _adorner;
    private DispatcherOperation? _pending;
    private bool _disposed;

    internal string Status => _matches.Length == 0 ? "No matches" : $"{_index + 1} of {_matches.Length}";
    internal bool HasMatches => _matches.Length > 0;
    internal FrameworkElement? CurrentMatch => _index >= 0 && _index < _matches.Length ? _matches[_index] : null;

    internal void Search(string query, bool revealMatch = true)
    {
        Clear();
        if (_disposed || string.IsNullOrWhiteSpace(query)) return;
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _matches = [.. Descendants(scope).OfType<FrameworkElement>().Where(IsCandidate)
            .Where(element => words.All(word => GetSearchText(element).Contains(word, StringComparison.OrdinalIgnoreCase)))];
        if (_matches.Length > 0)
        {
            _index = 0;
            if (revealMatch) Reveal();
        }
    }

    internal void Move(int direction)
    {
        if (_disposed || _matches.Length == 0) return;
        _index = (_index + direction + _matches.Length) % _matches.Length;
        Reveal();
    }

    internal void Clear()
    {
        DismissHighlight();
        foreach (var (expander, expanded) in _expansion) expander.SetCurrentValue(Expander.IsExpandedProperty, expanded);
        _expansion.Clear();
        _matches = [];
        _index = -1;
    }

    public void Dispose() { if (_disposed) return; Clear(); _disposed = true; }

    internal void DismissHighlight()
    {
        _pending?.Abort();
        _pending = null;
        RemoveAdorner();
    }

    private void Reveal()
    {
        DismissHighlight();
        if (CurrentMatch is not { } target || scope.Dispatcher.HasShutdownStarted) return;
        for (DependencyObject? parent = target; parent != null && !ReferenceEquals(parent, scope); parent = LogicalTreeHelper.GetParent(parent))
        {
            if (parent is not Expander expander) continue;
            _expansion.TryAdd(expander, expander.IsExpanded);
            expander.SetCurrentValue(Expander.IsExpandedProperty, true);
        }
        _pending = scope.Dispatcher.BeginInvoke(() =>
        {
            _pending = null;
            if (_disposed || !ReferenceEquals(CurrentMatch, target)) return;
            viewer.UpdateLayout();
            if (viewer.Content is Visual content && content.IsAncestorOf(target)
                && double.IsFinite(viewer.ViewportHeight) && viewer.ViewportHeight > 0)
            {
                Rect bounds = target.TransformToAncestor(content).TransformBounds(new Rect(target.RenderSize));
                double offset = bounds.Top + bounds.Height / 2 - viewer.ViewportHeight / 2;
                viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));
            }
            _layer = AdornerLayer.GetAdornerLayer(target);
            if (_layer != null) { _adorner = new MatchAdorner(target); _layer.Add(_adorner); }
        }, DispatcherPriority.Loaded);
    }

    private void RemoveAdorner()
    {
        if (_adorner != null) _layer?.Remove(_adorner);
        _adorner = null;
        _layer = null;
    }

    private static bool IsCandidate(FrameworkElement element) => element is Button or CheckBox or ComboBox or TextBox
        || element is TextBlock { FontWeight: var weight, Text.Length: > 0 } && weight >= FontWeights.SemiBold;

    private static string GetSearchText(FrameworkElement element)
    {
        var parts = new List<string>
        {
            AutomationProperties.GetName(element), AutomationProperties.GetHelpText(element),
        };
        if (element is ContentControl { Content: string content }) parts.Add(content);
        if (element is TextBlock text) parts.Add(text.Text);
        foreach (var help in Interaction.GetBehaviors(element).OfType<HoverInfoPopupBehavior>()) parts.Add(help.Text ?? string.Empty);
        if (element.Parent is Panel panel)
            foreach (Label label in panel.Children.OfType<Label>())
                if (label.Content is string caption) parts.Add(caption);
        return string.Join(' ', parts);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependency) continue;
            if (dependency is UIElement { Visibility: not Visibility.Visible }) continue;
            yield return dependency;
            foreach (var descendant in Descendants(dependency)) yield return descendant;
        }
    }

    private sealed class MatchAdorner(FrameworkElement target) : Adorner(target)
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            var bounds = new Rect(AdornedElement.RenderSize);
            bounds.Inflate(3, 3);
            var brush = target.TryFindResource("AccentTextBrush") as Brush ?? SystemColors.HighlightBrush;
            drawingContext.DrawRoundedRectangle(null, new Pen(brush, 2), bounds, 4, 4);
        }

        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }
}
