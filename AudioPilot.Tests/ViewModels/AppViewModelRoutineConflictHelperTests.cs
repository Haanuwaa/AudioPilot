using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineConflictHelperTests
{
    [Theory]
    [InlineData(NetworkTriggerDirection.Connect)]
    [InlineData(NetworkTriggerDirection.Disconnect)]
    [InlineData(NetworkTriggerDirection.Both)]
    public void NetworkConflictWarnings_KeepCaseDistinctNetworksSeparate(NetworkTriggerDirection direction)
    {
        var first = new AudioRoutine
        {
            Id = "one",
            OutputDeviceId = "speakers",
            Triggers = [new() { Kind = RoutineTriggerKind.Network, NetworkName = "Office", NetworkDirection = direction }]
        };
        var second = first.Clone();
        second.Id = "two";
        second.OutputDeviceId = "headphones";
        second.TriggerNetworkName = "office";
        Assert.Empty(AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]));
        second.TriggerNetworkName = "Office";
        Assert.Equal(2, AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]).Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConflictsPreserveStableIdCaseButIgnoreEndpointIdCase(bool communications, bool stableIdentity)
    {
        var first = new AudioRoutine { Id = "one", TriggerKind = RoutineTriggerKind.AudioPilotStartup };
        var second = first.Clone(); second.Id = "two";
        if (communications)
        {
            first.CommunicationsOutput = new() { Id = stableIdentity ? "one" : "Endpoint", StableId = stableIdentity ? "Opaque" : null };
            second.CommunicationsOutput = new() { Id = stableIdentity ? "two" : "endpoint", StableId = stableIdentity ? "opaque" : null };
        }
        else
        {
            first.OutputDeviceId = stableIdentity ? "one" : "Endpoint"; first.OutputDeviceStableId = stableIdentity ? "Opaque" : null; first.MasterVolumePercent = 20;
            second.OutputDeviceId = stableIdentity ? "two" : "endpoint"; second.OutputDeviceStableId = stableIdentity ? "opaque" : null; second.MasterVolumePercent = 40;
        }
        var warnings = AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]);
        Assert.Equal(communications && !stableIdentity ? 0 : 2, warnings.Count);
        Assert.Equal(!communications && !stableIdentity, warnings.Values.Any(static warning => warning.Contains("different output volume levels", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(DeviceAvailabilityTransition.Connected)]
    [InlineData(DeviceAvailabilityTransition.Disconnected)]
    public void DifferentOpaqueAvailabilityTargetsDoNotProduceConflicts(DeviceAvailabilityTransition transition)
    {
        var first = new AudioRoutine
        {
            Id = "one",
            TriggerKind = RoutineTriggerKind.DeviceAvailability,
            TriggerDevice = new() { Id = "one", StableId = "Opaque" },
            DeviceTransition = transition,
            MasterVolumePercent = 20
        };
        var second = first.Clone(); second.Id = "two";
        second.TriggerDevice = new() { Id = "two", StableId = "opaque" };
        second.MasterVolumePercent = 40;
        Assert.Empty(AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]));
    }

    [Theory]
    [InlineData(NetworkTriggerDirection.Disconnect, true)]
    [InlineData(NetworkTriggerDirection.Both, true)]
    [InlineData(NetworkTriggerDirection.Connect, false)]
    public void DisconnectAnyWarningsIncludeNamedDisconnectionsWithoutMatchingConnections(NetworkTriggerDirection direction, bool conflict)
    {
        var first = new AudioRoutine
        {
            Id = "all",
            TriggerKind = RoutineTriggerKind.Network,
            NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
            MasterVolumePercent = 20
        };
        var second = first.Clone(); second.Id = "specific";
        second.TriggerNetworkName = "Office"; second.NetworkTriggerDirection = direction; second.MasterVolumePercent = 40;
        Assert.Equal(conflict ? 2 : 0, AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]).Count);
    }

    [Theory]
    [InlineData(ApplicationTriggerMode.AppLaunch, 0)]
    [InlineData(ApplicationTriggerMode.ProcessFocus, 0)]
    [InlineData(ApplicationTriggerMode.AppExit, 2)]
    public void ExitConflicts_AreComparedWithOtherExitActions(ApplicationTriggerMode otherMode, int warnings)
    {
        var exit = new AudioRoutine
        {
            Id = "exit",
            MasterVolumePercent = 30,
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.AppExit,
            TriggerAppPath = @"C:\Apps\Player.exe"
        };
        var other = exit.Clone();
        other.Id = "other";
        other.ApplicationTriggerMode = otherMode;
        other.MasterVolumePercent = 60;
        Assert.Equal(warnings, AppViewModelRoutineConflictHelper.BuildConflictSummaries([exit, other]).Count);
    }

    [Theory]
    [InlineData(RoutineTriggerKind.AudioPilotStartup, false, false)]
    [InlineData(RoutineTriggerKind.AudioPilotStartup, true, true)]
    [InlineData(RoutineTriggerKind.Scheduled, true, false)]
    [InlineData(RoutineTriggerKind.Scheduled, false, true)]
    [InlineData(RoutineTriggerKind.DeviceAvailability, false, false)]
    [InlineData(RoutineTriggerKind.DeviceAvailability, true, true)]
    public void EndpointOnlyActions_WarnForCompetingLevelsOrMute(RoutineTriggerKind trigger, bool microphone, bool mute)
    {
        var first = new AudioRoutine
        {
            Id = "one",
            TriggerKind = trigger,
            ScheduleTimeZoneId = "UTC",
            TriggerDevice = trigger == RoutineTriggerKind.DeviceAvailability ? new() { Id = "dock", Name = "Dock" } : null
        };
        var second = first.Clone();
        second.Id = "two";
        if (mute)
        {
            if (microphone) { first.InputMuteAction = RoutineMuteAction.Mute; second.InputMuteAction = RoutineMuteAction.Unmute; }
            else { first.OutputMuteAction = RoutineMuteAction.Mute; second.OutputMuteAction = RoutineMuteAction.Unmute; }
        }
        else if (microphone) { first.MicVolumePercent = 30; second.MicVolumePercent = 70; }
        else { first.MasterVolumePercent = 30; second.MasterVolumePercent = 70; }
        var summaries = AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]);
        Assert.Equal(2, summaries.Count);
        Assert.All(summaries.Values, text => Assert.Contains($"different {(microphone ? "microphone" : "output")} {(mute ? "mute actions" : "volume levels")}", text));
    }

    [Theory]
    [InlineData("same", false)]
    [InlineData("unchanged", false)]
    [InlineData("disabled", false)]
    [InlineData("no-action", false)]
    [InlineData("different-app-same-endpoint", true)]
    [InlineData("different-app-different-endpoint", false)]
    public void EndpointActionWarnings_RespectAssignmentsAndPhysicalTargets(string variation, bool warning)
    {
        var first = new AudioRoutine
        {
            Id = "one",
            TriggerKind = RoutineTriggerKind.AudioPilotStartup,
            MasterVolumePercent = 30,
            RouteToApplication = true,
            TargetAppPath = @"C:\Apps\one.exe",
            OutputDeviceId = "out-one",
            OutputDeviceStableId = "physical-one"
        };
        var second = first.Clone();
        second.Id = "two";
        second.MasterVolumePercent = variation == "same" ? 30 : variation == "unchanged" ? null : 70;
        second.TargetAppPath = @"C:\Apps\two.exe";
        if (variation == "disabled") second.Enabled = false;
        if (variation == "no-action")
        {
            second.OutputDeviceId = string.Empty;
            second.OutputDeviceStableId = null;
            second.MasterVolumePercent = null;
        }
        if (variation == "different-app-same-endpoint") second.OutputDeviceId = "replacement-id";
        if (variation == "different-app-different-endpoint") { second.OutputDeviceId = "out-two"; second.OutputDeviceStableId = "physical-two"; }
        Assert.Equal(warning ? 2 : 0, AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]).Count);
    }

    [Theory]
    [InlineData(DeviceAvailabilityTransition.Connected, true, true)]
    [InlineData(DeviceAvailabilityTransition.Disconnected, true, false)]
    [InlineData(DeviceAvailabilityTransition.Both, true, true)]
    [InlineData(DeviceAvailabilityTransition.Both, false, false)]
    public void DeviceAvailabilityWarnings_RespectTransitionAndFlow(DeviceAvailabilityTransition transition, bool playback, bool warning)
    {
        var first = new AudioRoutine
        {
            Id = "one",
            TriggerKind = RoutineTriggerKind.DeviceAvailability,
            TriggerDevice = new() { Id = "old", StableId = "dock", Name = "Dock", Playback = true },
            OutputDeviceId = "speakers"
        };
        var second = first.Clone();
        second.Id = "two";
        second.TriggerDevice = first.TriggerDevice with { Id = "replacement", Playback = playback };
        second.DeviceTransition = transition;
        second.OutputDeviceId = "headset";
        Assert.Equal(warning ? 2 : 0, AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]).Count);
    }

    [Fact]
    public void DeviceAvailabilityWarnings_IncludeGenericDeviceChangeAndIgnoreOwnOrTriggers()
    {
        var first = new AudioRoutine
        {
            Id = "one",
            TriggerKind = RoutineTriggerKind.DeviceAvailability,
            TriggerDevice = new() { Id = "dock", Name = "Dock" },
            MasterVolumePercent = 30
        };
        first.Triggers.Add(new() { Kind = RoutineTriggerKind.DeviceChange });
        Assert.Empty(AppViewModelRoutineConflictHelper.BuildConflictSummaries([first]));
        var second = new AudioRoutine { Id = "two", TriggerKind = RoutineTriggerKind.DeviceChange, MasterVolumePercent = 70 };
        Assert.Equal(2, AppViewModelRoutineConflictHelper.BuildConflictSummaries([first, second]).Count);
    }

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
        var left = new AudioRoutine { Id = "one", Enabled = true, TriggerKind = trigger, TriggerAppPath = @"C:\Apps\Trigger.exe", RouteToApplication = true, TargetAppPath = @"C:\Apps\Target.exe", OutputDeviceId = "out-1" };
        var right = left.Clone();
        right.Id = "two";
        right.OutputDeviceId = "out-2";
        if (scope == "different") right.TargetAppPath = @"C:\Apps\Other.exe";
        if (scope == "system") right.RouteToApplication = false;
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
                TriggerNetworkName = "Office WiFi",
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
