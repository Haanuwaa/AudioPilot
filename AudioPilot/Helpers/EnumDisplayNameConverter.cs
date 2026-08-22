using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace AudioPilot.Helpers
{
    public sealed partial class EnumDisplayNameConverter : IValueConverter
    {
        [GeneratedRegex("([a-z])([A-Z])", RegexOptions.CultureInvariant)]
        private static partial Regex SplitCamelCaseRegex();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string text = value.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return SplitCamelCaseRegex().Replace(text, "$1 $2");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
