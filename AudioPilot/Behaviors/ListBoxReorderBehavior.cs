using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors;

/// <summary>Supports in-list mouse reordering without changing selection gestures or accepting external data.</summary>
public sealed class ListBoxReorderBehavior : Behavior<ListBox>
{
    public static readonly DependencyProperty ReorderCommandProperty = DependencyProperty.Register(
        nameof(ReorderCommand), typeof(ICommand), typeof(ListBoxReorderBehavior));

    private const string DragFormat = "AudioPilot.ListReorder";
    private object? _candidate;
    private Point _start;
    private bool _deferredSelection;
    private object[]? _dragItems;
    private object[]? _originalOrder;
    private Point _pointer;
    private ScrollViewer? _scrollViewer;
    private ScrollContentPresenter? _viewport;
    private DispatcherTimer? _scrollTimer;
    private int _scrollDirection;
    private DropIndicator? _indicator;
    private AdornerLayer? _adornerLayer;
    private bool _previousAllowDrop;

    public ICommand? ReorderCommand
    {
        get => (ICommand?)GetValue(ReorderCommandProperty);
        set => SetValue(ReorderCommandProperty, value);
    }

    protected override void OnAttached()
    {
        _previousAllowDrop = AssociatedObject.AllowDrop;
        AssociatedObject.SetCurrentValue(UIElement.AllowDropProperty, true);
        AssociatedObject.PreviewMouseLeftButtonDown += OnMouseDown;
        AssociatedObject.PreviewMouseLeftButtonUp += OnMouseUp;
        AssociatedObject.PreviewMouseMove += OnMouseMove;
        AssociatedObject.PreviewDragOver += OnDragOver;
        AssociatedObject.PreviewDrop += OnDrop;
        AssociatedObject.PreviewDragLeave += OnDragLeave;
        AssociatedObject.Unloaded += OnUnloaded;
    }

    protected override void OnDetaching()
    {
        Cleanup();
        AssociatedObject.PreviewMouseLeftButtonDown -= OnMouseDown;
        AssociatedObject.PreviewMouseLeftButtonUp -= OnMouseUp;
        AssociatedObject.PreviewMouseMove -= OnMouseMove;
        AssociatedObject.PreviewDragOver -= OnDragOver;
        AssociatedObject.PreviewDrop -= OnDrop;
        AssociatedObject.PreviewDragLeave -= OnDragLeave;
        AssociatedObject.Unloaded -= OnUnloaded;
        AssociatedObject.SetCurrentValue(UIElement.AllowDropProperty, _previousAllowDrop);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _candidate = null;
        _deferredSelection = false;
        if (e.ClickCount != 1 || Keyboard.Modifiers != ModifierKeys.None || IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (ItemsControl.ContainerFromElement(AssociatedObject, e.OriginalSource as DependencyObject) is not ListBoxItem container) return;
        _candidate = AssociatedObject.ItemContainerGenerator.ItemFromContainer(container);
        _start = e.GetPosition(AssociatedObject);
        _deferredSelection = container.IsSelected && AssociatedObject.SelectedItems.Count > 1;
        if (_deferredSelection) e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_candidate != null && _deferredSelection && _dragItems == null)
        {
            AssociatedObject.UnselectAll();
            AssociatedObject.SelectedItem = _candidate;
            AssociatedObject.Focus();
            e.Handled = true;
        }
        _candidate = null;
        _deferredSelection = false;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_candidate == null || _dragItems != null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _candidate = null; return; }
        Point point = e.GetPosition(AssociatedObject);
        if (Math.Abs(point.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!AssociatedObject.Items.Contains(_candidate) || ReorderCommand == null) { _candidate = null; return; }

        _originalOrder = [.. AssociatedObject.Items.Cast<object>()];
        _dragItems = AssociatedObject.SelectedItems.Contains(_candidate)
            ? [.. _originalOrder.Where(AssociatedObject.SelectedItems.Contains)] : [_candidate];
        _scrollViewer = FindDescendants<ScrollViewer>(AssociatedObject).FirstOrDefault();
        _viewport = _scrollViewer == null ? null : FindDescendants<ScrollContentPresenter>(_scrollViewer).FirstOrDefault();
        Mouse.Capture(null);
        e.Handled = true;
        try
        {
            DragDrop.DoDragDrop(AssociatedObject, new DataObject(DragFormat, _dragItems, false), DragDropEffects.Move);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Logger.Instance.Warning("ListReorder", "list-drag-unavailable", nameof(OnMouseMove), ex);
        }
        finally { Cleanup(); }
    }

    private bool IsOwnDrag(DragEventArgs e) => _dragItems != null && AssociatedObject.IsEnabled
        && (e.AllowedEffects & DragDropEffects.Move) != 0
        && e.Data.GetDataPresent(DragFormat, false) && ReferenceEquals(e.Data.GetData(DragFormat, false), _dragItems)
        && HasUnchangedOrder();

