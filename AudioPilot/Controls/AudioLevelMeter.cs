using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioPilot.Controls;

public sealed class AudioLevelMeter : Control
{
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
        Pen? borderPen = BorderBrush == null || borderWidth <= 0 ? null : new Pen(BorderBrush, borderWidth);
        drawingContext.DrawRoundedRectangle(Background, borderPen, surface, radius, radius);

        double inset = borderWidth + 3d;
        var content = new Rect(inset, inset, Math.Max(0, ActualWidth - (2d * inset)), Math.Max(0, ActualHeight - (2d * inset)));
        if (content.Width <= 0 || content.Height <= 0)
            return;

        int segmentCount = Math.Max(1, Math.Min(SegmentCount, (int)Math.Floor((content.Width + SegmentGap) / (4d + SegmentGap))));
        double segmentWidth = (content.Width - (SegmentGap * (segmentCount - 1))) / segmentCount;
        double activeSegments = (Level / 100d) * segmentCount;
        double segmentRadius = Math.Min(1.5d, Math.Min(segmentWidth, content.Height) / 2d);

        for (int index = 0; index < segmentCount; index++)
        {
            double left = content.Left + (index * (segmentWidth + SegmentGap));
            var segment = new Rect(left, content.Top, segmentWidth, content.Height);
            if (InactiveBrush != null)
            {
                drawingContext.DrawRoundedRectangle(InactiveBrush, null, segment, segmentRadius, segmentRadius);
            }

            double activeFraction = Math.Clamp(activeSegments - index, 0d, 1d);
            if (activeFraction > 0)
            {
                double position = (index + 1d) / segmentCount;
                Brush? activeBrush = ResolveActiveBrush(position);
                if (activeBrush != null)
                {
                    double activeWidth = segmentWidth * activeFraction;
                    double activeRadius = Math.Min(segmentRadius, activeWidth / 2d);
                    drawingContext.DrawRoundedRectangle(
                        activeBrush,
                        null,
                        new Rect(left, content.Top, activeWidth, content.Height),
                        activeRadius,
                        activeRadius);
                }
            }
        }

        if (Peak > 0 && PeakBrush != null)
        {
            double peakX = content.Left + ((Peak / 100d) * content.Width);
            peakX = Math.Clamp(peakX, content.Left + 1d, content.Right - 1d);
            drawingContext.DrawLine(new Pen(PeakBrush, 2d), new Point(peakX, content.Top - 1d), new Point(peakX, content.Bottom + 1d));
        }
    }

    private Brush? ResolveActiveBrush(double position) => position switch
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

    public void SetValue(double value) => throw new InvalidOperationException("The microphone level meter is read-only.");

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

    protected override string GetClassNameCore() => nameof(AudioLevelMeter);
}
