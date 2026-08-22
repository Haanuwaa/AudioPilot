using System.Globalization;
using System.Text.Json;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineTimeWindowTests
{
    [Fact]
    public void Equality_UsesWeekdayValuesAcrossSerializationAndIgnoresOrdering()
    {
        var original = new RoutineTimeWindow { TimeZoneId = "UTC", Days = [DayOfWeek.Friday, DayOfWeek.Monday] };
        var deserialized = JsonSerializer.Deserialize<RoutineTimeWindow>(JsonSerializer.Serialize(original, SettingsJson.Options), SettingsJson.Options);
        Assert.Equal(original, deserialized);
        var reordered = original with { Days = [DayOfWeek.Monday, DayOfWeek.Friday, DayOfWeek.Monday] };
        Assert.Equal(original, reordered);
        Assert.Equal(original.GetHashCode(), reordered.GetHashCode());
        Assert.Equal(original.ConfigurationKey, reordered.ConfigurationKey);
        Assert.Equal(original.Summary, reordered.Summary);
        Assert.NotEqual(original, original with { Days = [DayOfWeek.Monday] });
        Assert.NotEqual(original, original with { AllDay = true });
        Assert.NotEqual(original, original with { Start = new(10, 0) });
        Assert.Equal(new RoutineTimeWindow { Days = default }, new RoutineTimeWindow { Days = [] });
    }

    [Theory]
    [InlineData("2026-09-21T21:59:59Z", false)]
    [InlineData("2026-09-21T22:00:00Z", true)]
    [InlineData("2026-09-22T01:59:59Z", true)]
    [InlineData("2026-09-22T02:00:00Z", false)]
    [InlineData("2026-09-21T01:00:00Z", false)]
    [InlineData("2026-09-22T23:00:00Z", false)]
    public void Overnight_UsesStartingWeekdayAndExclusiveEnd(string instant, bool expected)
    {
        var window = new RoutineTimeWindow { Start = new(22, 0), End = new(2, 0), Days = [DayOfWeek.Monday], TimeZoneId = "UTC" };
        Assert.Equal(expected, window.Contains(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void WeekRollover_AllDay_AndEveryDayHaveDistinctSemantics()
    {
        var window = new RoutineTimeWindow { Start = new(22, 0), End = new(2, 0), Days = [DayOfWeek.Saturday], TimeZoneId = "UTC" };
        var sunday = new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);
        Assert.True(window.Contains(sunday));
        Assert.False((window with { AllDay = true }).Contains(sunday));
        Assert.True((window with { AllDay = true, Days = [] }).Contains(sunday));
        Assert.True((window with { Days = [] }).Contains(sunday));
        Assert.False((window with { Days = [] }).Contains(sunday.AddHours(1)));
    }

    [Theory]
    [InlineData("2026-09-21T08:59:59Z", false)]
    [InlineData("2026-09-21T09:00:00Z", true)]
    [InlineData("2026-09-21T16:59:59Z", true)]
    [InlineData("2026-09-21T17:00:00Z", false)]
    public void Daytime_IncludesStartAndExcludesEnd(string instant, bool expected)
    {
        Assert.Equal(expected, new RoutineTimeWindow { TimeZoneId = "UTC" }.Contains(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("2026-11-01T05:30:00Z", true)]
    [InlineData("2026-11-01T06:30:00Z", true)]
    [InlineData("2026-11-01T07:00:00Z", false)]
    [InlineData("2026-03-08T06:30:00Z", true)]
    [InlineData("2026-03-08T07:00:00Z", false)]
    public void SavedTimeZone_UsesWallClockAcrossDaylightSavingChanges(string instant, bool expected)
    {
        var window = new RoutineTimeWindow { Start = new(1, 0), End = new(2, 0), Days = [DayOfWeek.Sunday], TimeZoneId = "Eastern Standard Time" };
        Assert.Equal(expected, window.Contains(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Validation_RejectsAmbiguousRangesInvalidDaysAndUnknownZones()
    {
        Assert.NotNull(new RoutineTimeWindow { Start = new(9, 0), End = new(9, 0) }.Validate());
        Assert.Null(new RoutineTimeWindow { AllDay = true, Start = new(9, 0), End = new(9, 0) }.Validate());
        Assert.NotNull(new RoutineTimeWindow { Start = new(9, 0, 30) }.Validate());
        Assert.NotNull(new RoutineTimeWindow { Days = [(DayOfWeek)99] }.Validate());
        Assert.NotNull(new RoutineTimeWindow { TimeZoneId = "missing-zone" }.Validate());
    }

    [Fact]
    public void EditorAndJsonRoundTrip_PreserveConditionAndGroupAndRejectInvalidNestedInput()
    {
        var source = new AudioRoutine
        {
            Name = "Night",
            MasterVolumePercent = 30,
            Hotkey = "Ctrl+Alt+R",
            HotkeyCycleGroup = " Desk ",
            Conditions = new() { TimeWindow = new() { Start = new(22, 0), End = new(2, 0), Days = [DayOfWeek.Monday], TimeZoneId = "UTC" } },
        };
        string json = JsonSerializer.Serialize(source, SettingsJson.Options);
        AudioRoutine imported = RoutineTransferService.ParseSingleRoutine(json);
        Assert.Equal("Desk", imported.HotkeyCycleGroup);
        using var editor = new RoutineEditorViewModel([], [], imported);
        Assert.True(editor.RequireTimeWindow);
        Assert.True(editor.ConditionMonday);
        Assert.False(editor.ConditionTuesday);
        Assert.Null(editor.Validate());
        var saved = editor.BuildRoutine();
        Assert.Equal(source.Conditions.ConfigurationKey, saved.Conditions.ConfigurationKey);
        editor.ConditionEnd = "invalid";
        Assert.NotNull(editor.Validate());
        editor.ConditionAllDay = true;
        Assert.Null(editor.Validate());
        editor.RequireTimeWindow = false;
        Assert.Null(editor.BuildRoutine().Conditions.TimeWindow);
        Assert.Throws<JsonException>(() => RoutineTransferService.ParseSingleRoutine("""{"Conditions":{"TimeWindow":{"Unexpected":true}}}"""));
    }
}
