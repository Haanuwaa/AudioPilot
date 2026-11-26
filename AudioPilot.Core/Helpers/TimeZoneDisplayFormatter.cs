using System.Globalization;

namespace AudioPilot.Helpers
{
    internal static class TimeZoneDisplayFormatter
    {
        internal static string FormatRoutineZone(string? id, bool details = false)
        {
            try
            {
                TimeZoneInfo zone = RoutineScheduleCalculator.ResolveRoutineTimeZone(id);
                return details ? FormatDetails(zone) : FormatCompact(zone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                return details ? $"Invalid time zone ID: {id}" : "Invalid time zone";
            }
        }

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
