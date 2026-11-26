using System.Globalization;

namespace AudioPilot.Models;

internal static class VolumePercent
{
    internal const decimal MinimumStep = 0.1m;
    internal const decimal DefaultStep = 5m;
    internal const string StepGuidance = "Enter a step from 0.1 to 100 percentage points with at most one decimal place.";

    internal static bool IsValidStep(decimal value) => value >= MinimumStep && IsValidLevel(value);

    private static bool IsValidLevel(decimal value) => value is >= 0 and <= 100 && decimal.Round(value, 1) == value;

    internal static bool TryParseStep(string? text, out decimal value, bool allowDecimalComma = false)
    {
        ReadOnlySpan<char> input = text.AsSpan().Trim();
        if (input.StartsWith("+", StringComparison.Ordinal)) input = input[1..];
        return DecimalInput.TryParse(input, allowDecimalComma, out value) && IsValidStep(value);
    }

    internal static bool TryParseLevel(string? text, out decimal value) =>
        DecimalInput.TryParse(text.AsSpan().Trim(), allowDecimalComma: true, out value) && IsValidLevel(value);

    internal static decimal NormalizeStep(decimal value) => value < MinimumStep ? DefaultStep : NormalizeLevel(value);

    internal static decimal NormalizeLevel(decimal value) => decimal.Round(Math.Clamp(value, 0, 100), 1, MidpointRounding.AwayFromZero);

    internal static string Format(float value, IFormatProvider? provider = null) =>
        MathF.Round(Math.Clamp(value, 0, 100), 1, MidpointRounding.AwayFromZero).ToString("0.#", provider ?? CultureInfo.CurrentCulture);
}
