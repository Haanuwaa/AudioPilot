using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineConflictHelperTests
{

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "")]
    public void ScheduledCommunicationsTargets_ReportConflictingRoles(bool playback, string? stableId)
    {
        var left = new AudioRoutine { Id = "one", Enabled = true, TriggerKind = RoutineTriggerKind.Scheduled, ScheduleTimeZoneId = "UTC", ScheduleTime = new TimeOnly(12, 0) };
        var right = left.Clone(); right.Id = "two";
        if (playback)
        {
            left.CommunicationsOutput = new() { Id = "out-one", StableId = stableId };
            right.CommunicationsOutput = new() { Id = "out-two", StableId = stableId };
        }
        else
        {
            left.CommunicationsInput = new() { Id = "in-one", StableId = stableId, Playback = false };
            right.CommunicationsInput = new() { Id = "in-two", StableId = stableId, Playback = false };
        }
        var warnings = AppViewModelRoutineConflictHelper.BuildConflictSummaries([left, right], () => new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings.Values, warning => Assert.Contains(playback ? "different output targets" : "different input targets", warning, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RoutineTriggerKind.Application, "same", true)]
    [InlineData(RoutineTriggerKind.Application, "different", false)]
    [InlineData(RoutineTriggerKind.Application, "system", false)]
    [InlineData(RoutineTriggerKind.Scheduled, "same", true)]
    [InlineData(RoutineTriggerKind.Scheduled, "different", false)]
    [InlineData(RoutineTriggerKind.Scheduled, "system", false)]
    public void ConflictWarnings_CompareOnlyRoutinesWithTheSameRoutingScope(RoutineTriggerKind trigger, string scope, bool conflict)
    {
        var left = new AudioRoutine { Id = "one", Enabled = true, TriggerKind = trigger, TriggerAppPath = @"C:\Apps\Trigger.exe", SwitchOutputPerApp = true, TargetAppPath = @"C:\Apps\Target.exe", OutputDeviceId = "out-1" };
        var right = left.Clone();
        right.Id = "two";
        right.OutputDeviceId = "out-2";
        if (scope == "different") right.TargetAppPath = @"C:\Apps\Other.exe";
        if (scope == "system") right.SwitchOutputPerApp = false;
        var warnings = AppViewModelRoutineConflictHelper.BuildConflictSummaries([left, right], () => new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(conflict ? 2 : 0, warnings.Count);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsAppStartRoutines_WhenSameAppTargetsDifferentOutputs()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("different output targets", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("Application trigger for Spotify", result["routine-1"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_DoesNotFlagComplementaryAppStartRoutines()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Mic",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                InputDeviceId = "in-1",
                InputDeviceName = "Microphone",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsDeviceChangeRoutines_WhenOutputsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsAudioPilotStartupRoutines_WhenOutputsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("AudioPilot startup", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("different output targets", result["routine-2"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsSteamBigPictureRoutines_WhenOutputsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.SteamBigPicture,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("Steam Big Picture", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("different output targets", result["routine-2"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsScheduledRoutines_WhenSameTimeAndSameDayOverlap()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [DayOfWeek.Monday],
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [DayOfWeek.Monday],
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("Scheduled conflicts with 1 other enabled routine: different output targets.", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("Scheduled time overlap with 1 other enabled routine: 09:00 AM on Monday.", result["routine-1"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsScheduledRoutines_WhenDailyAndSpecificDayOverlap()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Daily",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [],
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Monday",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [DayOfWeek.Monday],
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("Monday", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("Monday", result["routine-2"], StringComparison.Ordinal);
        Assert.DoesNotContain("daily.", result["routine-1"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_DoesNotFlagScheduledRoutines_WhenLocalTimesDifferByTimeZone()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-pacific",
                Name = "Pacific Nine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [DayOfWeek.Monday],
                ScheduleTimeZoneId = "Pacific Standard Time",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-eastern",
                Name = "Eastern Nine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [DayOfWeek.Monday],
                ScheduleTimeZoneId = "Eastern Standard Time",
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(
            routines,
            () => new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.Empty(result);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsScheduledRoutines_WhenDifferentLocalTimesOverlapInUtc()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-pacific",
                Name = "Pacific Nine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [DayOfWeek.Monday],
                ScheduleTimeZoneId = "Pacific Standard Time",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-eastern",
                Name = "Eastern Noon",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(12, 0),
                ScheduleDays = [DayOfWeek.Monday],
                ScheduleTimeZoneId = "Eastern Standard Time",
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(
            routines,
            () => new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, result.Count);
        Assert.Contains("Scheduled conflicts with 1 other enabled routine: different output targets.", result["routine-pacific"], StringComparison.Ordinal);
        Assert.Contains("09:00 AM on Monday", result["routine-pacific"], StringComparison.Ordinal);
        Assert.Contains("12:00 PM on Monday", result["routine-eastern"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsWifiRoutines_WhenSameSsidTargetsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Office Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "Office WiFi",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Office Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = " office wifi ",
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("Network connect 'Office WiFi'", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("different output targets", result["routine-2"], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConflictSummaries_FlagsDisconnectAnyNetworkRoutines_WhenTargetsDiffer()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Disconnect Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Name = "Disconnect Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];

        IReadOnlyDictionary<string, string> result = AppViewModelRoutineConflictHelper.BuildConflictSummaries(routines);

        Assert.Equal(2, result.Count);
        Assert.Contains("Network disconnect", result["routine-1"], StringComparison.Ordinal);
        Assert.Contains("different output targets", result["routine-2"], StringComparison.Ordinal);
    }
}
