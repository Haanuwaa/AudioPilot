using System.Text.Json;
using System.Text.Json.Nodes;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Tests.Models;

public sealed class AudioRoutineTests
{

    [Fact]
    public void DetailsTriggers_ShowTimeZoneAndReminderForEverySchedule()
    {
        var routine = new AudioRoutine
        {
            Triggers = [new() { Kind = RoutineTriggerKind.Scheduled, TimeZoneId = "UTC", NotifyBeforeRun = true },
                new() { Kind = RoutineTriggerKind.Scheduled, Time = new(15, 30), TimeZoneId = "Pacific Standard Time" }],
        };
        Assert.Contains("[UTC] (reminder)", routine.RoutineDetailsTriggerSummary);
        Assert.Contains("[Pacific Standard Time]", routine.RoutineDetailsTriggerSummary);
        Assert.Equal(2, routine.RoutineDetailsTriggerSummary.Count(character => character == '['));
        Assert.Equal(routine.Triggers[0].Summary + " | OR " + routine.Triggers[1].Summary, routine.RoutineDetailsTriggerSummary);
    }

    [Fact]
    public void DetailsActions_IncludeApplicationCommunicationsVolumesAndMute_AndNotifyChanges()
    {
        var routine = new AudioRoutine
        {
            SwitchOutputPerApp = true,
            TargetAppPath = @"C:\Apps\Player.exe",
            OutputDeviceId = "out",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in",
            InputDeviceName = "Desk microphone",
            CommunicationsOutput = new() { Id = "calls-out", Name = "Headset" },
            CommunicationsInput = new() { Id = "calls-in", Name = "Headset microphone", Playback = false },
            MasterVolumePercent = 65,
            MicVolumePercent = 80,
            OutputMuteAction = RoutineMuteAction.Unmute,
            InputMuteAction = RoutineMuteAction.Mute,
        };
        string[] expected = ["App: Player", "Output: Speakers", "Input: Desk microphone",
            "Communications output: Headset", "Communications microphone: Headset microphone",
            "Master: 65%", "Microphone: 80%", "Output: Unmute", "Microphone: Mute"];
        Assert.Equal(expected, routine.RoutineDetailsTargetSummary.Split(Environment.NewLine));
        var changed = new List<string?>();
        routine.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        routine.InputMuteAction = RoutineMuteAction.Unchanged;
        Assert.Contains(nameof(AudioRoutine.RoutineDetailsTargetSummary), changed);
        Assert.DoesNotContain("Microphone: Mute", routine.RoutineDetailsTargetSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationTarget_RoundTripsIndependentlyAndSurvivesTriggerChanges(bool explicitTarget)
    {
        string json = explicitTarget
            ? "{\"TargetAppPath\":\"target.exe\",\"SwitchOutputPerApp\":true,\"Triggers\":[{\"AppPath\":\"trigger.exe\",\"Kind\":1}]}"
            : "{\"SwitchOutputPerApp\":true,\"Triggers\":[{\"AppPath\":\"trigger.exe\",\"Kind\":1}]}";
        AudioRoutine routine = JsonSerializer.Deserialize<AudioRoutine>(json)!;
        string expected = explicitTarget ? "target.exe" : "trigger.exe";
        Assert.Equal(expected, routine.TargetAppPath);
        routine.TriggerKind = RoutineTriggerKind.Scheduled;
        routine.TriggerKind = RoutineTriggerKind.Hotkey;
        Assert.True(routine.SwitchOutputPerApp);
        Assert.Empty(routine.TriggerAppPath);
        Assert.Equal(expected, routine.Clone().TargetAppPath);
        Assert.Equal(expected, JsonSerializer.Deserialize<AudioRoutine>(JsonSerializer.Serialize(routine))!.TargetAppPath);
    }

    [Fact]
    public void ScheduledReminder_IsOptInSurvivesCloneAndClearsWithTriggerChange()
    {
        var routine = new AudioRoutine { TriggerKind = RoutineTriggerKind.Scheduled };
        Assert.False(routine.NotifyBeforeScheduledRun);
        routine.NotifyBeforeScheduledRun = true;
        Assert.True(routine.Clone().NotifyBeforeScheduledRun);
        routine.TriggerKind = RoutineTriggerKind.Hotkey;
        Assert.False(routine.NotifyBeforeScheduledRun);
    }

    [Fact]
    public void ScheduleDays_WhenAssignedNull_NormalizesToEmptySet()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleDays = null!,
        };

        Assert.Empty(routine.ScheduleDays);
    }

