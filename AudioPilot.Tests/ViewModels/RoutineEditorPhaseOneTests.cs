using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class RoutineEditorPhaseOneTests
{
    [Fact]
    public void ActiveTriggerDraft_IsolatesEditsAndAvoidsSummaryChurnPerKeystroke()
    {
        using var editor = new RoutineEditorViewModel([], [], new AudioRoutine
        {
            Name = "Desk",
            MasterVolumePercent = 50,
            Triggers = [new() { Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Game.exe" }],
            RestorePreviousAudioOnDeactivate = true,
        }, preloadNetworks: false);

        editor.EditAutomaticTrigger(Assert.Single(editor.TriggerEntries));
        Assert.NotNull(editor.ActiveTriggerDraft);

        int summaryNotifications = 0;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(editor.EditorSummary)) summaryNotifications++;
        };

        editor.ActiveTriggerDraft!.SelectedTriggerMode = "Network";
        editor.ActiveTriggerDraft.TriggerNetworkName = "Office";
        editor.ActiveTriggerDraft.TriggerNetworkName = "Office 5G";

        Assert.Equal(0, summaryNotifications);
        Assert.Equal(RoutineTriggerKind.Application, Assert.Single(editor.TriggerEntries).Trigger.Kind);
        Assert.True(editor.RestorePreviousAudioOnDeactivate);

        editor.CancelTriggerEdit();
        Assert.Null(editor.ActiveTriggerDraft);
        Assert.Equal(1, summaryNotifications);
        Assert.True(editor.RestorePreviousAudioOnDeactivate);
        Assert.Equal(@"C:\Apps\Game.exe", Assert.Single(editor.TriggerEntries).Trigger.AppPath);
    }

    [Fact]
    public void ConditionTimePickers_SyncBidirectionallyAcross12And24HourFormats()
    {
        using var editor = new RoutineEditorViewModel([], [], displayCulture: CultureInfo.GetCultureInfo("en-US"), preloadNetworks: false);
        editor.RequireTimeWindow = true;

        editor.RefreshTimeFormat(CultureInfo.GetCultureInfo("en-US"), "h:mm tt");
        Assert.False(editor.Is24HourFormat);

        editor.ConditionStartHourIndex = 8;
        editor.ConditionStartMinuteIndex = 30;
        editor.ConditionStartAmPmIndex = 0;
        editor.ConditionEndHourIndex = 6;
        editor.ConditionEndMinuteIndex = 15;
        editor.ConditionEndAmPmIndex = 1;

        RoutineTimeWindow window = editor.BuildRoutine().Conditions.TimeWindow!;
        Assert.Equal(new TimeOnly(8, 30), window.Start);
        Assert.Equal(new TimeOnly(18, 15), window.End);

        editor.RefreshTimeFormat(CultureInfo.GetCultureInfo("de-DE"), "HH:mm");
        Assert.True(editor.Is24HourFormat);
        Assert.Equal(8, editor.ConditionStartHourIndex);
        Assert.Equal(18, editor.ConditionEndHourIndex);

        editor.ConditionEndHourIndex = 22;
        editor.ConditionEndMinuteIndex = 45;
        Assert.Equal("22:45", editor.ConditionEnd);
        Assert.Equal(new TimeOnly(22, 45), editor.BuildRoutine().Conditions.TimeWindow!.End);
    }

    [Fact]
    public void DayPresets_ApplyEveryDayWeekdaysAndWeekendsToScheduleAndConditions()
    {
        using var editor = new RoutineEditorViewModel([], [], preloadNetworks: false);
        editor.AddAutomaticTrigger();
        var draft = editor.ActiveTriggerDraft!;
        draft.SelectedTriggerMode = "Scheduled";

        draft.ApplyScheduleDayPreset(DaySelectionPreset.Weekdays);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], draft.ScheduleDays.Order());
        Assert.False(draft.IsDailySchedule);

        draft.ApplyScheduleDayPreset(DaySelectionPreset.Weekends);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], draft.ScheduleDays.Order());

        draft.ApplyScheduleDayPreset(DaySelectionPreset.EveryDay);
        Assert.Empty(draft.ScheduleDays);
        Assert.True(draft.IsDailySchedule);

        editor.ApplyConditionDayPreset(DaySelectionPreset.Weekdays);
        Assert.True(editor.ConditionMonday && editor.ConditionFriday);
        Assert.False(editor.ConditionSunday || editor.ConditionSaturday);

        editor.ApplyConditionDayPreset(DaySelectionPreset.Weekends);
        Assert.True(editor.ConditionSunday && editor.ConditionSaturday);
        Assert.False(editor.ConditionMonday || editor.ConditionFriday);

        editor.ApplyConditionDayPreset(DaySelectionPreset.EveryDay);
        Assert.False(editor.ConditionSunday || editor.ConditionMonday || editor.ConditionSaturday);
    }

    [Fact]
    public void HasUnsavedChanges_TracksDirtyStateAndResetsWhenReverted()
    {
        using var editor = new RoutineEditorViewModel([], [], new AudioRoutine
        {
            Name = "Desk",
            MasterVolumePercent = 50,
            ShowInTrayMenu = true,
        }, preloadNetworks: false);

        Assert.False(editor.HasUnsavedChanges);

        editor.MasterVolumePercentText = "65";
        Assert.True(editor.HasUnsavedChanges);

        editor.MasterVolumePercentText = "50";
        Assert.False(editor.HasUnsavedChanges);

        editor.Name = "Renamed";
        Assert.True(editor.HasUnsavedChanges);
    }

    [Fact]
    public void RunningApplicationDiscovery_DeduplicatesPrefersVisibleWindowsAndFiltersNoise()
    {
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.AddRange(
        [
            new(100, @"C:\Apps\AudioPilot\AudioPilot.exe", StartTimeUtcTicks: 10),
            new(200, @"C:\Apps\Browser\chrome.exe", StartTimeUtcTicks: 20),
            new(201, @"C:\Apps\Browser\chrome.exe", StartTimeUtcTicks: 21),
            new(300, Path.Combine(windowsDir, "System32", "svchost.exe"), StartTimeUtcTicks: 30),
            new(400, @"C:\Apps\Spotify\Spotify.exe", StartTimeUtcTicks: 40),
            new(500, @"C:\Program Files\WindowsApps\App\Player.exe", AppUserModelId: "Example.Media_1234567890abc!Player", StartTimeUtcTicks: 50),
        ]);

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> windows = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [201] = [new("AudioPilot - GitHub - Google Chrome", "Chrome_WidgetWin_1")],
            [500] = [new("Music Player", "ApplicationFrameWindow")],
        };

        IReadOnlyList<RunningAppIdentity> discovered = RunningApplicationDiscovery.Discover(
            provider,
            visibleWindowReader: _ => windows,
            fileDescriptionReader: pid => pid switch
            {
                200 or 201 => "Google Chrome",
                400 => "Spotify",
                _ => null,
            },
            aumidDisplayNameReader: aumid => aumid == "Example.Media_1234567890abc!Player" ? "Music Player" : null,
            currentProcessId: 100);

        Assert.Equal(3, discovered.Count);
        Assert.Equal("Google Chrome", discovered[0].DisplayName);
        Assert.Equal(201, discovered[0].ProcessId);
        Assert.Equal(@"C:\Apps\Browser\chrome.exe", discovered[0].TargetPath);
        Assert.Equal("Music Player", discovered[1].DisplayName);
        Assert.True(discovered[1].IsPackagedApp);
        Assert.Equal("Example.Media_1234567890abc!Player", discovered[1].TargetPath);
        Assert.Equal("Spotify", discovered[2].DisplayName);
        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", discovered[2].TargetPath);
    }

    [Fact]
    public void RunningAppPickerViewModel_FiltersByNameWindowTitleAndPathAndConfirmsSelection()
    {
        var vm = new RunningAppPickerViewModel(
        [
            new("Google Chrome", @"C:\Apps\Chrome\chrome.exe", @"C:\Apps\Chrome\chrome.exe", 10, 1, false, "YouTube"),
            new("Discord", @"C:\Apps\Discord\Discord.exe", @"C:\Apps\Discord\Discord.exe", 20, 2, false, "Voice Chat"),
        ]);

        Assert.Equal("2 apps", AudioPilot.Localization.LocalizationService.English.Format(vm.FilteredAppCountText));
        vm.TrySelectTargetPath(@"C:\Apps\Discord\Discord.exe");
        Assert.True(vm.CanConfirmSelection);
        Assert.Equal("Discord", vm.SelectedApp?.DisplayName);

        vm.SearchText = "youtube";
        var filtered = Assert.Single(vm.FilteredApps);
        Assert.Equal("Google Chrome", filtered.DisplayName);
        Assert.False(vm.CanConfirmSelection);

        vm.SelectedApp = filtered;
        Assert.True(vm.ConfirmSelection());
        Assert.Equal(@"C:\Apps\Chrome\chrome.exe", vm.ConfirmedTargetPath);
        Assert.Equal("Google Chrome", vm.ConfirmedDisplayName);
    }

    [Fact]
    public Task RoutineEditorWindow_ConfirmDiscardChangesPromptsOnlyWhenDirty() =>
        SharedStaDispatcherHost.RunAsync(async () =>
        {
            TestExecutionGuards.EnsureSharedWpfApplication();
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            ResourceDictionary resources = new()
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter(),
                ["EditableTextContextMenu"] = new ContextMenu(),
                ["AppMenuDeleteIcon"] = new object(),
            };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative),
            });
            RoutineEditorWindow? window = null;
            using var model = new RoutineEditorViewModel([], [], new AudioRoutine { Name = "Desk", MasterVolumePercent = 40 }, preloadNetworks: false);
            var dialogs = new RecordingAppDialogService { YesNoResponse = AppDialogResult.Declined };
            try
            {
                application.Resources = resources;
                window = new RoutineEditorWindow(model, dialogService: dialogs);
                Assert.True(await window.ConfirmDiscardChangesAsync());
                Assert.Empty(dialogs.Requests);

                model.Name = "Edited desk";
                Assert.False(await window.ConfirmDiscardChangesAsync());
                var request = Assert.Single(dialogs.Requests);
                Assert.Equal(DialogText.Captions.UnsavedChanges, request.Caption);

                dialogs.YesNoResponse = AppDialogResult.Confirmed;
                Assert.True(await window.ConfirmDiscardChangesAsync());
                Assert.Equal(2, dialogs.Requests.Count);
            }
            finally
            {
                window?.Close();
                application.Resources = originalResources;
            }
        });
}
