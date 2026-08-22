using System.Globalization;

namespace AudioPilot.Helpers
{
    internal static class TimeZoneDisplayFormatter
    {
        internal static string FormatCompact(TimeZoneInfo timeZone)
        {
            ArgumentNullException.ThrowIfNull(timeZone);

            string zoneName = GetCompactZoneName(timeZone.Id);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{zoneName} · {FormatUtcOffset(timeZone.BaseUtcOffset)}");
        }

        internal static string FormatDetails(TimeZoneInfo timeZone)
        {
            ArgumentNullException.ThrowIfNull(timeZone);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{timeZone.DisplayName}{Environment.NewLine}Windows time zone ID: {timeZone.Id}");
        }

        private static string GetCompactZoneName(string timeZoneId)
        {
            const string standardSuffix = " Standard Time";
            if (timeZoneId.EndsWith(standardSuffix, StringComparison.Ordinal))
            {
                string compact = timeZoneId[..^standardSuffix.Length];
                return string.IsNullOrWhiteSpace(compact) ? timeZoneId : compact;
            }

            return timeZoneId;
        }

        private static string FormatUtcOffset(TimeSpan offset)
        {
            if (offset == TimeSpan.Zero)
            {
                return "UTC";
            }

            char sign = offset < TimeSpan.Zero ? '-' : '+';
            TimeSpan magnitude = offset.Duration();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"UTC{sign}{magnitude.Hours:00}:{magnitude.Minutes:00}");
        }
    }
}
