using System.Diagnostics;
using System.Threading.Channels;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppHotkeyRegistrationCoordinatorTests : IDisposable
{
    private static readonly TimeSpan HotkeyActivationTimeout = TimeSpan.FromSeconds(10);
    private long _utcNowTicks = TimeSpan.TicksPerSecond;
    private FakeKeyboardHotkeyMessageHost _messageHost = null!;
    private readonly HotkeyService _hotkeys;

    public AppHotkeyRegistrationCoordinatorTests()
    {
        _hotkeys = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => _messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
        _hotkeys.InitializeInfrastructure();
    }

    [Fact]
    public void SwitchHotkeyRegistrationResult_HasFailures_ReflectsAnyFailure()
    {
        var allSucceeded = new SwitchHotkeyRegistrationResult(
            OutputSwitchRegistered: true,
            InputSwitchRegistered: true,
            OutputReverseSwitchRegistered: true,
            InputReverseSwitchRegistered: true);

        var partialFailure = new SwitchHotkeyRegistrationResult(
            OutputSwitchRegistered: true,
            InputSwitchRegistered: false,
            OutputReverseSwitchRegistered: true,
            InputReverseSwitchRegistered: true);

        Assert.False(allSucceeded.HasFailures);
        Assert.True(partialFailure.HasFailures);
    }

    [Fact]
    public void HotkeyRegistrationResult_FailedCount_AndSwitchResult_AggregateCorrectly()
    {
        var result = new HotkeyRegistrationResult(
            ToggleAppVisibilityRegistered: false,
            MediaHotkeysRegistered: true,
            MuteHotkeysRegistered: false,
            ListenToInputRegistered: true,
            VolumeStepHotkeysRegistered: false,
            OutputSwitchRegistered: true,
            InputSwitchRegistered: false,
            OutputReverseSwitchRegistered: true,
            InputReverseSwitchRegistered: true);

        Assert.Equal(4, result.FailedCount);

        var switchResult = result.SwitchResult;
        Assert.True(switchResult.HasFailures);
        Assert.True(switchResult.OutputSwitchRegistered);
        Assert.False(switchResult.InputSwitchRegistered);
        Assert.True(switchResult.OutputReverseSwitchRegistered);
        Assert.True(switchResult.InputReverseSwitchRegistered);
    }

    [Fact]
    public async Task RegisterRoutineHotkeys_SkipsDuplicateRoutineHotkeys_AndRegistersUniqueHotkeys()
    {
        var coordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, Logger.Instance);
        var activations = Channel.CreateUnbounded<string>();
        var routines = new[]
        {
            new AudioRoutine
            {
                Id = "routine-2",
                Name = "Second",
                Enabled = true,
                DisplayOrder = 2,
                OutputDeviceId = "out-2",
                Hotkey = "Ctrl+Alt+R"
            },
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "First",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                Hotkey = "Ctrl+Alt+R"
            }
        };

        RoutineHotkeyRegistrationResult result = coordinator.RegisterRoutineHotkeys(routines, routine => activations.Writer.TryWrite(routine.Id));

        Assert.Equal(1, result.RegisteredGroupCount);
        Assert.Equal(1, result.FailedGroupCount);
        Assert.Equal(2, result.ActiveRoutineCount);

        InvokeHotkeyById(10000);
        string firstActivation = await activations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(HotkeyActivationTimeout, TestContext.Current.CancellationToken);

        InvokeHotkeyById(10000);
        _utcNowTicks += AppConstants.Timing.HotkeyDebounceTicks + TimeSpan.TicksPerMillisecond;
        InvokeHotkeyById(10000);
        string secondActivation = await activations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(HotkeyActivationTimeout, TestContext.Current.CancellationToken);

        Assert.Equal("routine-1", firstActivation);
        Assert.Equal("routine-1", secondActivation);
    }

    [Fact]
    public async Task RegisterRoutineHotkeys_ReplacesPreviousRegistrations_WhenCalledAgain()
    {
        var coordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, Logger.Instance);
        var activations = Channel.CreateUnbounded<string>();

        coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-old",
                    Name = "Old",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "Ctrl+Alt+R"
                }
            ],
            routine => activations.Writer.TryWrite(routine.Id));

        coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-new",
                    Name = "New",
                    Enabled = true,
                    DisplayOrder = 1,
                    InputDeviceId = "in-1",
                    Hotkey = "Ctrl+Alt+T"
                }
            ],
            routine => activations.Writer.TryWrite(routine.Id));

        InvokeHotkeyById(10000);
        string activation = await activations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(HotkeyActivationTimeout, TestContext.Current.CancellationToken);

        Assert.Equal("routine-new", activation);
    }

    [Fact]
    public void RegisterRoutineHotkeys_AssignsDistinctAttemptedIds_WhenEarlierRegistrationsFail()
    {
        var coordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, Logger.Instance);

        bool appHotkeyRegistered = _hotkeys.RegisterOutputSwitchHotkey("Ctrl+Alt+R");
        Assert.True(appHotkeyRegistered);

        RoutineHotkeyRegistrationResult result = coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-alpha",
                    Name = "Alpha",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "Ctrl+Alt+R"
                },
                new AudioRoutine
                {
                    Id = "routine-beta",
                    Name = "Beta",
                    Enabled = true,
                    DisplayOrder = 2,
                    OutputDeviceId = "out-2",
                    Hotkey = "Ctrl+Alt+T"
                }
            ],
            static _ => { });

        Assert.Equal(2, result.AttemptedHotkeyIdsByRoutineId.Count);
        Assert.Equal(10000, result.AttemptedHotkeyIdsByRoutineId["routine-alpha"]);
        Assert.Equal(10001, result.AttemptedHotkeyIdsByRoutineId["routine-beta"]);
        Assert.Equal(HotkeyRegistrationOutcomeKind.Duplicate, _hotkeys.GetLastRegistrationOutcome(10000).Kind);
        Assert.Equal(HotkeyRegistrationOutcomeKind.Registered, _hotkeys.GetLastRegistrationOutcome(10001).Kind);
    }

    [Fact]
    public void RegisterAll_WithFullReset_RecreatesUnchangedRoutineHotkeys()
    {
        var coordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, Logger.Instance);
        var routine = new AudioRoutine
        {
            Id = "resume-routine",
            Name = "Resume routine",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Hotkey,
            OutputDeviceId = "out-1",
            Hotkey = "Ctrl+Alt+F10"
        };

        RoutineHotkeyRegistrationResult initial = coordinator.RegisterRoutineHotkeys([routine], static _ => { });
        int hotkeyId = initial.AttemptedHotkeyIdsByRoutineId[routine.Id];
        Assert.Equal(HotkeyRegistrationOutcomeKind.Registered, _hotkeys.GetLastRegistrationOutcome(hotkeyId).Kind);

        coordinator.RegisterAll(new Settings(), unregisterAllFirst: true);
        Assert.Equal(HotkeyRegistrationOutcomeKind.None, _hotkeys.GetLastRegistrationOutcome(hotkeyId).Kind);

        RoutineHotkeyRegistrationResult restored = coordinator.RegisterRoutineHotkeys([routine], static _ => { });

        Assert.Equal(1, restored.RegisteredGroupCount);
        Assert.Equal(0, restored.FailedGroupCount);
        Assert.Equal(HotkeyRegistrationOutcomeKind.Registered, _hotkeys.GetLastRegistrationOutcome(hotkeyId).Kind);
    }

    [Fact]
    public void RegisterRoutineHotkeys_IgnoresDisabledAndInvalidRoutines_InActiveCounts()
    {
        var coordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, Logger.Instance);

        RoutineHotkeyRegistrationResult result = coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "enabled-valid",
                    Name = "Valid",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "Ctrl+Alt+Y"
                },
                new AudioRoutine
                {
                    Id = "enabled-invalid",
                    Name = "Invalid",
                    Enabled = true,
                    DisplayOrder = 2,
                    OutputDeviceId = "out-2",
                    Hotkey = "Ctrl+NotAKey"
                },
                new AudioRoutine
                {
                    Id = "disabled-valid",
                    Name = "Disabled",
                    Enabled = false,
                    DisplayOrder = 3,
                    OutputDeviceId = "out-3",
                    Hotkey = "Ctrl+Alt+U"
                }
            ],
            _ => { });

        Assert.Equal(1, result.RegisteredGroupCount);
        Assert.Equal(0, result.FailedGroupCount);
        Assert.Equal(2, result.ActiveRoutineCount);
    }

    [Fact]
    public void RegisterRoutineHotkeys_RedactsHotkeyAndRoutineIdentifiers_InFailureLogs()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppHotkeyRegistrationCoordinatorTests), "routine-hotkey-registration.log", LogLevel.Warning);
        var coordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, loggerScope.Logger);

        _ = coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-alpha",
                    Name = "Alpha",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "Ctrl+Alt+R"
                },
                new AudioRoutine
                {
                    Id = "routine-beta",
                    Name = "Beta",
                    Enabled = true,
                    DisplayOrder = 2,
                    OutputDeviceId = "out-2",
                    Hotkey = "Ctrl+Alt+R"
                }
            ],
            static _ => { });

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains($"hotkey={LogPrivacy.Label("Ctrl+Alt+R")}", logText, StringComparison.Ordinal);
        Assert.Contains($"routineId={LogPrivacy.Id("routine-beta")}", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("hotkey=Ctrl+Alt+R", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("routineId=routine-beta", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterRoutineHotkeys_LogsDegradedDeliverySummary_WhenFallbackIsUsed()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppHotkeyRegistrationCoordinatorTests), "routine-hotkey-fallback.log", LogLevel.Info);
        using var hotkeys = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(loggerScope.Logger, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { });
        TestPrivateAccess.SetField(hotkeys, "_logger", loggerScope.Logger);
        hotkeys.InitializeInfrastructure();
        var coordinator = new AppHotkeyRegistrationCoordinator(hotkeys, loggerScope.Logger);

        RoutineHotkeyRegistrationResult result = coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-alpha",
                    Name = "Alpha",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "F8"
                },
                new AudioRoutine
                {
                    Id = "routine-beta",
                    Name = "Beta",
                    Enabled = true,
                    DisplayOrder = 2,
                    OutputDeviceId = "out-2",
                    Hotkey = "F9"
                }
            ],
            static _ => { });

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(2, result.RegisteredGroupCount);
        Assert.Equal(0, result.FailedGroupCount);
        Assert.Contains("hotkey-register-group-delivery", logText, StringComparison.Ordinal);
        Assert.Contains("group=routine", logText, StringComparison.Ordinal);
        Assert.Contains("hookOnlyFallback=2", logText, StringComparison.Ordinal);
        Assert.Contains("degraded=true", logText, StringComparison.Ordinal);
        Assert.Contains("hybrid=0", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterChangedGlobalHotkeys_ReRegistersOnlyChangedGroup()
    {
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var hotkeys = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
        hotkeys.InitializeInfrastructure();
        var coordinator = new AppHotkeyRegistrationCoordinator(hotkeys, Logger.Instance);

        Settings initial = new()
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O",
                    ReverseSwitchHotkey = "Ctrl+Shift+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+I",
                    ReverseSwitchHotkey = "Ctrl+Shift+I"
                }
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+H"
                },
                Media = new HotkeysMediaSettings
                {
                    PlayPause = "Ctrl+Alt+P",
                    NextTrack = "Ctrl+Alt+J",
                    PreviousTrack = "Ctrl+Alt+K"
                },
                Mute = new HotkeysMuteSettings
                {
                    Mic = "Ctrl+Alt+M",
                    Sound = "Ctrl+Alt+U",
                    Deafen = "Ctrl+Alt+G"
                },
                Listen = new HotkeysListenSettings
                {
                    ListenToInput = "Ctrl+Alt+L"
                },
                Volume = new HotkeysVolumeSettings
                {
                    MasterUp = "Ctrl+Shift+D1",
                    MasterDown = "Ctrl+Shift+D2",
                    MicUp = "Ctrl+Shift+D3",
                    MicDown = "Ctrl+Shift+D4"
                }
            }
        };

        HotkeyRegistrationResult initialResult = coordinator.RegisterAll(initial);
        Assert.NotNull(messageHost);
        Assert.Equal(16, messageHost!.RegisterCount);
        Assert.Equal(0, messageHost.UnregisterCount);
        Assert.Equal(0, initialResult.FailedCount);
        messageHost.ResetCounts();

        Settings updated = AppSettingsWorkflowCoordinator.CloneSettings(initial);
        updated.Hotkeys.App.ToggleAppVisibility = "Ctrl+Alt+Y";

        HotkeyRegistrationResult result = coordinator.RegisterChangedGlobalHotkeys(initial, updated);

        Assert.Equal(1, messageHost.RegisterCount);
        Assert.Equal(1, messageHost.UnregisterCount);
        Assert.True(result.ToggleAppVisibilityAttempted);
        Assert.False(result.MediaHotkeysAttempted);
        Assert.False(result.MuteHotkeysAttempted);
        Assert.False(result.ListenToInputAttempted);
        Assert.False(result.VolumeStepHotkeysAttempted);
        Assert.False(result.OutputSwitchAttempted);
        Assert.False(result.InputSwitchAttempted);
        Assert.False(result.OutputReverseSwitchAttempted);
        Assert.False(result.InputReverseSwitchAttempted);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void RegisterChangedGlobalHotkeys_ReRegistersMediaGroup_WhenShowCurrentTrackBindingChanges()
    {
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var hotkeys = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
        hotkeys.InitializeInfrastructure();
        var coordinator = new AppHotkeyRegistrationCoordinator(hotkeys, Logger.Instance);

        Settings initial = new()
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = string.Empty
                },
                Media = new HotkeysMediaSettings
                {
                    ShowCurrentTrack = string.Empty,
                    PlayPause = "Ctrl+Alt+P",
                    NextTrack = "Ctrl+Alt+J",
                    PreviousTrack = "Ctrl+Alt+K"
                }
            }
        };

        HotkeyRegistrationResult initialResult = coordinator.RegisterAll(initial);
        Assert.NotNull(messageHost);
        Assert.Equal(3, messageHost!.RegisterCount);
        Assert.Equal(0, messageHost.UnregisterCount);
        Assert.Equal(0, initialResult.FailedCount);
        messageHost.ResetCounts();

        Settings updated = AppSettingsWorkflowCoordinator.CloneSettings(initial);
        updated.Hotkeys.Media.ShowCurrentTrack = "Ctrl+Alt+Y";

        HotkeyRegistrationResult result = coordinator.RegisterChangedGlobalHotkeys(initial, updated);

        Assert.Equal(4, messageHost.RegisterCount);
        Assert.Equal(3, messageHost.UnregisterCount);
        Assert.False(result.ToggleAppVisibilityAttempted);
        Assert.True(result.MediaHotkeysAttempted);
        Assert.False(result.MuteHotkeysAttempted);
        Assert.False(result.ListenToInputAttempted);
        Assert.False(result.VolumeStepHotkeysAttempted);
        Assert.False(result.OutputSwitchAttempted);
        Assert.False(result.InputSwitchAttempted);
        Assert.False(result.OutputReverseSwitchAttempted);
        Assert.False(result.InputReverseSwitchAttempted);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void RegisterChangedGlobalHotkeys_ReRegistersMediaGroup_WhenShowCurrentTrackChanges()
    {
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var hotkeys = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
        hotkeys.InitializeInfrastructure();
        var coordinator = new AppHotkeyRegistrationCoordinator(hotkeys, Logger.Instance);

        Settings initial = new()
        {
            Hotkeys = new HotkeysSettings
            {
                Media = new HotkeysMediaSettings
                {
                    ShowCurrentTrack = "Ctrl+Alt+Y",
                    PlayPause = "Ctrl+Alt+P",
                    NextTrack = "Ctrl+Alt+J",
                    PreviousTrack = "Ctrl+Alt+K",
                }
            }
        };

        HotkeyRegistrationResult initialResult = coordinator.RegisterAll(initial);
        Assert.NotNull(messageHost);
        Assert.Equal(5, messageHost!.RegisterCount);
        Assert.Equal(0, messageHost.UnregisterCount);
        Assert.Equal(0, initialResult.FailedCount);
        messageHost.ResetCounts();

        Settings updated = AppSettingsWorkflowCoordinator.CloneSettings(initial);
        updated.Hotkeys.Media.ShowCurrentTrack = "Ctrl+Alt+Shift+Y";

        HotkeyRegistrationResult result = coordinator.RegisterChangedGlobalHotkeys(initial, updated);

        Assert.Equal(4, messageHost.RegisterCount);
        Assert.Equal(4, messageHost.UnregisterCount);
        Assert.False(result.ToggleAppVisibilityAttempted);
        Assert.True(result.MediaHotkeysAttempted);
        Assert.False(result.MuteHotkeysAttempted);
        Assert.False(result.ListenToInputAttempted);
        Assert.False(result.VolumeStepHotkeysAttempted);
        Assert.False(result.OutputSwitchAttempted);
        Assert.False(result.InputSwitchAttempted);
        Assert.False(result.OutputReverseSwitchAttempted);
        Assert.False(result.InputReverseSwitchAttempted);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void RegisterChangedSwitchHotkeys_ReRegistersOnlyChangedSwitchBinding()
    {
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var hotkeys = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
        hotkeys.InitializeInfrastructure();
        var coordinator = new AppHotkeyRegistrationCoordinator(hotkeys, Logger.Instance);

        Settings initial = new()
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O",
                    ReverseSwitchHotkey = "Ctrl+Shift+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+I",
                    ReverseSwitchHotkey = "Ctrl+Shift+I"
                }
            }
        };

        SwitchHotkeyRegistrationResult initialResult = coordinator.RegisterSwitchOnly(initial);
        Assert.False(initialResult.HasFailures);
        Assert.NotNull(messageHost);
        Assert.Equal(4, messageHost!.RegisterCount);
        Assert.Equal(0, messageHost.UnregisterCount);
        messageHost.ResetCounts();

        Settings updated = AppSettingsWorkflowCoordinator.CloneSettings(initial);
        updated.DeviceSwitching.Input.ReverseSwitchHotkey = "Ctrl+Alt+U";

        SwitchHotkeyRegistrationResult result = coordinator.RegisterChangedSwitchHotkeys(initial, updated);

        Assert.Equal(1, messageHost.RegisterCount);
        Assert.Equal(1, messageHost.UnregisterCount);
        Assert.False(result.OutputSwitchAttempted);
        Assert.False(result.InputSwitchAttempted);
        Assert.False(result.OutputReverseSwitchAttempted);
        Assert.True(result.InputReverseSwitchAttempted);
        Assert.False(result.HasFailures);
    }

    [Fact]
    public async Task RegisterRoutineHotkeys_RetainsUnchangedRegistrations_AndRefreshesChangedRoutinePayload()
    {
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var hotkeys = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
        hotkeys.InitializeInfrastructure();
        var coordinator = new AppHotkeyRegistrationCoordinator(hotkeys, Logger.Instance);
        var activations = Channel.CreateUnbounded<string>();

        coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-alpha",
                    Name = "Alpha",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "Ctrl+Alt+F8"
                },
                new AudioRoutine
                {
                    Id = "routine-beta",
                    Name = "Beta",
                    Enabled = true,
                    DisplayOrder = 2,
                    OutputDeviceId = "out-2",
                    Hotkey = "Ctrl+Alt+F9"
                }
            ],
            routine => activations.Writer.TryWrite(routine.Name));

        Assert.NotNull(messageHost);
        Assert.Equal(2, messageHost!.RegisterCount);
        Assert.Equal(0, messageHost.UnregisterCount);
        messageHost.ResetCounts();

        RoutineHotkeyRegistrationResult result = coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-alpha",
                    Name = "Alpha",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "Ctrl+Alt+F8"
                },
                new AudioRoutine
                {
                    Id = "routine-beta",
                    Name = "Beta Updated",
                    Enabled = true,
                    DisplayOrder = 2,
                    OutputDeviceId = "out-2",
                    Hotkey = "Ctrl+Alt+F9"
                }
            ],
            routine => activations.Writer.TryWrite(routine.Name));

        Assert.Equal(1, messageHost.RegisterCount);
        Assert.Equal(1, messageHost.UnregisterCount);
        Assert.Equal(10000, result.AttemptedHotkeyIdsByRoutineId["routine-alpha"]);
        Assert.Equal(10001, result.AttemptedHotkeyIdsByRoutineId["routine-beta"]);

        Assert.NotNull(messageHost);
        Assert.True(messageHost!.Trigger(10000));
        string alphaActivation = await activations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(HotkeyActivationTimeout, TestContext.Current.CancellationToken);
        Assert.Equal("Alpha", alphaActivation);

        _utcNowTicks += AppConstants.Timing.HotkeyDebounceTicks + TimeSpan.TicksPerMillisecond;
        Assert.True(messageHost.Trigger(10001));
        string betaActivation = await activations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(HotkeyActivationTimeout, TestContext.Current.CancellationToken);
        Assert.Equal("Beta Updated", betaActivation);
    }

    [Fact]
    public async Task RegisterRoutineHotkeys_AllowsConfiguredStandaloneExceptions()
    {
        var coordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, Logger.Instance);
        var activations = Channel.CreateUnbounded<string>();

        RoutineHotkeyRegistrationResult result = coordinator.RegisterRoutineHotkeys(
            [
                new AudioRoutine
                {
                    Id = "routine-printscreen",
                    Name = "PrintScreen",
                    Enabled = true,
                    DisplayOrder = 1,
                    OutputDeviceId = "out-1",
                    Hotkey = "PrintScreen"
                }
            ],
            routine => activations.Writer.TryWrite(routine.Id),
            ["PrintScreen"]);

        Assert.Equal(1, result.RegisteredGroupCount);
        Assert.Equal(0, result.FailedGroupCount);

        InvokeHotkeyById(10000);
        string activation = await activations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(HotkeyActivationTimeout, TestContext.Current.CancellationToken);

        Assert.Equal("routine-printscreen", activation);
    }

    private void InvokeHotkeyById(int id)
    {
        Assert.True(_messageHost.Trigger(id));
    }

    public void Dispose()
    {
        _hotkeys.Dispose();
    }

    private sealed class FakeKeyboardHotkeyMessageHost(Action<int, long> dispatchHotkeyId) : IKeyboardHotkeyMessageHost
    {
        private readonly Action<int, long> _dispatchHotkeyId = dispatchHotkeyId;
        private readonly HashSet<int> _registeredHotkeyIds = [];

        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public bool IsRunning { get; private set; }

        public bool TryStart()
        {
            IsRunning = true;
            return true;
        }

        public HotkeyOsRegistrationResult RegisterHotkey(int id, uint fsModifiers, uint vk)
        {
            RegisterCount++;
            _registeredHotkeyIds.Add(id);
            return new HotkeyOsRegistrationResult(Succeeded: true);
        }

        public void UnregisterHotkey(int id)
        {
            UnregisterCount++;
            _registeredHotkeyIds.Remove(id);
        }

        public void Stop()
        {
            IsRunning = false;
            _registeredHotkeyIds.Clear();
        }

        public void Dispose()
        {
            Stop();
        }

        public void ResetCounts()
        {
            RegisterCount = 0;
            UnregisterCount = 0;
        }

        public bool Trigger(int id)
        {
            if (!_registeredHotkeyIds.Contains(id))
            {
                return false;
            }

            _dispatchHotkeyId(id, Stopwatch.GetTimestamp());
            return true;
        }
    }
}
