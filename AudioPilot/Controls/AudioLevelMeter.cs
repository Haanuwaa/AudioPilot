using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioPilot.Controls;

public sealed class AudioLevelMeter : Control
{
    private DrawingGroup? _inactiveSegments;
    private (Rect Content, int Count, double Gap, Brush? Brush) _inactiveSegmentsKey;
    private Brush?[]? _segmentBrushes;
    private (int Count, Brush? Low, Brush? Mid, Brush? High, bool HighContrast) _segmentBrushesKey;
    private Brush? _cachedPeakLedBrush;
    private (Brush? Peak, bool HighContrast) _cachedPeakLedBrushKey;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(AudioLevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, static (_, value) => CoercePercentage((double)value)));

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak),
        typeof(double),
        typeof(AudioLevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, static (_, value) => CoercePercentage((double)value)));

    public static readonly DependencyProperty InactiveBrushProperty = RegisterBrush(nameof(InactiveBrush));
    public static readonly DependencyProperty LowLevelBrushProperty = RegisterBrush(nameof(LowLevelBrush));
    public static readonly DependencyProperty MidLevelBrushProperty = RegisterBrush(nameof(MidLevelBrush));
    public static readonly DependencyProperty HighLevelBrushProperty = RegisterBrush(nameof(HighLevelBrush));
    public static readonly DependencyProperty PeakBrushProperty = RegisterBrush(nameof(PeakBrush));

    public static readonly DependencyProperty SegmentCountProperty = DependencyProperty.Register(
        nameof(SegmentCount),
        typeof(int),
        typeof(AudioLevelMeter),
        new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender, null, static (_, value) => CoerceSegmentCount((int)value)));

    public static readonly DependencyProperty SegmentGapProperty = DependencyProperty.Register(
        nameof(SegmentGap),
        typeof(double),
        typeof(AudioLevelMeter),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender, null, static (_, value) => CoerceNonNegativeFinite((double)value)));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(double),
        typeof(AudioLevelMeter),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsRender, null, static (_, value) => CoerceNonNegativeFinite((double)value)));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public Brush? InactiveBrush
    {
        get => (Brush?)GetValue(InactiveBrushProperty);
        set => SetValue(InactiveBrushProperty, value);
    }

    public Brush? LowLevelBrush
    {
        get => (Brush?)GetValue(LowLevelBrushProperty);
        set => SetValue(LowLevelBrushProperty, value);
    }

    public Brush? MidLevelBrush
    {
        get => (Brush?)GetValue(MidLevelBrushProperty);
        set => SetValue(MidLevelBrushProperty, value);
    }

    public Brush? HighLevelBrush
    {
        get => (Brush?)GetValue(HighLevelBrushProperty);
        set => SetValue(HighLevelBrushProperty, value);
    }

    public Brush? PeakBrush
    {
        get => (Brush?)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    public double SegmentGap
    {
        get => (double)GetValue(SegmentGapProperty);
        set => SetValue(SegmentGapProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new AudioLevelMeterAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        double borderWidth = Math.Max(
            Math.Max(BorderThickness.Left, BorderThickness.Right),
            Math.Max(BorderThickness.Top, BorderThickness.Bottom));
        double radius = Math.Min(CornerRadius, Math.Min(ActualWidth, ActualHeight) / 2d);
        var surface = new Rect(0.5d * borderWidth, 0.5d * borderWidth, Math.Max(0, ActualWidth - borderWidth), Math.Max(0, ActualHeight - borderWidth));
        Pen? borderPen = GetBorderPen(BorderBrush, borderWidth);
        drawingContext.DrawRoundedRectangle(Background, borderPen, surface, radius, radius);

        double inset = borderWidth > 0 ? borderWidth + 2.5d : 1.5d;
        var content = new Rect(inset, inset, Math.Max(0, ActualWidth - (2d * inset)), Math.Max(0, ActualHeight - (2d * inset)));
        if (content.Width <= 0 || content.Height <= 0)
            return;

        double gap = SegmentGap;
        int segmentCount = Math.Max(1, Math.Min(SegmentCount, (int)Math.Floor((content.Width + gap) / (4d + gap))));
        double segmentWidth = (content.Width - (gap * (segmentCount - 1))) / segmentCount;
        double activeSegments = (Level / 100d) * segmentCount;
        double segmentRadius = Math.Min(2d, Math.Min(segmentWidth, content.Height) / 2d);
        bool highContrast = SystemParameters.HighContrast;

        Brush? inactive = InactiveBrush;
        // Touching segments can share antialiased edge pixels. Preserve their original paint order.
        bool batchInactive = gap >= 1d;
        if (batchInactive && inactive != null)
            DrawInactiveSegments(drawingContext, content, segmentCount, segmentWidth, segmentRadius, gap, inactive);
        else
        {
            _inactiveSegments = null;
            _inactiveSegmentsKey = default;
        }

        Brush?[] activeBrushes = GetSegmentBrushes(segmentCount, highContrast);
        for (int index = 0; index < segmentCount; index++)
        {
            double left = content.Left + (index * (segmentWidth + gap));
            var cell = new Rect(left, content.Top, segmentWidth, content.Height);
            if (!batchInactive && inactive != null)
                drawingContext.DrawRoundedRectangle(inactive, null, cell, segmentRadius, segmentRadius);

            double activeFraction = Math.Clamp(activeSegments - index, 0d, 1d);
            if (activeFraction > 0)
            {
                Brush? activeBrush = activeBrushes[index];
                if (activeBrush != null)
                {
                    if (activeFraction >= 0.999d)
                    {
                        drawingContext.DrawRoundedRectangle(activeBrush, null, cell, segmentRadius, segmentRadius);
                    }
                    else
                    {
                        drawingContext.PushOpacity(activeFraction);
                        drawingContext.DrawRoundedRectangle(activeBrush, null, cell, segmentRadius, segmentRadius);
                        drawingContext.Pop();
                    }
                }
            }
        }

        if (Peak > 0 && PeakBrush != null)
        {
            int peakIndex = Math.Clamp((int)Math.Ceiling((Peak / 100d) * segmentCount) - 1, 0, segmentCount - 1);
            double peakLeft = content.Left + (peakIndex * (segmentWidth + gap));
            var peakCell = new Rect(peakLeft, content.Top, segmentWidth, content.Height);
            Brush peakBrush = GetPeakLedBrush(PeakBrush, highContrast);
            drawingContext.DrawRoundedRectangle(peakBrush, null, peakCell, segmentRadius, segmentRadius);
        }
    }

    private Pen? _cachedBorderPen;
    private (Brush? Brush, double Thickness) _cachedBorderPenKey;

    private Pen? GetBorderPen(Brush? brush, double thickness)
    {
        if (brush == null || thickness <= 0)
        {
            _cachedBorderPen = null;
            _cachedBorderPenKey = default;
            return null;
        }

        var key = (brush, thickness);
        if (_cachedBorderPen == null || _cachedBorderPenKey != key)
        {
            var pen = new Pen(brush, thickness);
            if (pen.CanFreeze) pen.Freeze();
            _cachedBorderPen = pen;
            _cachedBorderPenKey = key;
        }

        return _cachedBorderPen;
    }

    private Brush GetPeakLedBrush(Brush brush, bool highContrast)
    {
        var key = (brush, highContrast);
        if (_cachedPeakLedBrush != null && _cachedPeakLedBrushKey == key)
            return _cachedPeakLedBrush;

        Brush resolved = !highContrast && brush is SolidColorBrush solid
            ? CreateLedSheenBrush(solid.Color, highlightStrength: 0.12d, shadowStrength: 0.14d)
            : brush;
        _cachedPeakLedBrush = resolved;
        _cachedPeakLedBrushKey = key;
        return resolved;
    }

    private Brush?[] GetSegmentBrushes(int segmentCount, bool highContrast)
    {
        var key = (segmentCount, LowLevelBrush, MidLevelBrush, HighLevelBrush, highContrast);
        if (_segmentBrushes != null && _segmentBrushesKey == key)
            return _segmentBrushes;

        var brushes = new Brush?[segmentCount];
        if (!highContrast
            && LowLevelBrush is SolidColorBrush lowSolid
            && MidLevelBrush is SolidColorBrush midSolid
            && HighLevelBrush is SolidColorBrush highSolid)
        {
            Color low = lowSolid.Color;
            Color mid = midSolid.Color;
            Color high = highSolid.Color;
            for (int index = 0; index < segmentCount; index++)
            {
                double position = (index + 0.5d) / segmentCount;
                Color color = InterpolateZoneColor(position, low, mid, high);
                brushes[index] = CreateLedSheenBrush(color, highlightStrength: 0.16d, shadowStrength: 0.12d);
            }
        }
        else
        {
            for (int index = 0; index < segmentCount; index++)
            {
                double position = (index + 1d) / segmentCount;
                Brush? baseBrush = ResolveDiscreteBrush(position);
                brushes[index] = !highContrast && baseBrush is SolidColorBrush solid
                    ? CreateLedSheenBrush(solid.Color, highlightStrength: 0.16d, shadowStrength: 0.12d)
                    : baseBrush;
            }
        }

        _segmentBrushes = brushes;
        _segmentBrushesKey = key;
        return brushes;
    }

    private static Color InterpolateZoneColor(double position, Color low, Color mid, Color high)
    {
        if (position <= 0.52d) return low;
        if (position < 0.74d) return LerpColor(low, mid, SmoothStep((position - 0.52d) / 0.22d));
        if (position <= 0.82d) return mid;
        if (position < 0.94d) return LerpColor(mid, high, SmoothStep((position - 0.82d) / 0.12d));
        return high;
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0d, 1d);
        return t * t * (3d - (2d * t));
    }

    private static Color LerpColor(Color from, Color to, double t) => Color.FromArgb(
        (byte)Math.Round(from.A + ((to.A - from.A) * t)),
        (byte)Math.Round(from.R + ((to.R - from.R) * t)),
        (byte)Math.Round(from.G + ((to.G - from.G) * t)),
        (byte)Math.Round(from.B + ((to.B - from.B) * t)));

    private static LinearGradientBrush CreateLedSheenBrush(Color baseColor, double highlightStrength, double shadowStrength)
    {
        Color top = LerpColor(baseColor, Color.FromArgb(baseColor.A, 255, 255, 255), highlightStrength);
        Color bottom = LerpColor(baseColor, Color.FromArgb(baseColor.A, 0, 0, 0), shadowStrength);
        var gradient = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        gradient.Freeze();
        return gradient;
    }

    private void DrawInactiveSegments(DrawingContext context, Rect content, int count, double width, double radius, double gap, Brush brush)
    {
        var key = (content, count, gap, brush);
        if (_inactiveSegments == null || _inactiveSegmentsKey != key)
        {
            var drawing = new DrawingGroup();
            using (DrawingContext layer = drawing.Open())
            {
                for (int index = 0; index < count; index++)
                {
                    double left = content.Left + (index * (width + gap));
                    layer.DrawRoundedRectangle(brush, null, new Rect(left, content.Top, width, content.Height), radius, radius);
                }
            }
            if (brush.IsFrozen) drawing.Freeze();
            _inactiveSegments = drawing;
            _inactiveSegmentsKey = key;
        }
        context.DrawDrawing(_inactiveSegments);
    }

    private Brush? ResolveDiscreteBrush(double position) => position switch
    {
        <= 0.65d => LowLevelBrush,
        <= 0.85d => MidLevelBrush,
        _ => HighLevelBrush,
    };

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name,
        typeof(Brush),
        typeof(AudioLevelMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static double CoercePercentage(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0d;

    private static int CoerceSegmentCount(int value) => Math.Clamp(value, 4, 64);

    private static double CoerceNonNegativeFinite(double value) =>
        double.IsFinite(value) ? Math.Max(0d, value) : 0d;
}

internal sealed class AudioLevelMeterAutomationPeer(AudioLevelMeter owner)
    : FrameworkElementAutomationPeer(owner), IRangeValueProvider
{
    private AudioLevelMeter Meter => (AudioLevelMeter)Owner;

    public bool IsReadOnly => true;
    public double LargeChange => double.NaN;
    public double Maximum => 100d;
    public double Minimum => 0d;
    public double SmallChange => double.NaN;
    public double Value => Meter.Level;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

    public void SetValue(double value) => throw new InvalidOperationException("The audio level meter is read-only.");

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

    protected override string GetClassNameCore() => nameof(AudioLevelMeter);
}
