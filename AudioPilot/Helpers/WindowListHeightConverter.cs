using System.Globalization;
using System.Windows.Data;

namespace AudioPilot.Helpers;

public sealed class WindowListHeightConverter : IValueConverter
{
    public double InitialHeight { get; set; } = 104;
    public double InitialWindowHeight { get; set; } = 378;
    public double RowHeight { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double height || !double.IsFinite(height)) return InitialHeight;
        double desired = Math.Clamp(InitialHeight + (height - InitialWindowHeight) / 2, InitialHeight, 260);
        return RowHeight > 0 && double.IsFinite(RowHeight)
            ? InitialHeight + Math.Floor((desired - InitialHeight) / RowHeight) * RowHeight
            : desired;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