    [Fact]
    public void TriggerSummary_OrdersScheduledDaysDeterministically()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTime = new TimeOnly(8, 0),
            ScheduleDays = [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday],
        };

        Assert.Contains("Monday, Wednesday, Friday", routine.TriggerSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleTimeZoneDisplay_UsesCompactCanonicalWindowsName()
    {
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById("SA Western Standard Time");
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTimeZoneId = timeZone.Id,
        };

        Assert.Equal("SA Western · UTC-04:00", routine.ScheduleTimeZoneDisplay);
        Assert.NotEqual(timeZone.Id, routine.ScheduleTimeZoneDisplay);
        Assert.Contains(timeZone.DisplayName, routine.ScheduleTimeZoneDetails, StringComparison.Ordinal);
        Assert.Contains(timeZone.Id, routine.ScheduleTimeZoneDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleTimeZoneDisplay_FallsBackToCompactLocalNameForInvalidId()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTimeZoneId = "Invalid/TimeZone",
        };

        Assert.Equal(TimeZoneDisplayFormatter.FormatCompact(TimeZoneInfo.Local), routine.ScheduleTimeZoneDisplay);
    }

    [Fact]
    public void TriggerSummary_IncludesAppAudioOnly_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            InputDeviceId = "in-1",
            InputDeviceName = "Mic",
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            ShowInTrayMenu = true,
        };

        Assert.Equal("Application launch: Spotify | Application audio only | Tray menu", routine.TriggerSummary);
    }

    [Fact]
    public void RoutineDetailsTriggerSummary_ExcludesAppAudioAndRestoreOptions()
    {
        var routine = new AudioRoutine
        {
            InputDeviceId = "in-1",
            InputDeviceName = "Mic",
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            RestorePreviousAudioOnDeactivate = true,
        };

        Assert.Equal("Application launch: Spotify | Application audio only | Restore on exit", routine.TriggerSummary);
        Assert.Equal("Application launch: Spotify", routine.RoutineDetailsTriggerSummary);
        Assert.True(routine.HasRoutineDetailsOptions);
        Assert.Equal("Application audio only | Restore previous audio on deactivate", routine.RoutineDetailsOptionsSummary);
    }

    [Fact]
    public void Clone_PreservesSwitchOutputPerApp()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Spotify",
            OutputDeviceId = "out-1",
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            MasterVolumePercent = 42,
        };

        AudioRoutine clone = routine.Clone();

        Assert.True(clone.SwitchOutputPerApp);
        Assert.Equal(42, clone.MasterVolumePercent);
    }

    [Fact]
    public void Clone_PreservesScheduledAndNetworkTriggerDetails()
    {
        var scheduled = new AudioRoutine
        {
            Id = "routine-scheduled",
            Name = "Morning",
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTime = new TimeOnly(9, 15),
            ScheduleDays = [DayOfWeek.Monday, DayOfWeek.Friday],
            ScheduleTimeZoneId = "Pacific Standard Time",
        };

        var network = new AudioRoutine
        {
            Id = "routine-network",
            Name = "Office",
            TriggerKind = RoutineTriggerKind.Network,
            TriggerNetworkName = "Office WiFi",
            NetworkTriggerDirection = NetworkTriggerDirection.Both,
        };

        AudioRoutine scheduledClone = scheduled.Clone();
        AudioRoutine networkClone = network.Clone();

        Assert.Equal("Pacific Standard Time", scheduledClone.ScheduleTimeZoneId);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], [.. scheduledClone.ScheduleDays.OrderBy(static day => (int)day)]);
        Assert.Equal(NetworkTriggerDirection.Both, networkClone.NetworkTriggerDirection);
        Assert.Equal("Office WiFi", networkClone.TriggerNetworkName);
    }

    [Fact]
    public void Clone_PreservesProcessFocusApplicationTriggerDetails()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-focus",
            Name = "Spotify Focus",
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            ApplicationTriggerTitlePattern = "playlist",
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex,
        };

        AudioRoutine clone = routine.Clone();

        Assert.Equal(ApplicationTriggerMode.ProcessFocus, clone.ApplicationTriggerMode);
        Assert.Equal("playlist", clone.ApplicationTriggerTitlePattern);
        Assert.Equal(ApplicationTriggerTitleMatchMode.Regex, clone.ApplicationTriggerTitleMatchMode);
    }

    [Fact]
    public void TargetSummary_IncludesConfiguredVolumeTargets()
    {
        var routine = new AudioRoutine
        {
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            MasterVolumePercent = 55,
            MicVolumePercent = 10,
        };

        Assert.Equal("Output: Speakers | Master: 55% | Microphone: 10%", routine.TargetSummary);
        Assert.Equal("Out", routine.TargetKindBadgeText);
    }

    [Fact]
    public void TargetKindBadgeText_UsesVolumeOnly_WhenOnlyVolumeTargetsConfigured()
    {
        var routine = new AudioRoutine
        {
            MasterVolumePercent = 30,
        };

        Assert.Equal("Vol", routine.TargetKindBadgeText);
    }

    [Fact]
    public void HasExecutionTarget_ReturnsTrue_WhenOnlyVolumeTargetsConfigured()
    {
        var routine = new AudioRoutine
        {
            MasterVolumePercent = 30,
        };

        Assert.True(routine.HasExecutionTarget);
    }

    [Fact]
    public void OutputDeviceId_NotifiesTriggerAndDetailOptions_WhenAppAudioOnlyIsConfigured()
    {
        var routine = new AudioRoutine
        {
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
        };
        var changedProperties = new HashSet<string?>();
        routine.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        routine.OutputDeviceId = "out-1";

        Assert.Contains(nameof(AudioRoutine.TriggerSummary), changedProperties);
        Assert.Contains(nameof(AudioRoutine.RoutineDetailsTriggerSummary), changedProperties);
        Assert.Contains(nameof(AudioRoutine.HasRoutineDetailsOptions), changedProperties);
        Assert.Contains(nameof(AudioRoutine.RoutineDetailsOptionsSummary), changedProperties);
        Assert.Contains(nameof(AudioRoutine.HasExecutionTarget), changedProperties);
        Assert.Equal("Application launch: Spotify | Application audio only", routine.TriggerSummary);
        Assert.Equal("Application audio only", routine.RoutineDetailsOptionsSummary);
    }

    [Fact]
    public void TriggerSummary_UsesPackagedAppDisplayName_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            UsesApplicationTrigger = true,
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            ShowInTrayMenu = true,
        };

        Assert.Equal("Application launch: SpotifyAB SpotifyMusic | Tray menu", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerSummary_IncludesProcessFocusTitleMetadata_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            ApplicationTriggerTitlePattern = "playlist",
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Contains,
        };

        Assert.Equal("Application focus: Spotify | Title (contains): playlist", routine.TriggerSummary);
        Assert.Equal("Application focus: Spotify | Title (contains): playlist", routine.RoutineDetailsTriggerSummary);
    }

    [Fact]
    public void ApplicationTriggerMode_ClearsProcessFocusTitleMetadata_WhenSwitchingBackToLaunch()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            ApplicationTriggerTitlePattern = "playlist",
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex,
        };

        routine.ApplicationTriggerMode = ApplicationTriggerMode.AppLaunch;

        Assert.Equal(string.Empty, routine.ApplicationTriggerTitlePattern);
        Assert.Equal(ApplicationTriggerTitleMatchMode.Contains, routine.ApplicationTriggerTitleMatchMode);
        Assert.Equal("Application launch: Spotify", routine.TriggerSummary);
    }

    [Fact]
    public void UsesApplicationTrigger_SetsTriggerKindToApplication()
    {
        var routine = new AudioRoutine
        {
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.True(routine.HasApplicationTrigger);
    }

    [Fact]
    public void Serialize_DoesNotPersistUsesApplicationTrigger()
    {
        var routine = new AudioRoutine
        {
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(routine))!.AsObject();

        Assert.Equal(nameof(RoutineTriggerKind.Application), json[nameof(AudioRoutine.Triggers)]?[0]?[nameof(RoutineTrigger.Kind)]?.GetValue<string>());
        Assert.Null(json[nameof(AudioRoutine.UsesApplicationTrigger)]);
    }

    [Fact]
    public void TriggerSummary_UsesDeviceChangeTrigger_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.DeviceChange,
            ShowInTrayMenu = true,
        };

        Assert.Equal("Device change | Tray menu", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerSummary_CombinesHotkeyAndTrayMenu()
    {
        var routine = new AudioRoutine
        {
            Hotkey = "Ctrl+Alt+R",
            TriggerKind = RoutineTriggerKind.Hotkey,
            ShowInTrayMenu = true,
        };

        Assert.Equal("Hotkey: Ctrl+Alt+R | Tray menu", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerSummary_UsesAudioPilotStartupTrigger_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.AudioPilotStartup,
            ShowInTrayMenu = true,
        };

        Assert.Equal("AudioPilot startup | Tray menu", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerSummary_UsesDisconnectNetworkTrigger_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Network,
            NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
        };

        Assert.True(routine.HasNetworkTrigger);
        Assert.Equal("Network: All networks disconnected", routine.TriggerSummary);
    }

    [Fact]
    public void TriggerSummary_UsesConnectDisconnectNetworkTrigger_WhenConfigured()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Network,
            TriggerNetworkName = "HomeWiFi",
            NetworkTriggerDirection = NetworkTriggerDirection.Both,
        };

        Assert.True(routine.HasNetworkTrigger);
        Assert.Equal("Network: Connect/disconnect — HomeWiFi", routine.TriggerSummary);
    }

    [Fact]
    public void LastRunStatusText_ShowsFailureState()
    {
        var routine = new AudioRoutine
        {
            LastRunState = RoutineLastRunState.Failed,
            LastRunUtc = DateTimeOffset.UtcNow,
        };

        Assert.Contains("Last run:", routine.LastRunStatusText, StringComparison.Ordinal);
        Assert.Contains("Failed", routine.LastRunStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void LastRunStatusText_ClarifiesWaitingForAppAudio()
    {
        var routine = new AudioRoutine
        {
            LastRunState = RoutineLastRunState.WaitingForApp,
        };

        Assert.Equal("Last run: Waiting for app audio", routine.LastRunStatusText);
    }

    [Fact]
    public void TriggerKind_ClearsAppStartOnlyFields_WhenSwitchingToAudioPilotStartup()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            ShowInTrayMenu = true,
            RestorePreviousAudioOnDeactivate = true,
        };

        routine.TriggerKind = RoutineTriggerKind.AudioPilotStartup;

        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.True(routine.SwitchOutputPerApp);
        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", routine.TargetAppPath);
        Assert.True(routine.ShowInTrayMenu);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
        Assert.True(routine.HasAudioPilotStartupTrigger);
    }

    [Fact]
    public void TriggerKind_PreservesManualAccess_WhenLeavingHotkey()
    {
        var routine = new AudioRoutine
        {
            TriggerKind = RoutineTriggerKind.Hotkey,
            Hotkey = "Ctrl+Alt+R",
            ShowInTrayMenu = true,
        };

        routine.TriggerKind = RoutineTriggerKind.Application;

        Assert.True(routine.ShowInTrayMenu);
    }
}
