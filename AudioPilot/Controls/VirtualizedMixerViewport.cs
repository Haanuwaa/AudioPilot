using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioPilot.Controls;

/// <summary>Reports the full mixer height to the page while realizing only its visible rows.</summary>
public sealed class VirtualizedMixerViewport : Decorator
{
    private const int NaturalLayoutLimit = 16;
    private ScrollViewer? _bodyViewer;
    private VirtualizingStackPanel? _bodyInfo;
    private bool _virtualized;
    private double _contentHeight;
    private double _requestedBodyOffset;
    private bool _requestingPage;
    private bool _pageOffsetDirty = true;
    private double _lastContentTop = double.NaN;
    private ScrollViewer? _subscribedOwner;

    public static readonly DependencyProperty ScrollOwnerProperty = DependencyProperty.Register(nameof(ScrollOwner), typeof(ScrollViewer), typeof(VirtualizedMixerViewport),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, static (element, _) => ((VirtualizedMixerViewport)element).AttachOwner()));
    public ScrollViewer? ScrollOwner { get => (ScrollViewer?)GetValue(ScrollOwnerProperty); set => SetValue(ScrollOwnerProperty, value); }

    public VirtualizedMixerViewport()
    {
        Loaded += (_, _) => AttachOwner();
        Unloaded += (_, _) => { DetachOwner(); _bodyViewer = null; _bodyInfo = null; };
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(BodyScrolled));
        AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(BringBodyIntoView));
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is ItemsControl oldItems) ((INotifyCollectionChanged)oldItems.Items).CollectionChanged -= ItemsChanged;
        if (visualAdded is ItemsControl newItems) ((INotifyCollectionChanged)newItems.Items).CollectionChanged += ItemsChanged;
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _pageOffsetDirty = true;
        InvalidateMeasure();
    }

    private void AttachOwner()
    {
        if (ReferenceEquals(_subscribedOwner, ScrollOwner)) return;
        DetachOwner();
        _pageOffsetDirty = true;
        _subscribedOwner = ScrollOwner;
        _subscribedOwner?.ScrollChanged += PageScrolled;
    }

    private void DetachOwner()
    {
        _subscribedOwner?.ScrollChanged -= PageScrolled;
        _subscribedOwner = null;
    }

    private void PageScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ScrollOwner)) return;
        _requestingPage = false;
        _pageOffsetDirty |= e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0;
        if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0) InvalidateMeasure();
        if (e.VerticalChange != 0) { InvalidateMeasure(); InvalidateArrange(); }
    }

    private double ContentTop()
    {
        if (Parent is not StackPanel panel) return 0;
        // Siblings may have been measured but not arranged yet when the page changes tabs.
        double top = 0;
        foreach (UIElement child in panel.Children)
        {
            if (ReferenceEquals(child, this)) break;
            top += child.DesiredSize.Height;
        }
        return top;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null) return default;
        _virtualized = Child is ItemsControl { Items.Count: > NaturalLayoutLimit } && ScrollOwner != null;
        double height = _virtualized ? Math.Max(0, ScrollOwner!.ViewportHeight) : double.PositiveInfinity;
        LocateScrollParts();
        bool hadScrollInfo = _bodyInfo != null;
        double contentTop = ContentTop();
        if (_pageOffsetDirty || !hadScrollInfo || contentTop != _lastContentTop) SyncBodyOffset();
        _lastContentTop = contentTop;
        _pageOffsetDirty = false;
        Child.Measure(new Size(constraint.Width, height));
        LocateScrollParts();
        // Preserve native offset corrections for variable-height rows; BodyScrolled forwards them to the page.
        if (!hadScrollInfo && SyncBodyOffset()) Child.Measure(new Size(constraint.Width, height));
        _contentHeight = _virtualized ? Math.Max(Child.DesiredSize.Height, _bodyInfo?.ExtentHeight ?? _bodyViewer?.ExtentHeight ?? 0) : Child.DesiredSize.Height;
        return new Size(Child.DesiredSize.Width, _contentHeight);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child == null) return arrangeSize;
        double height = _virtualized ? Math.Max(0, ScrollOwner!.ViewportHeight) : arrangeSize.Height;
        double top = _virtualized ? Math.Clamp(ScrollOwner!.VerticalOffset - ContentTop(), 0, Math.Max(0, _contentHeight - height)) : 0;
        Child.Arrange(new Rect(0, top, arrangeSize.Width, height));
        return arrangeSize;
    }

    private bool SyncBodyOffset()
    {
        if (_bodyInfo == null) return false;
        double offset = _virtualized ? Math.Clamp(ScrollOwner!.VerticalOffset - ContentTop(), 0, Math.Max(0, _bodyInfo.ExtentHeight - _bodyInfo.ViewportHeight)) : 0;
        _requestedBodyOffset = offset;
        if (Math.Abs(_bodyInfo.VerticalOffset - offset) < .5) return false;
        _bodyInfo.SetVerticalOffset(offset);
        return true;
    }

    private void LocateScrollParts()
    {
        if (Child is not ListBox list) return;
        list.ApplyTemplate();
        if (_bodyViewer != null && list.IsAncestorOf(_bodyViewer) && _bodyInfo != null && list.IsAncestorOf(_bodyInfo)) return;
        _bodyViewer = null;
        _bodyInfo = null;
        Visit(list);
        void Visit(DependencyObject current)
        {
            if (current is ScrollViewer viewer && ReferenceEquals(viewer.TemplatedParent, list)) _bodyViewer = viewer;
            if (current is VirtualizingStackPanel panel && ReferenceEquals(panel.ScrollOwner, _bodyViewer)) _bodyInfo = panel;
            if (_bodyInfo != null) return;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                Visit(VisualTreeHelper.GetChild(current, index));
                if (_bodyInfo != null) return;
            }
        }
    }

    private void BodyScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (Child is not ListBox list || e.OriginalSource is not ScrollViewer viewer || !ReferenceEquals(viewer.TemplatedParent, list)) return;
        _bodyViewer = viewer;
        if (!_virtualized) return;
        if (e.ExtentHeightChange != 0) InvalidateMeasure();
        if (!_requestingPage && e.VerticalChange != 0 && Math.Abs(viewer.VerticalOffset - _requestedBodyOffset) > .5)
            RequestPageOffset(ContentTop() + viewer.VerticalOffset);
    }

    private void RequestPageOffset(double offset)
    {
        if (ScrollOwner == null) return;
        offset = Math.Clamp(offset, 0, ScrollOwner.ScrollableHeight);
        if (Math.Abs(ScrollOwner.VerticalOffset - offset) < .5) return;
        _requestingPage = true;
        ScrollOwner.ScrollToVerticalOffset(offset);
    }

    private void BringBodyIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (!_virtualized || ScrollOwner == null || Child == null || e.TargetObject is not Visual visual || !Child.IsAncestorOf(visual)) return;
        var rect = e.TargetRect.IsEmpty && visual is FrameworkElement element ? new Rect(element.RenderSize) : e.TargetRect;
        Rect visible = visual.TransformToAncestor(ScrollOwner).TransformBounds(rect);
        e.Handled = true;
        if (visible.Top >= 0 && visible.Bottom <= ScrollOwner.ViewportHeight) return;
        double bodyOffset = _bodyInfo?.VerticalOffset ?? _bodyViewer?.VerticalOffset ?? 0;
        RequestPageOffset(ContentTop() + bodyOffset);
    }
}
