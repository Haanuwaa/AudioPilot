using System.Globalization;

namespace AudioPilot.Models
{
    /// <summary>Shares seek-duration input rules across Settings and the CLI while keeping persisted values in seconds.</summary>
    internal static class MediaSeekStep
    {
        internal const int DefaultSeconds = 10;
        internal const int MaximumSeconds = 3600;
        internal const string InputHelp = "Use seconds or minutes, for example 90, 90s, 1.5m, 1m30s, or 1:30. The duration must equal whole seconds, from 1 second to 60 minutes.";

        internal static int Normalize(int seconds) => seconds is >= 1 and <= MaximumSeconds ? seconds : DefaultSeconds;

        internal static bool TryParse(string? value, out int seconds)
        {
            seconds = 0;
            ReadOnlySpan<char> input = value.AsSpan().Trim();
            if (input.IsEmpty || input.Length > 64) return false;

            int total;
            int colon = input.IndexOf(':');
            int minuteSuffix = input.IndexOfAny('m', 'M');
            if (colon >= 0)
            {
                ReadOnlySpan<char> secondsPart = input[(colon + 1)..];
                if (secondsPart.Length != 2
                    || !int.TryParse(input[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                    || minutes is < 0 or > 60
                    || !int.TryParse(secondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out int remainder)
                    || remainder is < 0 or > 59) return false;
                total = minutes * 60 + remainder;
            }
            else if (minuteSuffix >= 0)
            {
                ReadOnlySpan<char> minutesPart = input[..minuteSuffix].Trim();
                ReadOnlySpan<char> remainderPart = input[(minuteSuffix + 1)..].Trim();
                if (remainderPart.IsEmpty)
                {
                    if (minutesPart.Length > 16
                        || !decimal.TryParse(minutesPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal minutes)
                        || minutes is < 0 or > 60) return false;
                    decimal totalSeconds = minutes * 60;
                    if (totalSeconds != decimal.Truncate(totalSeconds)) return false;
                    total = (int)totalSeconds;
                }
                else
                {
                    if (remainderPart[^1] is not ('s' or 'S')
                        || !int.TryParse(minutesPart, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                        || minutes is < 0 or > 60
                        || !int.TryParse(remainderPart[..^1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int remainder)
                        || remainder is < 0 or > 59) return false;
                    total = minutes * 60 + remainder;
                }
            }
            else
            {
                if (input[^1] is 's' or 'S') input = input[..^1].Trim();
                if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out total)) return false;
            }

            if (total is < 1 or > MaximumSeconds) return false;
            seconds = total;
            return true;
        }

        internal static string Format(int seconds)
        {
            seconds = Normalize(seconds);
            int minutes = seconds / 60;
            int remainder = seconds % 60;
            return minutes == 0 ? string.Create(CultureInfo.InvariantCulture, $"{seconds}s")
                : remainder == 0 ? string.Create(CultureInfo.InvariantCulture, $"{minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{minutes}m {remainder}s");
        }
    }
}