    private bool HasUnchangedOrder() => _originalOrder != null
        && AssociatedObject.Items.Cast<object>().SequenceEqual(_originalOrder, ReferenceEqualityComparer.Instance);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        _pointer = e.GetPosition(AssociatedObject);
        if (!IsOwnDrag(e) || IsInteractive(e.OriginalSource as DependencyObject)) { HideIndicator(); return; }
        int index = UpdateDropTarget(_pointer);
        if (index >= 0 && ReorderCommand?.CanExecute(new ListReorderRequest(_dragItems!, index)) == true) e.Effects = DragDropEffects.Move;
        else HideIndicator();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!IsOwnDrag(e) || IsInteractive(e.OriginalSource as DependencyObject)) return;
        _pointer = e.GetPosition(AssociatedObject);
        int index = UpdateDropTarget(_pointer);
        if (index < 0) return;
        var request = new ListReorderRequest(_dragItems!, index);
        if (ReorderCommand?.CanExecute(request) != true) return;
        ReorderCommand.Execute(request);
        AssociatedObject.SelectedItems.Clear();
        foreach (object item in request.Items)
        {
            if (AssociatedObject.Items.Contains(item)) AssociatedObject.SelectedItems.Add(item);
        }
        AssociatedObject.Focus();
        e.Effects = DragDropEffects.Move;
        HideIndicator();
    }

    internal int UpdateDropTarget(Point pointer)
    {
        _pointer = pointer;
        FrameworkElement viewport = _viewport ?? (FrameworkElement)AssociatedObject;
        Point origin = viewport.TranslatePoint(new Point(), AssociatedObject);
        var bounds = new Rect(origin, viewport.RenderSize);
        if (bounds.Width < 4 || bounds.Height < 2 || !bounds.Contains(_pointer)) { HideIndicator(); return -1; }
        int index = -1;
        double y = bounds.Top;
        foreach (ListBoxItem container in FindDescendants<ListBoxItem>(AssociatedObject))
        {
            Point top = container.TranslatePoint(new Point(), AssociatedObject);
            int itemIndex = AssociatedObject.ItemContainerGenerator.IndexFromContainer(container);
            if (itemIndex < 0) continue;
            if (_pointer.Y < top.Y + container.ActualHeight / 2)
            {
                index = itemIndex;
                y = top.Y;
                break;
            }
            index = itemIndex + 1;
            y = top.Y + container.ActualHeight;
        }
        if (index < 0) { HideIndicator(); return -1; }
        _adornerLayer ??= AdornerLayer.GetAdornerLayer(AssociatedObject);
        if (_indicator == null && _adornerLayer != null)
        {
            _indicator = new DropIndicator(AssociatedObject);
            _adornerLayer.Add(_indicator);
        }
        _indicator?.SetLine(bounds.Left + 2, bounds.Right - 2, Math.Clamp(y, bounds.Top + 1, bounds.Bottom - 1));
        _scrollDirection = _pointer.Y < bounds.Top + 20 ? -1 : _pointer.Y > bounds.Bottom - 20 ? 1 : 0;
        if (_scrollDirection != 0 && _scrollViewer != null)
        {
            _scrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Input, OnScroll, AssociatedObject.Dispatcher);
            _scrollTimer.Start();
        }
        else _scrollTimer?.Stop();
        return index;
    }

    private void OnScroll(object? sender, EventArgs e)
    {
        if (_dragItems == null || !HasUnchangedOrder() || !AssociatedObject.IsVisible) { HideIndicator(); return; }
        if (_scrollDirection < 0) _scrollViewer?.LineUp();
        else _scrollViewer?.LineDown();
        AssociatedObject.UpdateLayout();
        UpdateDropTarget(_pointer);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => HideIndicator();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Cleanup();

    private void HideIndicator()
    {
        _scrollTimer?.Stop();
        if (_indicator != null) _adornerLayer?.Remove(_indicator);
        _indicator = null;
    }

    private void Cleanup()
    {
        HideIndicator();
        _scrollTimer?.Tick -= OnScroll;
        _scrollTimer = null;
        _candidate = null;
        _deferredSelection = false;
        _dragItems = null;
        _originalOrder = null;
        _scrollViewer = null;
        _viewport = null;
        _adornerLayer = null;
    }

    internal static bool IsInteractive(DependencyObject? source)
    {
        for (DependencyObject? current = source; current != null && current is not ListBoxItem; current = Parent(current))
        {
            if (current is ButtonBase or TextBoxBase or Slider or ScrollBar or ComboBox or Hyperlink) return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject value) => value is Visual
        ? VisualTreeHelper.GetParent(value) ?? LogicalTreeHelper.GetParent(value)
        : value is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(value);

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            else foreach (T descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private sealed class DropIndicator(UIElement element) : Adorner(element)
    {
        private Point _from;
        private Point _to;

        internal void SetLine(double left, double right, double y)
        {
            IsHitTestVisible = false;
            _from = new Point(left, y);
            _to = new Point(right, y);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawLine(new Pen(SystemColors.HighlightBrush, 2), _from, _to);
    }
}
