using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineConflictHelperTests
{
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
