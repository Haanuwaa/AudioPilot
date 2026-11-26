using System.Globalization;

namespace AudioPilot.Models
{
    /// <summary>Shares seek-duration input rules across Settings and the CLI while keeping persisted values in seconds.</summary>
    internal static class MediaSeekStep
    {
        internal const int DefaultSeconds = 10;
        internal const decimal MinimumSeconds = 0.1m;
        internal const int MaximumSeconds = 3600;
        internal const string InputHelp = "Use a duration from 0.1 seconds to 60 minutes in tenths of a second, for example 100ms, 1.5s, 1.5m, 1m30s, or 1:30.";

        internal static bool IsValid(decimal seconds) =>
            seconds is >= MinimumSeconds and <= MaximumSeconds && decimal.Round(seconds, 1) == seconds;

        internal static decimal Normalize(decimal seconds) => IsValid(seconds) ? seconds : DefaultSeconds;

        internal static bool TryParse(string? value, out decimal seconds, bool allowDecimalComma = false)
        {
            seconds = 0;
            ReadOnlySpan<char> input = value.AsSpan().Trim();
            if (input.IsEmpty || input.Length > 64) return false;

            decimal total;
            int colon = input.IndexOf(':');
            int minuteSuffix = input.IndexOfAny('m', 'M');
            if (input.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(input[..^2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds))
                    return false;
                total = milliseconds / 1000m;
            }
            else if (colon >= 0)
            {
                ReadOnlySpan<char> secondsPart = input[(colon + 1)..];
                int separator = secondsPart.IndexOfAny('.', ',');
                if ((separator < 0 ? secondsPart.Length : separator) != 2 ||
                    !TryParseMinutesAndSeconds(input[..colon], secondsPart, allowDecimalComma, out total)) return false;
            }
            else if (minuteSuffix >= 0)
            {
                ReadOnlySpan<char> minutesPart = input[..minuteSuffix].Trim();
                ReadOnlySpan<char> remainderPart = input[(minuteSuffix + 1)..].Trim();
                if (remainderPart.IsEmpty)
                {
                    if (!DecimalInput.TryParse(minutesPart, allowDecimalComma, out decimal minutes)
                        || minutes is < 0 or > 60) return false;
                    total = minutes * 60;
                }
                else
                {
                    if (remainderPart[^1] is not ('s' or 'S')
                        || !TryParseMinutesAndSeconds(minutesPart, remainderPart[..^1].Trim(), allowDecimalComma, out total)) return false;
                }
            }
            else
            {
                if (input[^1] is 's' or 'S') input = input[..^1].Trim();
                if (!DecimalInput.TryParse(input, allowDecimalComma, out total)) return false;
            }

            if (!IsValid(total)) return false;
            seconds = total;
            return true;
        }

        private static bool TryParseMinutesAndSeconds(ReadOnlySpan<char> minutesPart, ReadOnlySpan<char> secondsPart,
            bool allowDecimalComma, out decimal total)
        {
            total = 0;
            if (!int.TryParse(minutesPart, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                minutes is < 0 or > 60 || !DecimalInput.TryParse(secondsPart, allowDecimalComma, out decimal seconds) ||
                seconds >= 60) return false;
            total = minutes * 60 + seconds;
            return true;
        }

        internal static string Format(decimal seconds)
        {
            seconds = Normalize(seconds);
            int minutes = (int)(seconds / 60);
            decimal remainder = seconds % 60;
            return minutes == 0 ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#}s")
                : remainder == 0 ? string.Create(CultureInfo.InvariantCulture, $"{minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{minutes}m {remainder:0.#}s");
        }
    }
}
