using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AudioPilot.Models;

/// <summary>A wall-clock eligibility window whose weekdays refer to the start of an overnight range.</summary>
public sealed record RoutineTimeWindow
{
    public TimeOnly Start { get; init; } = new(9, 0);
    public TimeOnly End { get; init; } = new(17, 0);
    public bool AllDay { get; init; }
    public ImmutableArray<DayOfWeek> Days { get; init; } = [];
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    [JsonIgnore] internal string ConfigurationKey => $"{Start:HH:mm:ss}\u001f{End:HH:mm:ss}\u001f{AllDay}\u001f{string.Join(',', (Days.IsDefault ? [] : Days).Distinct().Order())}\u001f{TimeZoneId}";
    [JsonIgnore]
    public string Summary => $"{(Days.IsDefaultOrEmpty ? "Every day" : string.Join(", ", Days.Distinct().Order()))}: " +
        (AllDay ? "all day" : $"{Start.ToString("t", CultureInfo.CurrentCulture)}–{End.ToString("t", CultureInfo.CurrentCulture)}{(End < Start ? " next day" : string.Empty)}") + $" [{TimeZoneId}]";

    public bool Equals(RoutineTimeWindow? other) => other != null && Start == other.Start && End == other.End &&
        AllDay == other.AllDay && string.Equals(TimeZoneId, other.TimeZoneId, StringComparison.Ordinal) &&
        (Days.AsSpan().SequenceEqual(other.Days.AsSpan()) ||
            (!Days.IsDefaultOrEmpty && !other.Days.IsDefaultOrEmpty && Days.Distinct().Order().SequenceEqual(other.Days.Distinct().Order())));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Start);
        hash.Add(End);
        hash.Add(AllDay);
        hash.Add(TimeZoneId, StringComparer.Ordinal);
        if (!Days.IsDefaultOrEmpty)
            foreach (DayOfWeek day in Days.Distinct().Order()) hash.Add(day);
        return hash.ToHashCode();
    }

    internal string? Validate()
    {
        if (!Days.IsDefault && Days.Any(static day => !Enum.IsDefined(day))) return "Choose valid weekdays for the time condition.";
        if (!AllDay && (Start.Ticks % TimeSpan.TicksPerMinute != 0 || End.Ticks % TimeSpan.TicksPerMinute != 0))
            return "Use hours and minutes for the time condition.";
        if (!AllDay && Start == End) return "The time condition needs different start and end times, or choose All day.";
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return "The time condition requires a valid time zone."; }
        return null;
    }

    internal bool Contains(DateTimeOffset now)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));
        TimeOnly time = TimeOnly.FromDateTime(local.DateTime);
        DayOfWeek day = local.DayOfWeek;
        if (!AllDay)
        {
            if (Start < End) { if (time < Start || time >= End) return false; }
            else
            {
                if (time >= End && time < Start) return false;
                if (time < End) day = (DayOfWeek)(((int)day + 6) % 7);
            }
        }
        return Days.IsDefaultOrEmpty || Days.Contains(day);
    }
}
