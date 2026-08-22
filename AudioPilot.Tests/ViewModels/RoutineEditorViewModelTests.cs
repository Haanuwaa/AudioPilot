using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class RoutineEditorViewModelTests
{
    [Fact]
    public void PrimaryActionLabel_IsAdd_ForNewRoutine()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: "Routine 3", scheduleTimeZoneId: null);

        Assert.False(viewModel.IsEditingExistingRoutine);
        Assert.Equal("Add", viewModel.PrimaryActionLabel);
    }

    [Fact]
    public void PrimaryActionLabel_IsUpdate_ForExistingRoutine()
    {
        var viewModel = new RoutineEditorViewModel(
            [],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
            },
            scheduleTimeZoneId: null);

        Assert.True(viewModel.IsEditingExistingRoutine);
        Assert.Equal("Update", viewModel.PrimaryActionLabel);
    }

    [Fact]
    public void Constructor_UsesSuggestedName_ForNewRoutine()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: "Routine 3", scheduleTimeZoneId: null);

        Assert.Equal("Routine 3", viewModel.Name);
        Assert.Equal(0, viewModel.SelectedOutputIndex);
        Assert.Equal(0, viewModel.SelectedInputIndex);
    }

    [Fact]
    public void Constructor_FallsBackToRoutineOne_WhenSuggestionMissing()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: null, scheduleTimeZoneId: null);

        Assert.Equal("Routine 1", viewModel.Name);
    }

    [Fact]
    public void Name_TruncatesToThirtyFiveCharacters()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: null, scheduleTimeZoneId: null)
        {
            Name = "123456789012345678901234567890123456789"
        };

        Assert.Equal("12345678901234567890123456789012345", viewModel.Name);
    }

    [Fact]
    public void RoutineNameCharactersRemainingText_UpdatesAsNameChanges()
    {
        var viewModel = new RoutineEditorViewModel([], [], existingRoutine: null, suggestedName: null, scheduleTimeZoneId: null)
        {
            Name = "Routine"
        };

        Assert.Equal("28 characters remaining", viewModel.RoutineNameCharactersRemainingText);

        viewModel.Name = "AB";

        Assert.Equal("33 characters remaining", viewModel.RoutineNameCharactersRemainingText);
    }

    [Fact]
    public void BuildRoutine_ForNewRoutine_DefaultsEnabled_AndUsesSelectedTargets()
    {
        var outputDevices = new ObservableCollection<CycleDevice>
        {
            new() { Id = "out-1", Name = "Speakers" }
        };
        var inputDevices = new ObservableCollection<CycleDevice>
        {
            new() { Id = "in-1", Name = "Mic" }
        };

        var viewModel = new RoutineEditorViewModel(outputDevices, inputDevices, suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedInputIndex = 1,
        };
        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal("Routine 2", routine.Name);
        Assert.True(routine.Enabled);
        Assert.Equal("out-1", routine.OutputDeviceId);
        Assert.Equal("Speakers", routine.OutputDeviceName);
        Assert.Equal("in-1", routine.InputDeviceId);
        Assert.Equal("Mic", routine.InputDeviceName);
        Assert.Equal("Ctrl+Alt+R", routine.Hotkey);
    }

    [Fact]
    public void BuildRoutine_PersistsAppStartTrigger_ButClearsTrayMenu()
    {
        var viewModel = new RoutineEditorViewModel([new CycleDevice { Id = "out-1", Name = "Speakers" }], [], suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedInputIndex = 0,
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            ShowInTrayMenu = true,
            RestorePreviousAudioOnDeactivate = true,
        };

        viewModel.EditorHotkey.LoadFromString(string.Empty);

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.True(routine.UsesApplicationTrigger);
        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", routine.TriggerAppPath);
        Assert.True(routine.SwitchOutputPerApp);
        Assert.False(routine.ShowInTrayMenu);
        Assert.True(routine.RestorePreviousAudioOnDeactivate);
        Assert.Equal(string.Empty, routine.Hotkey);
    }

    [Fact]
    public void BuildRoutine_PersistsProcessFocusModeAndTitleMetadata()
    {
        RoutineEditorViewModel viewModel = new([new() { Id = "out-1", Name = "Speakers" }], [], suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            ApplicationTriggerTitlePattern = "playlist",
            SelectedApplicationTriggerMode = "When application window is focused",
            SelectedApplicationTriggerTitleMatchMode = "Regex (e.g., '.*Chrome.*')",
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.Equal(ApplicationTriggerMode.ProcessFocus, routine.ApplicationTriggerMode);
        Assert.Equal("playlist", routine.ApplicationTriggerTitlePattern);
        Assert.Equal(ApplicationTriggerTitleMatchMode.Regex, routine.ApplicationTriggerTitleMatchMode);
    }

    [Fact]
    public void BuildRoutine_PersistsVolumeTargets_WhenConfigured()
    {
        var viewModel = new RoutineEditorViewModel([new CycleDevice { Id = "out-1", Name = "Speakers" }], [], suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            MasterVolumePercentText = "60",
            MicVolumePercentText = "15",
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(60, routine.MasterVolumePercent);
        Assert.Equal(15, routine.MicVolumePercent);
    }

    [Fact]
    public void BuildRoutine_ConvertsScheduleTimeToConfiguredTimeZone()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2",
            scheduleTimeZoneId: "Pacific Standard Time")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Network",
            TriggerNetworkName = "Home WiFi",
            ScheduleTime = new TimeOnly(12, 0),
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal("Pacific Standard Time", routine.ScheduleTimeZoneId);
    }

    [Fact]
    public void Constructor_ConvertsScheduleTimeFromConfiguredTimeZoneToLocal()
    {
        var existingRoutine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Routine 1",
            Enabled = true,
            DisplayOrder = 1,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTime = new TimeOnly(12, 0),
            ScheduleTimeZoneId = "Pacific Standard Time",
        };

        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            existingRoutine,
            scheduleTimeZoneId: "Pacific Standard Time");

        var builtRoutine = viewModel.BuildRoutine();
        Assert.Equal("Pacific Standard Time", builtRoutine.ScheduleTimeZoneId);
    }

    [Fact]
    public void BuildRoutine_PresistsScheduleTimeZoneId()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2",
            scheduleTimeZoneId: "Pacific Standard Time")
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Network",
            TriggerNetworkName = "Home WiFi",
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal("Pacific Standard Time", routine.ScheduleTimeZoneId);
    }

    [Fact]
    public void Constructor_UsesLocalTimeZoneId_WhenScheduleTimeZoneIdIsNull()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2",
            scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(TimeZoneInfo.Local.Id, routine.ScheduleTimeZoneId);
    }

    [Fact]
    public void ScheduleTimeZoneDisplayName_FallsBackToLocalDisplay_WhenScheduleTimeZoneIdIsInvalid()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2",
            scheduleTimeZoneId: "Invalid/Timezone");

        Assert.Equal(TimeZoneDisplayFormatter.FormatCompact(TimeZoneInfo.Local), viewModel.ScheduleTimeZoneDisplayName);
    }

    [Fact]
    public void ScheduleEditor_UsesWindowsTwentyFourHourClockPreference()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.ShortTimePattern = "HH:mm";
        var viewModel = new RoutineEditorViewModel([], [], displayCulture: culture)
        {
            ScheduleTime = new TimeOnly(23, 5),
        };

        Assert.True(viewModel.Is24HourFormat);
        Assert.Equal(24, viewModel.HourOptions.Count);
        Assert.Equal("00", viewModel.HourOptions[0]);
        Assert.Equal("23", viewModel.HourOptions[23]);
        Assert.Equal(23, viewModel.ScheduleHourIndex);
    }

    [Fact]
    public void ScheduleEditor_AutomaticModeUsesLiveWindowsLocalePattern()
    {
        DateTimeFormatInfo dateTimeFormat = CultureInfo.CurrentCulture.DateTimeFormat;
        bool expected = RoutineEditorViewModel.GetHourFormat(
            WindowsTimeFormatPreference.GetCurrentTaskbarTimePattern(
                dateTimeFormat.ShortTimePattern,
                dateTimeFormat.LongTimePattern)).Is24Hour;

        var viewModel = new RoutineEditorViewModel([], []);

        Assert.Equal(expected, viewModel.Is24HourFormat);
    }

    [Fact]
    public void ScheduleEditor_UsesWindowsTwelveHourDesignators()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.ShortTimePattern = "h:mm tt";
        culture.DateTimeFormat.AMDesignator = "a.m.";
        culture.DateTimeFormat.PMDesignator = "p.m.";
        var viewModel = new RoutineEditorViewModel([], [], displayCulture: culture)
        {
            ScheduleTime = new TimeOnly(13, 5),
        };

        Assert.False(viewModel.Is24HourFormat);
        Assert.Equal(["12", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11"], viewModel.HourOptions);
        Assert.Equal(["a.m.", "p.m."], viewModel.AmPmOptions);
        Assert.Equal(1, viewModel.ScheduleHourIndex);
        Assert.Equal(1, viewModel.ScheduleAmPmIndex);
    }

    [Theory]
    [InlineData("HH:mm", true)]
    [InlineData("H:mm", true)]
    [InlineData("h:mm tt", false)]
    [InlineData("h 'H' mm", false)]
    [InlineData("h \\H mm", false)]
    public void GetHourFormat_IgnoresEscapedAndLiteralHourMarkers(string pattern, bool expected)
    {
        Assert.Equal(expected, RoutineEditorViewModel.GetHourFormat(pattern).Is24Hour);
    }

    [Theory]
    [InlineData("HH:mm", true)]
    [InlineData("H:mm", false)]
    [InlineData("hh:mm tt", true)]
    [InlineData("h:mm tt", false)]
    [InlineData("h 'HH' mm", false)]
    [InlineData("h \\HH mm", false)]
    public void GetHourFormat_RespectsHourTokenWidthAndLiterals(string pattern, bool expected)
    {
        Assert.Equal(expected, RoutineEditorViewModel.GetHourFormat(pattern).PadsHour);
    }

    [Fact]
    public void RefreshTimeFormat_PreservesScheduledTimeAndUpdatesVisibleClockConvention()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        var viewModel = new RoutineEditorViewModel([], [], displayCulture: culture)
        {
            ScheduleTime = new TimeOnly(13, 5),
        };
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.RefreshTimeFormat(culture, "HH:mm");

        Assert.True(viewModel.Is24HourFormat);
        Assert.Equal("00", viewModel.HourOptions[0]);
        Assert.Equal("01", viewModel.HourOptions[1]);
        Assert.Equal(13, viewModel.ScheduleHourIndex);
        Assert.Equal(new TimeOnly(13, 5), viewModel.ScheduleTime);
        Assert.Contains(nameof(RoutineEditorViewModel.Is24HourFormat), changedProperties);
        Assert.Contains(nameof(RoutineEditorViewModel.HourOptions), changedProperties);

        viewModel.RefreshTimeFormat(culture, "hh:mm tt");

        Assert.False(viewModel.Is24HourFormat);
        Assert.Equal(["12", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11"], viewModel.HourOptions);
        Assert.Equal(1, viewModel.ScheduleHourIndex);
        Assert.Equal(1, viewModel.ScheduleAmPmIndex);
        Assert.Equal(new TimeOnly(13, 5), viewModel.ScheduleTime);
    }

    [Fact]
    public void Validate_AllowsVolumeOnlyRoutineTargets()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            MasterVolumePercentText = "35",
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        string? validation = viewModel.Validate();

        Assert.Null(validation);
        AudioRoutine routine = viewModel.BuildRoutine();
        Assert.Equal(35, routine.MasterVolumePercent);
        Assert.True(routine.HasExecutionTarget);
    }

    [Fact]
    public void Validate_RequiresDeviceOrVolumeTarget()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4", scheduleTimeZoneId: null);

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        string? validation = viewModel.Validate();

        Assert.Equal("Choose an output device, input device, or volume target.", validation);
    }

    [Fact]
    public void Validate_RejectsInvalidVolumeTarget()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            MasterVolumePercentText = "150",
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        string? validation = viewModel.Validate();

        Assert.Equal("Volume targets must be whole numbers between 0 and 100.", validation);
    }

    [Fact]
    public void Constructor_ExpandsVolumeTargets_WhenExistingRoutineHasConfiguredLevels()
    {
        var viewModel = new RoutineEditorViewModel(
            [],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                MasterVolumePercent = 45,
            },
            scheduleTimeZoneId: null);

        Assert.True(viewModel.IsVolumeTargetsExpanded);
        Assert.Equal("45", viewModel.MasterVolumePercentText);
    }

    [Fact]
    public void SwitchOutputPerApp_RemainsSelectable_WhenOutputTargetIsRemoved()
    {
        var viewModel = new RoutineEditorViewModel([new CycleDevice { Id = "out-1", Name = "Speakers" }], [], suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
        };

        viewModel.SelectedOutputIndex = 0;

        Assert.True(viewModel.SwitchOutputPerApp);
        Assert.True(viewModel.CanSwitchOutputPerApp);
        Assert.False(viewModel.HasAudioTargetSelected);
    }

    [Fact]
    public void SwitchOutputPerApp_CanBeCheckedBeforeTarget_WhenAppStartupSelected()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true
        };

        Assert.True(viewModel.SwitchOutputPerApp);
        Assert.True(viewModel.CanSwitchOutputPerApp);
        Assert.False(viewModel.HasAudioTargetSelected);
        Assert.Equal("Application audio routing requires an Application trigger and at least one output or input device target.", viewModel.Validate());
    }

    [Fact]
    public void SwitchOutputPerApp_AllowsInputTarget()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [new CycleDevice { Id = "in-1", Name = "Microphone" }],
            suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedInputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true
        };

        Assert.Equal(1, viewModel.SelectedInputIndex);
        Assert.True(viewModel.HasAudioTargetSelected);
    }

    [Fact]
    public void SwitchOutputPerApp_SupportsInputOnlyTarget_WhenAppStartupSelected()
    {
        var viewModel = new RoutineEditorViewModel(
            [],
            [new CycleDevice { Id = "in-1", Name = "Microphone" }],
            suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 0,
            SelectedInputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true
        };

        Assert.True(viewModel.SwitchOutputPerApp);
        Assert.True(viewModel.CanSwitchOutputPerApp);
    }

    [Fact]
    public void Validate_RequiresHotkey()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Routine hotkey is required.", validation);
    }

    [Fact]
    public void Validate_RequiresHotkey_WhenHotkeyTriggerSelectedEvenIfTrayEnabled()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            ShowInTrayMenu = true,
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Routine hotkey is required.", validation);
    }

    [Fact]
    public void Validate_RejectsDuplicateRoutineHotkey()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4",
            reservedHotkeyKeys: ["Ctrl+Alt+R"],
            scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
        };

        viewModel.EditorHotkey.LoadFromString("Ctrl+Alt+R");

        string? validation = viewModel.Validate();

        Assert.Equal("Routine hotkey must be unique and cannot conflict with another app hotkey.", validation);
    }

    [Fact]
    public void Validate_AllowsExistingRoutineToKeepItsCurrentHotkey()
    {
        var existingRoutine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Routine 1",
            Enabled = true,
            DisplayOrder = 1,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            Hotkey = "Ctrl+Alt+R",
        };

        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            existingRoutine,
            reservedHotkeyKeys: ["Ctrl+Alt+R", "Ctrl+Alt+P"],
            scheduleTimeZoneId: null);

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void Validate_RequiresFullExePathWhenAppStartTriggerEnabled()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = "spotify",
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Application trigger requires a full .exe path or packaged app AUMID.", validation);
    }

    [Fact]
    public void Validate_AllowsPackagedAppAumid_WhenAppStartTriggerEnabled()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void ResolvedTriggerAppTargetText_UsesExecutableFileName_ForDesktopApp()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        Assert.True(viewModel.HasResolvedTriggerAppTarget);
        Assert.Equal("Resolved app: Spotify", viewModel.ResolvedTriggerAppTargetText);
    }

    [Fact]
    public void ResolvedTriggerAppTargetText_UsesResolvedPackagedAppDisplayName_WhenAvailable()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedTriggerMode = "Application",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
        };

        viewModel.SetResolvedPackagedAppDisplayName("Spotify");

        Assert.True(viewModel.HasResolvedTriggerAppTarget);
        Assert.Equal("Resolved app: Spotify", viewModel.ResolvedTriggerAppTargetText);
    }

    [Fact]
    public void ResolvedTriggerAppTargetText_IsHidden_WhenNotAppStartupTrigger()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedTriggerMode = "Hotkey",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        Assert.False(viewModel.HasResolvedTriggerAppTarget);
        Assert.Equal(string.Empty, viewModel.ResolvedTriggerAppTargetText);
    }

    [Fact]
    public void Validate_AllowsPerAppRouting_ForPackagedAppTarget()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            SwitchOutputPerApp = true,
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void Validate_AllowsAppStartupTriggerWithoutHotkey()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void BuildRoutine_PreservesPackagedAppAumidTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Application",
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            ShowInTrayMenu = true,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", routine.TriggerAppPath);
        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.True(routine.UsesApplicationTrigger);
    }

    [Fact]
    public void Constructor_PreservesStatefulFlags_ForExistingAppStartRoutine()
    {
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                RestorePreviousAudioOnDeactivate = true,
            },
            scheduleTimeZoneId: null);

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.True(routine.RestorePreviousAudioOnDeactivate);
    }

    [Fact]
    public void BuildRoutine_SupportsSteamBigPictureTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Steam Big Picture",
            RestorePreviousAudioOnDeactivate = true,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.SteamBigPicture, routine.TriggerKind);
        Assert.False(routine.UsesApplicationTrigger);
        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.True(routine.RestorePreviousAudioOnDeactivate);
    }

    [Fact]
    public void BuildRoutine_SupportsDeviceChangeTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Device change",
            ShowInTrayMenu = true,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.DeviceChange, routine.TriggerKind);
        Assert.False(routine.UsesApplicationTrigger);
        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
        Assert.True(routine.EnforceTargetsOnDeviceChange);
    }

    [Fact]
    public void BuildRoutine_SupportsAudioPilotStartupTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 2", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "AudioPilot startup",
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            RestorePreviousAudioOnDeactivate = true,
            ShowInTrayMenu = true,
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.AudioPilotStartup, routine.TriggerKind);
        Assert.False(routine.UsesApplicationTrigger);
        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.False(routine.SwitchOutputPerApp);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
        Assert.False(routine.ShowInTrayMenu);
    }

    [Fact]
    public void BuildRoutine_SupportsWifiTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 3", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Network",
            TriggerNetworkName = "Home WiFi",
        };

        AudioRoutine routine = viewModel.BuildRoutine();

        Assert.Equal(RoutineTriggerKind.Network, routine.TriggerKind);
        Assert.Equal("Home WiFi", routine.TriggerNetworkName);
        Assert.Equal(string.Empty, routine.TriggerAppPath);
        Assert.False(routine.RestorePreviousAudioOnDeactivate);
    }

    [Fact]
    public void Validate_AllowsAudioPilotStartupTriggerWithoutAppPath()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "AudioPilot startup",
        };

        string? validation = viewModel.Validate();

        Assert.Null(validation);
    }

    [Fact]
    public void Validate_RequiresWifiSsid_ForWifiTrigger()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 5", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            SelectedTriggerMode = "Network",
            TriggerNetworkName = string.Empty,
        };

        string? validation = viewModel.Validate();

        Assert.Equal("Network trigger requires a network name when direction is Connect or Both.", validation);
    }

    [Fact]
    public void Constructor_PreservesAudioPilotStartupTrigger_ForExistingRoutine()
    {
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                ShowInTrayMenu = true,
            },
            scheduleTimeZoneId: null);

        Assert.Equal("AudioPilot startup", viewModel.SelectedTriggerMode);
        Assert.True(viewModel.IsAudioPilotStartupTriggerSelected);
        Assert.False(viewModel.IsStatefulTriggerSelected);
        Assert.False(viewModel.SupportsTrayMenuTrigger);
        Assert.False(viewModel.ShowInTrayMenu);
    }

    [Fact]
    public void Constructor_PreservesWifiTrigger_ForExistingRoutine()
    {
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            new AudioRoutine
            {
                Id = "routine-2",
                Name = "Routine WiFi",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "Office WiFi",
            },
            scheduleTimeZoneId: null);

        Assert.Equal("Network", viewModel.SelectedTriggerMode);
        Assert.True(viewModel.IsNetworkTriggerSelected);
        Assert.Equal("Office WiFi", viewModel.TriggerNetworkName);
        Assert.False(viewModel.SupportsTrayMenuTrigger);
    }

    [Fact]
    public void SelectedTriggerMode_Network_ReusesLoadedNetworksWithoutForcingRefresh()
    {
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine WiFi",
            scheduleTimeZoneId: null,
            preloadNetworks: false);

        viewModel.AvailableNetworkNames.Add("Office WiFi");
        typeof(RoutineEditorViewModel)
            .GetField("_networksLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, true);

        viewModel.SelectedTriggerMode = "Network";

        Assert.True(viewModel.IsNetworkTriggerSelected);
        Assert.False(viewModel.IsScanningNetworks);
        Assert.Equal("Office WiFi", Assert.Single(viewModel.AvailableNetworkNames));
    }

    [Fact]
    public async Task SelectedTriggerMode_Network_ForceRefreshesWhenCurrentNetworkMissingFromCache()
    {
        int loadCalls = 0;
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine WiFi",
            scheduleTimeZoneId: null,
            preloadNetworks: false,
            loadAvailableNetworkNamesAsync: _ =>
            {
                loadCalls++;
                return Task.FromResult<IReadOnlyList<string>>(["Guest WiFi", "Home WiFi"]);
            });

        viewModel.AvailableNetworkNames.Add("Office WiFi");
        SetNetworksLoaded(viewModel, true);
        viewModel.TriggerNetworkName = "Home WiFi";

        viewModel.SelectedTriggerMode = "Network";
        await WaitForConditionAsync(() => !viewModel.IsScanningNetworks);

        Assert.Equal(1, loadCalls);
        Assert.Equal(["Guest WiFi", "Home WiFi"], viewModel.AvailableNetworkNames);
        Assert.Equal("Home WiFi", viewModel.TriggerNetworkName);
    }

    [Fact]
    public async Task RefreshNetworksAsync_PreservesTypedNetworkNameWhileRefreshIsInFlight()
    {
        var refreshCompletion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine WiFi",
            scheduleTimeZoneId: null,
            preloadNetworks: false,
            loadAvailableNetworkNamesAsync: _ => refreshCompletion.Task);

        viewModel.AvailableNetworkNames.Add("Office WiFi");
        SetNetworksLoaded(viewModel, true);
        viewModel.TriggerNetworkName = "Office WiFi";
        Assert.Equal("Office WiFi", viewModel.SelectedAvailableNetworkName);

        Task refreshTask = viewModel.RefreshNetworksAsync(forceRefresh: true);
        await WaitForConditionAsync(() => viewModel.IsScanningNetworks);

        Assert.Equal("Office WiFi", viewModel.TriggerNetworkName);
        Assert.Equal("Office WiFi", viewModel.SelectedAvailableNetworkName);
        Assert.Equal(["Office WiFi"], viewModel.AvailableNetworkNames);

        refreshCompletion.SetResult(["Home WiFi", "Office WiFi"]);
        await refreshTask;

        Assert.Equal("Office WiFi", viewModel.TriggerNetworkName);
        Assert.Equal("Office WiFi", viewModel.SelectedAvailableNetworkName);
        Assert.Equal(["Home WiFi", "Office WiFi"], viewModel.AvailableNetworkNames);
    }

    [Fact]
    public async Task RefreshNetworksAsync_CancelsStaleRefreshAndAppliesLatestResult()
    {
        int loadCalls = 0;
        var firstRefreshReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new RoutineEditorViewModel(
            [new() { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine WiFi",
            scheduleTimeZoneId: null,
            preloadNetworks: false,
            loadAvailableNetworkNamesAsync: async cancellationToken =>
            {
                loadCalls++;
                if (loadCalls == 1)
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        static state => ((TaskCompletionSource)state!).TrySetResult(),
                        firstRefreshReleased);
                    await firstRefreshReleased.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                    return ["Stale WiFi"];
                }

                return ["Fresh WiFi"];
            });

        Task firstRefresh = viewModel.RefreshNetworksAsync(forceRefresh: true);
        await WaitForConditionAsync(() => viewModel.IsScanningNetworks);

        await viewModel.RefreshNetworksAsync(forceRefresh: true);
        await WaitForConditionAsync(() => loadCalls >= 2 && !viewModel.IsScanningNetworks);
        await firstRefresh;

        Assert.Equal(2, loadCalls);
        Assert.Equal(["Fresh WiFi"], viewModel.AvailableNetworkNames);
    }

    [Fact]
    public async Task Dispose_DuringPendingNetworkRefresh_DoesNotStartReplacementScan()
    {
        int loadCalls = 0;
        var firstRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new RoutineEditorViewModel(
            [],
            [],
            preloadNetworks: false,
            loadAvailableNetworkNamesAsync: async cancellationToken =>
            {
                Interlocked.Increment(ref loadCalls);
                firstRefreshStarted.TrySetResult();
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    cancellationObserved);
                await releaseFirstRefresh.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return [];
            });

        Task firstRefresh = viewModel.RefreshNetworksAsync(forceRefresh: true);
        await firstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await viewModel.RefreshNetworksAsync(forceRefresh: true);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        viewModel.Dispose();
        releaseFirstRefresh.TrySetResult();
        await firstRefresh.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref loadCalls));
        Assert.False(viewModel.IsScanningNetworks);

        await viewModel.RefreshNetworksAsync(forceRefresh: true);
        Assert.Equal(1, Volatile.Read(ref loadCalls));
    }

    private static void SetNetworksLoaded(RoutineEditorViewModel viewModel, bool loaded)
    {
        typeof(RoutineEditorViewModel)
            .GetField("_networksLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, loaded);
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        await TestExecutionGuards.WaitUntilAsync(
            predicate,
            "Condition was not reached within the allotted time.",
            timeout: TimeSpan.FromMilliseconds(timeoutMs),
            pollInterval: TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public void AutomaticTriggerModes_DoNotSupportTrayMenu()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            ShowInTrayMenu = true,
            SelectedTriggerMode = "Application",
        };

        Assert.False(viewModel.SupportsTrayMenuTrigger);
        Assert.False(viewModel.ShowInTrayMenu);

        viewModel.SelectedTriggerMode = "Device change";
        Assert.False(viewModel.SupportsTrayMenuTrigger);

        viewModel.SelectedTriggerMode = "Steam Big Picture";
        Assert.False(viewModel.SupportsTrayMenuTrigger);
    }

    [Fact]
    public void SelectedTriggerMode_ClearsTrayMenu_ForAudioPilotStartup()
    {
        var viewModel = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [],
            suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            SelectedOutputIndex = 1,
            ShowInTrayMenu = true,
            SelectedTriggerMode = "AudioPilot startup",
        };

        Assert.False(viewModel.SupportsTrayMenuTrigger);
        Assert.False(viewModel.ShowInTrayMenu);
    }

    [Fact]
    public void TriggerAppPath_PreservesTypedPeriodWhileEditing()
    {
        var viewModel = new RoutineEditorViewModel([], [], suggestedName: "Routine 4", scheduleTimeZoneId: null)
        {
            TriggerAppPath = @"C:\Apps\Spotify\Spotify."
        };

        Assert.Equal(@"C:\Apps\Spotify\Spotify.", viewModel.TriggerAppPath);
    }

}
