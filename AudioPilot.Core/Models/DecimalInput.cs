using System.Globalization;

namespace AudioPilot.Models;

internal static class DecimalInput
{
    private static readonly NumberFormatInfo CommaDecimalFormat = new() { NumberDecimalSeparator = "," };

    internal static bool TryParse(ReadOnlySpan<char> input, bool allowDecimalComma, out decimal value)
    {
        value = 0;
        return input.Length is > 0 and <= 16 && char.IsAsciiDigit(input[^1]) &&
            (decimal.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) ||
             (allowDecimalComma && decimal.TryParse(input, NumberStyles.AllowDecimalPoint, CommaDecimalFormat, out value)));
    }
}
