using System.Globalization;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineTriggerSummaryTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void ScheduleSummaryPreservesRegionalTimeDetailsAndSharedDays(string language)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
            var localization = new LocalizationService(language);
            var trigger = new RoutineTrigger
            {
                Kind = RoutineTriggerKind.Scheduled,
                Time = new(17, 15),
                TimeZoneId = "UTC",
                Days = [DayOfWeek.Friday, DayOfWeek.Monday],
                NotifyBeforeRun = true,
            };
            HashSet<DayOfWeek> sharedDays = trigger.Days;
            string compact = localization.FormatKey("Routine_ScheduledAt", "17:15",
                $" {localization.FormatValue(DayOfWeek.Monday)}, {localization.FormatValue(DayOfWeek.Friday)}");
            string details = compact + " [UTC]" + localization["Routine_Reminder"];
            Assert.Equal(details, trigger.FormatSummary(localization));
            var routine = new AudioRoutine { Triggers = [trigger] };
            Assert.Equal(compact, routine.TriggerPresentation(localization));
            Assert.Equal(details, routine.TriggerDetailsPresentation(localization));
            routine.Triggers.Insert(0, new() { Kind = RoutineTriggerKind.AudioPilotStartup });
            Assert.Equal(localization["Routine_AudioPilotStartup"] + " | " + localization.FormatKey("Routine_Or", details), routine.TriggerPresentation(localization));
            Assert.Same(sharedDays, trigger.Days);
            Assert.True(sharedDays.SetEquals([DayOfWeek.Friday, DayOfWeek.Monday]));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void FocusSummaryPreservesRoutineOptionsAndManualActionsAcrossLiveLanguageChanges()
    {
        var trigger = new RoutineTrigger
        {
            AppPath = @"C:\Synthetic\Player.exe",
            ApplicationMode = ApplicationTriggerMode.ProcessFocus,
            TitlePattern = "Project *",
            TitleMatchMode = ApplicationTriggerTitleMatchMode.Wildcard,
        };
        var routine = new AudioRoutine
        {
            Triggers = [trigger, new() { Kind = RoutineTriggerKind.SystemResume }],
            OutputDeviceId = "synthetic-output",
            RouteToApplication = true,
            RestorePreviousAudioOnDeactivate = true,
            Hotkey = "Ctrl+Alt+R",
            HotkeyCycleGroup = "Desk",
            ShowInTrayMenu = true,
        };
        var localization = new LocalizationService("en");
        foreach (string language in new[] { "en", "es" })
        {
            localization.SetLanguage(language);
            string focus = localization.FormatKey("Routine_AppTrigger", localization["Routine_Focus"], "Player") + " | "
                + localization.FormatKey("Routine_Title", localization["Routine_Wildcard"], "Project *");
            Assert.Equal(focus, trigger.FormatSummary(localization));
            Assert.Equal(string.Join(" | ", focus, localization["Routine_ApplicationAudioOnly"],
                localization.FormatKey("Routine_Or", localization["Routine_SystemResume"]),
                localization.FormatKey("Routine_CycleGroup", "Desk", "Ctrl+Alt+R"), localization["Routine_RestoreOnExit"], localization["Routine_TrayMenu"]),
                routine.TriggerPresentation(localization));
        }
        Assert.Same(trigger, routine.Triggers[0]);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void EmptyPreviewsKeepTheirDistinctFallbacksAndNetworkIdentity(string language)
    {
        var localization = new LocalizationService(language);
        var empty = new RoutineTrigger();
        Assert.Equal(localization["Routine_PickApp"], empty.FormatSummary(localization));
        Assert.Equal(localization["Routine_RunManually"], new AudioRoutine { Triggers = [empty] }.TriggerPresentation(localization));
        Assert.Equal(localization["Routine_ManualOnly"], (empty with { Kind = RoutineTriggerKind.Hotkey }).FormatSummary(localization));
        var network = new RoutineTrigger { Kind = RoutineTriggerKind.Network, NetworkDirection = NetworkTriggerDirection.Disconnect };
        Assert.Equal(localization["Routine_NetworkAllNetworksDisconnected"], network.FormatSummary(localization));
        Assert.Equal(localization["Routine_RunManually"], (network with { NetworkDirection = NetworkTriggerDirection.Connect }).FormatSummary(localization));
        Assert.Contains("Office", (network with { NetworkName = "Office" }).FormatSummary(localization), StringComparison.Ordinal);
        Assert.Contains("office", (network with { NetworkName = "office" }).FormatSummary(localization), StringComparison.Ordinal);
    }
}
