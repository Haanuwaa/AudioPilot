using System.Globalization;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Tests.Helpers;

public sealed class RoutineScheduleCalculatorTests
{
    [Theory]
    [InlineData("UTC", "2026-01-05T08:59:00Z", 9, 0, null, "2026-01-05T09:00:00Z")]
    [InlineData("UTC", "2026-01-05T09:00:00Z", 9, 0, null, "2026-01-06T09:00:00Z")]
    [InlineData("UTC", "2026-01-05T09:00:00Z", 9, 0, DayOfWeek.Monday, "2026-01-12T09:00:00Z")]
    [InlineData("Pacific Standard Time", "2026-03-08T09:59:00Z", 2, 30, null, "2026-03-08T10:00:00Z")]
    [InlineData("Pacific Standard Time", "2026-11-01T08:29:00Z", 1, 30, null, "2026-11-01T08:30:00Z")]
    [InlineData("Pacific Standard Time", "2026-11-01T08:30:00Z", 1, 30, null, "2026-11-02T09:30:00Z")]
    [InlineData("Pacific Standard Time", "2026-01-06T06:00:00Z", 23, 0, DayOfWeek.Monday, "2026-01-06T07:00:00Z")]
    public void NextOccurrence_MatchesExecutionRules(string zoneId, string after, int hour, int minute, DayOfWeek? day, string expected)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        DateTime afterUtc = DateTime.Parse(after, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        DateTime expectedUtc = DateTime.Parse(expected, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTime = new TimeOnly(hour, minute),
            ScheduleDays = day.HasValue ? [day.Value] : [],
        };

        Assert.True(RoutineScheduleCalculator.TryGetNextOccurrence(routine, zone, afterUtc, out DateTime next));
        Assert.Equal(expectedUtc, next);
        Assert.True(RoutineScheduleCalculator.TryGetScheduledOccurrenceInWindow(routine, zone, next.AddSeconds(-1), next, false, out DateTime actual));
        Assert.Equal(next, actual);
    }

    [Fact]
    public void CatchUp_SelectsLatestWeeklyOccurrenceAcrossALongGap()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTime = new TimeOnly(9, 0),
            ScheduleDays = [DayOfWeek.Monday],
        };
        DateTime start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime end = new(2026, 1, 6, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(RoutineScheduleCalculator.TryGetScheduledOccurrenceInWindow(routine, TimeZoneInfo.Utc, start, end, false, out DateTime occurrence));
        Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), occurrence);
    }

    [Fact]
    public void OccurrenceSearch_StopsAtMaximumSupportedDate()
    {
        var routine = new AudioRoutine { TriggerKind = RoutineTriggerKind.Scheduled, ScheduleTime = new TimeOnly(23, 59) };
        DateTime lastMinute = new(9999, 12, 31, 23, 59, 0, DateTimeKind.Utc);

        Assert.True(RoutineScheduleCalculator.TryGetScheduledOccurrenceInWindow(routine, TimeZoneInfo.Utc, lastMinute, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), true, out DateTime occurrence));
        Assert.Equal(lastMinute, occurrence);
        Assert.False(RoutineScheduleCalculator.TryGetNextOccurrence(routine, TimeZoneInfo.Utc, lastMinute, out _));
    }
}
