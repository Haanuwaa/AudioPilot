using System.Globalization;
using System.Windows.Data;

namespace AudioPilot.Helpers;

public sealed class WindowListHeightConverter : IValueConverter
{
    public double InitialHeight { get; set; } = 104;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is double height && double.IsFinite(height)
            ? Math.Clamp(InitialHeight + (height - 378) / 2, InitialHeight, 260)
            : InitialHeight;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
