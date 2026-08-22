using AudioPilot.Models;

namespace AudioPilot.Helpers
{
    /// <summary>Calculates routine occurrences consistently for execution, reminders, the editor, and CLI previews.</summary>
    internal static class RoutineScheduleCalculator
    {
        /// <summary>Finds the first occurrence strictly after the supplied instant, including weekly schedules across daylight-saving transitions.</summary>
        internal static bool TryGetNextOccurrence(AudioRoutine routine, TimeZoneInfo timeZone, DateTime afterUtc, out DateTime occurrenceUtc)
        {
            occurrenceUtc = default;
            DateTime normalizedUtc = NormalizeToUtc(afterUtc);
            DateOnly startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, timeZone));
            int lastDayNumber = Math.Min(startDate.DayNumber + 14, DateOnly.MaxValue.DayNumber);
            for (int dayNumber = startDate.DayNumber; dayNumber <= lastDayNumber; dayNumber++)
            {
                DateOnly date = DateOnly.FromDayNumber(dayNumber);
                if (OccursOnLocalDate(routine, date.DayOfWeek)
                    && TryCreateScheduledOccurrenceUtc(date, routine.ScheduleTime, timeZone, out DateTime candidateUtc)
                    && candidateUtc > normalizedUtc)
                {
                    occurrenceUtc = candidateUtc;
                    return true;
                }
            }

            return false;
        }

        internal static TimeZoneInfo ResolveRoutineTimeZone(string? timeZoneId)
        {
            try
            {
                return string.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Local
                    : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch
            {
                return TimeZoneInfo.Local;
            }
        }

        internal static DateTime NormalizeToUtc(DateTime now)
        {
            if (now.Kind == DateTimeKind.Utc)
            {
                return now;
            }

            DateTime localNow = now.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(now, DateTimeKind.Local)
                : now;

            return localNow.ToUniversalTime();
        }

        internal static DateTime TruncateToMinute(DateTime utcTime)
        {
            DateTime normalizedUtc = NormalizeToUtc(utcTime);
            return new DateTime(normalizedUtc.Year, normalizedUtc.Month, normalizedUtc.Day, normalizedUtc.Hour, normalizedUtc.Minute, 0, DateTimeKind.Utc);
        }

        /// <summary>Finds the most recent due occurrence so resume catch-up does not scan every missed day.</summary>
        internal static bool TryGetScheduledOccurrenceInWindow(
            AudioRoutine routine,
            TimeZoneInfo routineTimeZone,
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            bool includeWindowStart,
            out DateTime occurrenceUtc)
        {
            occurrenceUtc = default;
            DateTime normalizedStartUtc = NormalizeToUtc(windowStartUtc);
            DateTime normalizedEndUtc = NormalizeToUtc(windowEndUtc);

            if (normalizedEndUtc < normalizedStartUtc)
            {
                return false;
            }

            DateOnly startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedStartUtc, routineTimeZone));
            DateOnly endDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedEndUtc, routineTimeZone));

            for (int dayNumber = endDate.DayNumber; dayNumber >= startDate.DayNumber; dayNumber--)
            {
                DateOnly localDate = DateOnly.FromDayNumber(dayNumber);
                if (!OccursOnLocalDate(routine, localDate.DayOfWeek))
                {
                    continue;
                }

                if (!TryCreateScheduledOccurrenceUtc(localDate, routine.ScheduleTime, routineTimeZone, out DateTime scheduledUtc))
                {
                    continue;
                }

                if (scheduledUtc > normalizedEndUtc)
                {
                    continue;
                }

                if (scheduledUtc > normalizedStartUtc || (includeWindowStart && scheduledUtc == normalizedStartUtc))
                {
                    occurrenceUtc = scheduledUtc;
                    return true;
                }
            }

            return false;
        }

        internal static bool TryCreateScheduledOccurrenceUtc(
            DateOnly localDate,
            TimeOnly scheduledTime,
            TimeZoneInfo routineTimeZone,
            out DateTime scheduledUtc)
        {
            DateTime localDateTime = localDate.ToDateTime(scheduledTime, DateTimeKind.Unspecified);
            if (routineTimeZone.IsInvalidTime(localDateTime))
            {
                DateTime firstValidLocalTime = localDateTime;
                for (int minute = 0; minute < 180 && routineTimeZone.IsInvalidTime(firstValidLocalTime); minute++)
                {
                    firstValidLocalTime = firstValidLocalTime.AddMinutes(1);
                }

                if (routineTimeZone.IsInvalidTime(firstValidLocalTime))
                {
                    scheduledUtc = default;
                    return false;
                }

                localDateTime = firstValidLocalTime;
            }

            TimeSpan offset;
            if (routineTimeZone.IsAmbiguousTime(localDateTime))
            {
                offset = routineTimeZone.GetAmbiguousTimeOffsets(localDateTime).Max();
            }
            else
            {
                offset = routineTimeZone.GetUtcOffset(localDateTime);
            }

            scheduledUtc = new DateTimeOffset(localDateTime, offset).UtcDateTime;
            return true;
        }

        private static bool OccursOnLocalDate(AudioRoutine routine, DayOfWeek localDay)
        {
            return routine.ScheduleDays.Count == 0 || routine.ScheduleDays.Contains(localDay);
        }

    }
}
