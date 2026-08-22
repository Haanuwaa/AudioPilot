using System.Text.Json;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Tests.Helpers;

public sealed class RoutineSchedulePreviewTests
{
    [Theory]
    [InlineData("Fixture/MissingZone", false)]
    [InlineData("UTC", true)]
    [InlineData("", true)]
    public void ImportNormalization_PreservesExplicitInvalidZonesAndDefaultsOnlyMissingValues(string id, bool valid)
    {
        var routine = new AudioRoutine { MasterVolumePercent = 40, TriggerKind = RoutineTriggerKind.Scheduled, ScheduleTime = new(12, 0), ScheduleTimeZoneId = id };
        var settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Routines = new() { Items = [routine] } }))!;
        SettingsValidationService.Normalize(settings);
        var saved = Assert.Single(settings.Routines.Items);
        Assert.Equal(string.IsNullOrEmpty(id) ? TimeZoneInfo.Local.Id : id, saved.ScheduleTimeZoneId);
        Assert.Equal(valid, saved.ValidateTriggers() == null);
        Assert.Equal(valid, saved.ExpandAutomaticTriggers().Any());
        Assert.Equal(valid, RoutineScheduleCalculator.TryGetNextOccurrence(saved, new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc), out _, out _));
    }

    [Theory]
    [InlineData(true, false, "Next scheduled trigger")]
    [InlineData(true, true, "paused")]
    [InlineData(false, false, "disabled")]
    public void Preview_UsesTheEarliestTriggerAndExplainsPauseAndRequirements(bool enabled, bool paused, string expected)
    {
        var routine = new AudioRoutine
        {
            Enabled = enabled,
            MasterVolumePercent = 40,
            Triggers = [
            new() { Id = "late", Kind = RoutineTriggerKind.Scheduled, TimeZoneId = "UTC", Time = new(15, 0) },
            new() { Id = "early", Kind = RoutineTriggerKind.Scheduled, TimeZoneId = "UTC", Time = new(12, 0) }]
        };
        DateTime now = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(RoutineScheduleCalculator.TryGetNextOccurrence(routine, now, out var next, out var occurrence));
        Assert.Equal("early", next!.TriggerIdentity);
        Assert.Equal(now.AddHours(12), occurrence);
        string preview = RoutineScheduleCalculator.BuildPreview(routine, now, paused);
        Assert.Contains(expected, preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UTC", preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Requirements still apply", preview, StringComparison.Ordinal);
    }
}
