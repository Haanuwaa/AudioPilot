using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AudioPilot.Controls;

/// <summary>Keeps scrollbar thumbs at least 24 DIPs long when space permits, using WPF's native layout and drag calculations.</summary>
public sealed class AppScrollBarTrack : Track
{
    private const double MinimumThumbLength = 24;

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        double length = Orientation == Orientation.Vertical ? arrangeSize.Height : arrangeSize.Width;
        double minimum = length >= 2 ? Math.Min(MinimumThumbLength, length / 2) : MinimumThumbLength;
        ResourceKey key = Orientation == Orientation.Vertical
            ? SystemParameters.VerticalScrollBarButtonHeightKey
            : SystemParameters.HorizontalScrollBarButtonWidthKey;

        // Track uses half of the corresponding scroll-button metric as its minimum thumb length.
        double metric = minimum * 2;
        if (Resources[key] is not double current || current != metric) Resources[key] = metric;
        return base.ArrangeOverride(arrangeSize);
    }
}
