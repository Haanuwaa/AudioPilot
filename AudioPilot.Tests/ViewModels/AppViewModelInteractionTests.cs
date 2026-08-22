using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("AppDialogServiceIsolation")]
public sealed partial class AppViewModelInteractionTests : IDisposable
{
    private readonly TestSettingsWorkspace _workspace;

    public AppViewModelInteractionTests()
    {
        _workspace = new TestSettingsWorkspace(nameof(AppViewModelInteractionTests));
    }

    [Fact]
    public void SaveSettingsAsync_UpdatesPersistedCachedSettings_AndSwitchHotkeys()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);

            ReplaceCollection(
                harness.ViewModel.OutputCycleDevices,
                new CycleDevice { Id = "out-1", Name = "Speakers", DisplayOrder = 1 },
                new CycleDevice { Id = "out-2", Name = "Headset", DisplayOrder = 2 });

            harness.ViewModel.Hotkey.LoadFromString("Ctrl+Alt+S");
            harness.ViewModel.OutputReverseHotkey.LoadFromString("Ctrl+Alt+Shift+S");
            harness.ViewModel.OverlayDurationSecondsText = "2.5";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Assert.Equal("Ctrl+Alt+S", current.DeviceSwitching.Output.SwitchHotkey);
            Assert.Equal("Ctrl+Alt+Shift+S", current.DeviceSwitching.Output.ReverseSwitchHotkey);
            Assert.Collection(
                current.DeviceSwitching.Output.CycleDevices,
                first => Assert.Equal("out-1", first.Id),
                second => Assert.Equal("out-2", second.Id));
            Assert.Equal(cached.DeviceSwitching.Input.SwitchHotkey, current.DeviceSwitching.Input.SwitchHotkey);

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal("Ctrl+Alt+S", persisted.DeviceSwitching.Output.SwitchHotkey);
            Assert.Equal("Ctrl+Alt+Shift+S", persisted.DeviceSwitching.Output.ReverseSwitchHotkey);
            Assert.Collection(
                persisted.DeviceSwitching.Output.CycleDevices,
                first => Assert.Equal("out-1", first.Id),
                second => Assert.Equal("out-2", second.Id));

            List<(int Id, string Description)> registrations = TestPrivateAccess.GetRegisteredHotkeys(harness.Hotkeys);
            Assert.Contains(registrations, entry => entry.Id == AppConstants.Hotkeys.OutputSwitchHotkeyId && entry.Description.Contains("Ctrl+Alt+S", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(registrations, entry => entry.Id == AppConstants.Hotkeys.OutputReverseSwitchHotkeyId && entry.Description.Contains("Ctrl+Alt+Shift+S", StringComparison.OrdinalIgnoreCase));
            Assert.False(harness.ViewModel.IsSaving);
            Assert.True(harness.ViewModel.ShowBalloonAfterSave);
            Assert.True(harness.Messages.SuccessMessages.Count + harness.Messages.WarningMessages.Count > 0);
        });
    }

    [Fact]
    public void SaveSettingsAsync_PreservesExistingRoutines()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        UsesApplicationTrigger = true,
                        TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                        ShowInTrayMenu = true,
                    }
                ]
            };
            harness.SetCachedSettings(cached);

            harness.ViewModel.Hotkey.LoadFromString("Ctrl+Alt+S");

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            AudioRoutine preserved = Assert.Single(current.Routines.Items);
            Assert.Equal("routine-1", preserved.Id);
            Assert.True(preserved.UsesApplicationTrigger);
            Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", preserved.TriggerAppPath);
            Assert.False(preserved.ShowInTrayMenu);
        });
    }

    [Fact]
    public void SaveSettingsAsync_AllowsRepeatedSave_WhenInputCycleIsDisabledAndCleared()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.InputHotkeysEnabled = false;
            harness.ViewModel.InputCycleDevices.Clear();

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Empty(persisted.DeviceSwitching.Input.CycleDevices);
            Assert.Equal(cached.DeviceSwitching.Input.SwitchHotkey, persisted.DeviceSwitching.Input.SwitchHotkey);
            Assert.DoesNotContain(
                harness.Messages.WarningMessages,
                entry => entry.message.Contains("Please add at least one input device before saving.", StringComparison.Ordinal));
            Assert.True(harness.Messages.SuccessMessages.Count + harness.Messages.WarningMessages.Count > 0);
            Assert.False(harness.ViewModel.IsSaving);
        });
    }

    [Fact]
    public void SaveSettingsAsync_AllowsRepeatedSave_WhenOutputCycleIsDisabledAndCleared()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.OutputHotkeysEnabled = false;
            harness.ViewModel.OutputCycleDevices.Clear();

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Empty(persisted.DeviceSwitching.Output.CycleDevices);
            Assert.Equal(cached.DeviceSwitching.Output.SwitchHotkey, persisted.DeviceSwitching.Output.SwitchHotkey);
            Assert.DoesNotContain(
                harness.Messages.WarningMessages,
                entry => entry.message.Contains("Please add at least one output device before saving.", StringComparison.Ordinal));
            Assert.True(harness.Messages.SuccessMessages.Count + harness.Messages.WarningMessages.Count > 0);
            Assert.False(harness.ViewModel.IsSaving);
        });
    }

    [Fact]
    public void CleanupAsync_FlushesPendingAutoSaveChanges_BeforeShutdown()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.Theme = AppTheme.Dark;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.CleanupAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(AppTheme.Dark, persisted.Theme);
        });
    }

    [Fact]
    public void AutoSave_QueuesTrailingPass_WhenAnEditArrivesDuringAnActiveSave()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            harness.ViewModel.SetIsInitializingForTests(false);

            long revisionAtSaveStart = harness.ViewModel.GetAutoSaveDirtyRevisionForTests();
            harness.ViewModel.SetIsSavingForTests(true);
            harness.ViewModel.Theme = AppTheme.Dark;
            harness.ViewModel.SetIsSavingForTests(false);

            Assert.True(harness.ViewModel.GetAutoSaveDirtyRevisionForTests() > revisionAtSaveStart);
            harness.ViewModel.TryQueueTrailingAutoSaveForTests(revisionAtSaveStart, "test-active-save");
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(AppTheme.Dark, persisted.Theme);
        });
    }

    [Fact]
    public void SaveSettingsAsync_PersistsTheme_WhenCyclesAreUnconfiguredButUnchanged()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.DeviceSwitching.Output.CycleDevices = [];
            cached.DeviceSwitching.Input.CycleDevices = [];
            cached.DeviceSwitching.Output.HotkeysEnabled = true;
            cached.DeviceSwitching.Input.HotkeysEnabled = true;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.Theme = AppTheme.Dark;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(AppTheme.Dark, persisted.Theme);
        });
    }

    [Fact]
    public void CleanupAsync_FlushesThemeAutoSave_WhenCyclesAreUnconfiguredButUnchanged()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            cached.DeviceSwitching.Output.CycleDevices = [];
            cached.DeviceSwitching.Input.CycleDevices = [];
            cached.DeviceSwitching.Output.HotkeysEnabled = true;
            cached.DeviceSwitching.Input.HotkeysEnabled = true;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.Theme = AppTheme.Dark;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.CleanupAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(AppTheme.Dark, persisted.Theme);
        });
    }

    [Fact]
    public void Theme_UpdatesSettingsThemeDraft_WhenChangedFromLiveToolbarControl()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);

            harness.ViewModel.SettingsThemeDraft = AppTheme.System;

            harness.ViewModel.Theme = AppTheme.Dark;

            Assert.Equal(AppTheme.Dark, harness.ViewModel.SettingsThemeDraft);
        });
    }

    [Fact]
    public void RunAtStartup_UpdatesSettingsRunAtStartupDraft_WhenChangedFromLiveToolbarControl()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.RunAtStartup = false;
            harness.SetCachedSettings(cached);

            harness.ViewModel.SettingsRunAtStartupDraft = false;

            harness.ViewModel.RunAtStartup = true;

            Assert.True(harness.ViewModel.SettingsRunAtStartupDraft);
        });
    }

    [Fact]
    public void SetStartupEnabledFromCli_PersistsSettingsRegistryAndLiveState()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();

            string registryPath = $"SOFTWARE\\AudioPilot.Tests\\{Guid.NewGuid():N}";
            string registryValueName = $"AudioPilotCliToggle_{Guid.NewGuid():N}";
            var registry = new InMemoryUserRegistryAccessor();
            var startupService = new StartupService(registryPath, registryValueName, Logger.Instance, registry);
            using var harness = CreateHarness(
                Dispatcher.CurrentDispatcher,
                startupService: startupService);

            Settings cached = BuildCachedSettings();
            cached.RunAtStartup = false;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            bool succeeded = harness.ViewModel.SetStartupEnabledFromCli(enabled: true);

            Assert.True(succeeded);
            Assert.True(harness.ViewModel.RunAtStartup);
            Assert.True(harness.ViewModel.SettingsRunAtStartupDraft);
            Assert.True(harness.SettingsService.LoadSettings().RunAtStartup);
            Assert.NotNull(registry.GetValue(registryPath, registryValueName));
        });
    }

    [Fact]
    public void SaveSettingsAsync_PersistsRunAtStartup_WhenSavingImmediatelyAfterLiveToggle()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();

            string registryPath = $"SOFTWARE\\AudioPilot.Tests\\{Guid.NewGuid():N}";
            string registryValueName = $"AudioPilotToggle_{Guid.NewGuid():N}";
            using var loggerScope = new TestLoggerScope(nameof(AppViewModelInteractionTests), "startup-save-sync.log", LogLevel.Trace);
            var registry = new InMemoryUserRegistryAccessor();
            var startupService = new StartupService(registryPath, registryValueName, loggerScope.Logger, registry);
            using var harness = CreateHarness(
                Dispatcher.CurrentDispatcher,
                logger: loggerScope.Logger,
                startupService: startupService,
                allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            cached.RunAtStartup = false;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            harness.ViewModel.SetIsInitializingForTests(false);

            harness.ViewModel.RunAtStartup = true;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            string? startupEntry = registry.GetValue(registryPath, registryValueName)?.ToString();

            Assert.True(harness.ViewModel.RunAtStartup);
            Assert.True(persisted.RunAtStartup);
            Assert.False(string.IsNullOrWhiteSpace(startupEntry));
            Assert.Contains("-startup", startupEntry, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void SaveSettingsAsync_Blocks_WhenOutputCycleIsClearedButHotkeysRemainEnabled()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.OutputHotkeysEnabled = true;
            harness.ViewModel.Hotkey.LoadFromString("Ctrl+Alt+O");
            harness.ViewModel.OutputCycleDevices.Clear();

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(2, persisted.DeviceSwitching.Output.CycleDevices.Count);
            Assert.Contains(
                harness.Messages.WarningMessages,
                entry => entry.message.Contains("Please add at least one output device before saving.", StringComparison.Ordinal));
            Assert.False(harness.ViewModel.IsSaving);
        });
    }

    [Fact]
    public void SaveSettingsAsync_Blocks_WhenInputCycleIsClearedButHotkeysRemainEnabled()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.InputHotkeysEnabled = true;
            harness.ViewModel.InputHotkey.LoadFromString("Ctrl+Alt+I");
            harness.ViewModel.InputCycleDevices.Clear();

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(2, persisted.DeviceSwitching.Input.CycleDevices.Count);
            Assert.Contains(
                harness.Messages.WarningMessages,
                entry => entry.message.Contains("Please add at least one input device before saving.", StringComparison.Ordinal));
            Assert.False(harness.ViewModel.IsSaving);
        });
    }

    [Fact]
    public void SaveSettingsAsync_AllowsClearedOutputCycle_WhenNoSwitchHotkeysRemainEvenIfEnabledStaysTrue()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.OutputHotkeysEnabled = true;
            harness.ViewModel.Hotkey.LoadFromString(string.Empty);
            harness.ViewModel.OutputReverseHotkey.LoadFromString(string.Empty);
            harness.ViewModel.OutputCycleDevices.Clear();

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Empty(persisted.DeviceSwitching.Output.CycleDevices);
            Assert.Equal(string.Empty, persisted.DeviceSwitching.Output.SwitchHotkey);
            Assert.Equal(string.Empty, persisted.DeviceSwitching.Output.ReverseSwitchHotkey);
            Assert.DoesNotContain(
                harness.Messages.WarningMessages,
                entry => entry.message.Contains("Please add at least one output device before saving.", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void SaveSettingsAsync_PreservesDisabledOutputHotkeyText_WhenCycleIsCleared()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            cached.Miscellaneous.AutoSaveEnabled = true;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.OutputHotkeysEnabled = false;
            harness.ViewModel.Hotkey.LoadFromString("Ctrl+Alt+O");
            harness.ViewModel.OutputReverseHotkey.LoadFromString(string.Empty);
            harness.ViewModel.OutputCycleDevices.Clear();

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Empty(persisted.DeviceSwitching.Output.CycleDevices);
            Assert.False(persisted.DeviceSwitching.Output.HotkeysEnabled);
            Assert.Equal("Ctrl+Alt+O", persisted.DeviceSwitching.Output.SwitchHotkey);
            Assert.False(harness.ViewModel.IsSaving);
        });
    }

    [Fact]
    public void SaveSettingsAsync_PersistsOutputCycle_WhenOnlyReverseSwitchHotkeyIsConfigured()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.OutputHotkeysEnabled = true;
            harness.ViewModel.Hotkey.LoadFromString(string.Empty);
            harness.ViewModel.OutputReverseHotkey.LoadFromString("Ctrl+Alt+Shift+O");
            harness.ViewModel.OutputCycleDevices.Clear();
            harness.ViewModel.OutputCycleDevices.Add(new CycleDevice { Id = "out-new-1", Name = "Desk Speakers" });
            harness.ViewModel.OutputCycleDevices.Add(new CycleDevice { Id = "out-new-2", Name = "Headset" });

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(["out-new-1", "out-new-2"], [.. persisted.DeviceSwitching.Output.CycleDevices.Select(static device => device.Id)]);
            Assert.Equal(string.Empty, persisted.DeviceSwitching.Output.SwitchHotkey);
            Assert.Equal("Ctrl+Alt+Shift+O", persisted.DeviceSwitching.Output.ReverseSwitchHotkey);
        });
    }

    [Fact]
    public void SaveRoutinesCommand_IsDisabled_WhenNoRoutinesAndNoUnsavedChanges()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Assert.False(harness.ViewModel.HasRoutines);
            Assert.False(harness.ViewModel.HasUnsavedRoutineChanges);
            Assert.False(harness.ViewModel.SaveRoutinesCommand.CanExecute(null));
        });
    }

    [Fact]
    public void SaveCurrentContextAsync_DoesNotSaveRoutines_WhenSaveRoutinesCommandIsDisabled()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.Routines = new RoutinesSettings { Items = [] };
            harness.SetCachedSettings(cached);

            harness.ViewModel.SelectedSettingsTabIndex = 2;

            Assert.False(harness.ViewModel.HasRoutines);
            Assert.False(harness.ViewModel.HasUnsavedRoutineChanges);
            Assert.False(harness.ViewModel.SaveRoutinesCommand.CanExecute(null));

            TestPrivateAccess.RunTaskOnDispatcher(
                TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel, "SaveCurrentContextAsync"));

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Empty(persisted.Routines.Items);
            Assert.False(harness.ViewModel.IsSavingRoutines);
        });
    }

    [Fact]
    public void RemoveOutputCycleDeviceCommand_RemovesAllSelectedDevices()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);

            harness.ViewModel.SelectedOutputCycleDevices.Add(harness.ViewModel.OutputCycleDevices[0]);
            harness.ViewModel.SelectedOutputCycleDevices.Add(harness.ViewModel.OutputCycleDevices[1]);
            harness.ViewModel.SelectedOutputCycleIndex = 1;

            Assert.True(harness.ViewModel.RemoveOutputCycleDeviceCommand.CanExecute(null));

            harness.ViewModel.RemoveOutputCycleDeviceCommand.Execute(null);

            Assert.Empty(harness.ViewModel.OutputCycleDevices);
            Assert.Equal(-1, harness.ViewModel.SelectedOutputCycleIndex);
        });
    }

    [Fact]
    public void RemoveInputCycleDeviceCommand_RemovesAllSelectedDevices()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);

            harness.ViewModel.SelectedInputCycleDevices.Add(harness.ViewModel.InputCycleDevices[0]);
            harness.ViewModel.SelectedInputCycleDevices.Add(harness.ViewModel.InputCycleDevices[1]);
            harness.ViewModel.SelectedInputCycleIndex = 1;

            Assert.True(harness.ViewModel.RemoveInputCycleDeviceCommand.CanExecute(null));

            harness.ViewModel.RemoveInputCycleDeviceCommand.Execute(null);

            Assert.Empty(harness.ViewModel.InputCycleDevices);
            Assert.Equal(-1, harness.ViewModel.SelectedInputCycleIndex);
        });
    }

    [Fact]
    public void RemoveRoutineCommand_RemovesAllSelectedRoutines()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            ReplaceCollection(
                harness.ViewModel.Routines,
                new AudioRoutine { Id = "routine-1", Name = "Desk", Enabled = true },
                new AudioRoutine { Id = "routine-2", Name = "Meeting", Enabled = true },
                new AudioRoutine { Id = "routine-3", Name = "Stream", Enabled = false });

            harness.ViewModel.SelectedRoutines.Add(harness.ViewModel.Routines[0]);
            harness.ViewModel.SelectedRoutines.Add(harness.ViewModel.Routines[2]);
            harness.ViewModel.SelectedRoutineIndex = 2;

            Assert.True(harness.ViewModel.RemoveRoutineCommand.CanExecute(null));

            harness.ViewModel.RemoveRoutineCommand.Execute(null);

            AudioRoutine remaining = Assert.Single(harness.ViewModel.Routines);
            Assert.Equal("routine-2", remaining.Id);
            Assert.True(harness.ViewModel.HasUnsavedRoutineChanges);
        });
    }

    [Fact]
    public void EnableDisableSelectedRoutinesCommands_UpdateAllSelectedRoutines()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            ReplaceCollection(
                harness.ViewModel.Routines,
                new AudioRoutine { Id = "routine-1", Name = "Desk", Enabled = true },
                new AudioRoutine { Id = "routine-2", Name = "Meeting", Enabled = true },
                new AudioRoutine { Id = "routine-3", Name = "Stream", Enabled = false });

            harness.ViewModel.SelectedRoutines.Add(harness.ViewModel.Routines[0]);
            harness.ViewModel.SelectedRoutines.Add(harness.ViewModel.Routines[2]);
            harness.ViewModel.SelectedRoutineIndex = 0;

            Assert.True(harness.ViewModel.DisableSelectedRoutinesCommand.CanExecute(null));

            harness.ViewModel.DisableSelectedRoutinesCommand.Execute(null);

            Assert.False(harness.ViewModel.Routines[0].Enabled);
            Assert.False(harness.ViewModel.Routines[2].Enabled);
            Assert.True(harness.ViewModel.EnableSelectedRoutinesCommand.CanExecute(null));

            harness.ViewModel.EnableSelectedRoutinesCommand.Execute(null);

            Assert.True(harness.ViewModel.Routines[0].Enabled);
            Assert.True(harness.ViewModel.Routines[2].Enabled);
        });
    }

    [Fact]
    public void SwitchInputDevicesAsync_ShowsWarning_WhenCycleIsEmpty()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            harness.ViewModel.InputCycleDevices.Clear();

            Task<bool> switchTask = harness.ViewModel.SwitchInputDevicesAsync().AsTask();
            TestPrivateAccess.RunTaskOnDispatcher(switchTask);
            bool switched = switchTask.GetAwaiter().GetResult();

            Assert.False(switched);
            var (message, caption) = Assert.Single(harness.Messages.WarningMessages);
            Assert.Equal("Please configure input cycle devices before switching.", message);
            Assert.Equal(DialogText.Captions.InputDevicesMissing, caption);
            Assert.Empty(harness.OverlayMessages);
        });
    }

    [Fact]
    public void ExportSettingsAsync_DoesNothing_WhenDialogIsCancelled()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            AppViewModel.ExportSettingsDialogForTests = _ => (false, string.Empty);

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ExportSettingsForTestsAsync());

            Assert.Empty(harness.Messages.SuccessMessages);
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

    [Fact]
    public void ExportSettingsAsync_ExportsCurrentSettings_AndShowsSuccess()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.DeviceSwitching.Output.CycleDevices =
            [
                new CycleDevice { Id = "export-current", Name = "Desk Speakers" }
            ];
            harness.SetCachedSettings(cached);

            string exportPath = Path.Combine(_workspace.Root, "ui-export-settings.json");
            AppViewModel.ExportSettingsDialogForTests = _ => (true, exportPath);

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ExportSettingsForTestsAsync());

            Assert.True(File.Exists(exportPath));
            string text = File.ReadAllText(exportPath);
            Assert.Contains("export-current", text, StringComparison.Ordinal);
            Assert.Contains(
                harness.Messages.SuccessMessages,
                entry => entry.caption == DialogText.Captions.ExportSettings && entry.message.Contains("ui-export-settings.json", StringComparison.Ordinal));
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

    [Fact]
    public void ExportSettingsAsync_ShowsError_WhenExportFails()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            string exportPath = Path.Combine(_workspace.Root, "ui-export-settings.txt");
            AppViewModel.ExportSettingsDialogForTests = _ => (true, exportPath);

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ExportSettingsForTestsAsync());

            Assert.Empty(harness.Messages.SuccessMessages);
            Assert.Contains(
                harness.Messages.ErrorMessages,
                entry => entry.caption == DialogText.Captions.ExportSettings && entry.message.Contains("ui-export-settings.txt", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ImportSettingsAsync_DoesNothing_WhenDialogIsCancelled()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            AppViewModel.ImportSettingsDialogForTests = _ => (false, string.Empty);

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ImportSettingsForTestsAsync());

            Assert.False(harness.ViewModel.IsApplyingSettings);
            Assert.Empty(harness.Messages.SuccessMessages);
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

    [Fact]
    public void ImportSettingsAsync_DoesNotImport_WhenConfirmationIsDeclined()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            string importPath = Path.Combine(_workspace.Root, "declined-import.json");
            SettingsTransferService.ExportSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "import-declined", Name = "Imported Speakers" }
                        ]
                    }
                }
            }, importPath);

            AppViewModel.ImportSettingsDialogForTests = _ => (true, importPath);
            harness.Messages.YesNoResponse = AppDialogResult.Declined;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ImportSettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Assert.NotEqual("import-declined", current.DeviceSwitching.Output.CycleDevices[0].Id);
            Assert.False(harness.ViewModel.IsApplyingSettings);
            Assert.Empty(harness.Messages.SuccessMessages);
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

    [Fact]
    public void ImportSettingsAsync_ImportsSettings_AndShowsSuccess()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            string importPath = Path.Combine(_workspace.Root, "success-import.json");
            SettingsTransferService.ExportSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "import-success", Name = "Imported Speakers" }
                        ],
                        SwitchHotkey = "Ctrl+Alt+9"
                    },
                    Input = new DeviceSwitchingInputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "import-mic", Name = "Imported Mic" }
                        ],
                        SwitchHotkey = "Ctrl+Alt+8"
                    }
                }
            }, importPath);

            AppViewModel.ImportSettingsDialogForTests = _ => (true, importPath);
            harness.Messages.YesNoResponse = AppDialogResult.Confirmed;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ImportSettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Settings persisted = harness.SettingsService.LoadSettings();

            Assert.Equal("import-success", current.DeviceSwitching.Output.CycleDevices[0].Id);
            Assert.Equal("import-success", persisted.DeviceSwitching.Output.CycleDevices[0].Id);
            Assert.Equal("Ctrl+Alt+9", current.DeviceSwitching.Output.SwitchHotkey);
            Assert.Equal("Ctrl+Alt+9", persisted.DeviceSwitching.Output.SwitchHotkey);
            Assert.False(harness.ViewModel.IsApplyingSettings);
            Assert.Contains(
                harness.Messages.SuccessMessages,
                entry => entry.caption == DialogText.Captions.ImportSettings && entry.message.Contains("success-import.json", StringComparison.Ordinal));
            Assert.Empty(harness.Messages.ErrorMessages);
        });
    }

    [Fact]
    public void ImportSettingsAsync_ShowsError_WhenImportFormatIsUnsupported()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            string importPath = Path.Combine(_workspace.Root, "bad-import.txt");
            File.WriteAllText(importPath, "not-a-supported-import");

            AppViewModel.ImportSettingsDialogForTests = _ => (true, importPath);
            harness.Messages.YesNoResponse = AppDialogResult.Confirmed;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ImportSettingsForTestsAsync());

            Assert.False(harness.ViewModel.IsApplyingSettings);
            Assert.Contains(
                harness.Messages.ErrorMessages,
                entry => entry.caption == DialogText.Captions.ImportSettings && entry.message.Contains("Only .json and .zip settings files are supported.", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ApplySettingsAsync_UpdatesCachedSettings_AndRegistersRuntimeHotkeys()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            string deviceReferencePath = Path.Combine(_workspace.PrimaryDir, "DEVICES.txt");

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            TestPrivateAccess.GetField<AppHotkeyRegistrationCoordinator>(harness.ViewModel, "_hotkeyRegistrationCoordinator").RegisterAll(cached);
            SetKnownActiveDeviceInfos(
                harness.ViewModel,
                [new CycleDevice { Id = "out-1", Name = "Speakers", DisplayOrder = 1 }],
                [new CycleDevice { Id = "in-1", Name = "Microphone", DisplayOrder = 1 }]);

            harness.ViewModel.SettingsRunAtStartupDraft = cached.RunAtStartup;
            harness.ViewModel.OutputReverseHotkey.LoadFromString("Ctrl+Alt+Shift+O");
            harness.ViewModel.InputReverseHotkey.LoadFromString("Ctrl+Alt+Shift+I");
            harness.ViewModel.SettingsToggleAppVisibilityHotkeyDraft = "Ctrl+Alt+H";
            harness.ViewModel.SettingsShowCurrentTrackHotkeyDraft = "Ctrl+Alt+Y";
            harness.ViewModel.SettingsPlayPauseHotkeyDraft = "Ctrl+Alt+P";
            harness.ViewModel.SettingsNextTrackHotkeyDraft = "Ctrl+Alt+.";
            harness.ViewModel.SettingsPreviousTrackHotkeyDraft = "Ctrl+Alt+,";
            harness.ViewModel.SettingsMuteMicHotkeyDraft = "Ctrl+Alt+M";
            harness.ViewModel.SettingsMuteSoundHotkeyDraft = "Ctrl+Alt+Shift+M";
            harness.ViewModel.SettingsDeafenHotkeyDraft = "Ctrl+Alt+D";
            harness.ViewModel.SettingsListenToInputHotkeyDraft = "Ctrl+Alt+L";
            harness.ViewModel.SettingsThemeDraft = AppTheme.Dark;
            harness.ViewModel.SettingsPreserveAudioLevelsDraft = false;
            harness.ViewModel.SettingsBluetoothReconnectEnabledDraft = false;
            harness.ViewModel.SettingsDeviceReferenceFileModeDraft = DeviceReferenceFileMode.Plaintext;
            harness.ViewModel.SettingsOverlayEnabledDraft = false;
            harness.ViewModel.SettingsOverlayPositionDraft = OverlayPosition.TopLeft;
            harness.ViewModel.SettingsOverlayDurationSecondsDraft = "1.5";
            harness.ViewModel.SettingsLogLevelDraft = LogLevel.Debug;
            harness.ViewModel.SettingsPlayDialogSoundsDraft = false;
            Assert.False(harness.Messages.SoundsEnabled);
            harness.ViewModel.SettingsOutputRoleMultimediaDraft = true;
            harness.ViewModel.SettingsOutputRoleCommunicationsDraft = false;
            harness.ViewModel.SettingsOutputRoleConsoleDraft = true;
            harness.ViewModel.SettingsInputRoleMultimediaDraft = true;
            harness.ViewModel.SettingsInputRoleCommunicationsDraft = false;
            harness.ViewModel.SettingsInputRoleConsoleDraft = false;
            harness.ViewModel.SettingsListenMonitorOutputDeviceIdDraft = "monitor-1";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ApplySettingsForTestsAsync());

            harness.ViewModel.SettingsDeviceReferenceFileModeDraft = DeviceReferenceFileMode.Hashed;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ApplySettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Assert.Equal(AppTheme.Dark, current.Theme);
            Assert.False(current.Miscellaneous.PreserveAudioLevels);
            Assert.False(current.Miscellaneous.BluetoothReconnectEnabled);
            Assert.Equal(DeviceReferenceFileMode.Hashed, current.Miscellaneous.DeviceReferenceFileMode);
            Assert.False(current.Overlay.Enabled);
            Assert.Equal(OverlayPosition.TopLeft, current.Overlay.Position);
            Assert.Equal(1.5, current.Overlay.DurationSeconds);
            Assert.Equal("Debug", current.Miscellaneous.LogLevel);
            Assert.False(current.Miscellaneous.PlayDialogSounds);
            Assert.Equal("Ctrl+Alt+Y", current.Hotkeys.Media.ShowCurrentTrack);
            Assert.Equal("Ctrl+Alt+M", current.Hotkeys.Mute.Mic);
            Assert.Equal("Ctrl+Alt+Shift+M", current.Hotkeys.Mute.Sound);
            Assert.Equal("Ctrl+Alt+D", current.Hotkeys.Mute.Deafen);
            Assert.Equal("Ctrl+Alt+L", current.Hotkeys.Listen.ListenToInput);
            Assert.Equal("monitor-1", current.Hotkeys.Listen.MonitorOutputDeviceId);
            Assert.Equal(["Multimedia", "Console"], current.DeviceSwitching.Output.SwitchRoles);
            Assert.Equal(["Multimedia"], current.DeviceSwitching.Input.SwitchRoles);
            Assert.Equal(AppTheme.Dark, harness.ViewModel.Theme);

            Assert.True(File.Exists(deviceReferencePath));
            string hashedDeviceReference = File.ReadAllText(deviceReferencePath);
            Assert.DoesNotContain("out-1 | Speakers", hashedDeviceReference, StringComparison.Ordinal);
            Assert.DoesNotContain("in-1 | Microphone", hashedDeviceReference, StringComparison.Ordinal);
            Assert.Contains("[OUTPUT DEVICES]", hashedDeviceReference, StringComparison.Ordinal);
            Assert.Contains("Speakers", hashedDeviceReference, StringComparison.Ordinal);
            Assert.Contains("Microphone", hashedDeviceReference, StringComparison.Ordinal);

            List<(int Id, string Description)> registrations = TestPrivateAccess.GetRegisteredHotkeys(harness.Hotkeys);
            Assert.Contains(registrations, entry => entry.Description.Contains("Ctrl+Alt+H", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(registrations, entry => entry.Description.Contains("Ctrl+Alt+Y", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(registrations, entry => entry.Description.Contains("Ctrl+Alt+M", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(registrations, entry => entry.Description.Contains("Ctrl+Alt+L", StringComparison.OrdinalIgnoreCase));
            Assert.False(harness.ViewModel.IsApplyingSettings);
            Assert.Empty(harness.Messages.ErrorMessages);
            Assert.True(harness.Messages.SuccessMessages.Count + harness.Messages.WarningMessages.Count > 0);
        });
    }

    [Fact]
    public void ApplySettingsAsync_ShowsWarning_AndSkipsApply_WhenOverlayDurationIsInvalid()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);

            harness.ViewModel.SettingsRunAtStartupDraft = cached.RunAtStartup;
            harness.ViewModel.SettingsThemeDraft = AppTheme.Dark;
            harness.ViewModel.SettingsOverlayDurationSecondsDraft = "bogus";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ApplySettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Assert.Equal(AppTheme.System, current.Theme);
            Assert.False(harness.ViewModel.IsApplyingSettings);
            Assert.Contains(
                harness.Messages.WarningMessages,
                entry => entry.caption == DialogText.Captions.InvalidOverlayDuration &&
                         entry.message == DialogText.Messages.InvalidOverlayDuration);
        });
    }

    [Fact]
    public void ApplySettingsAsync_AllowsBlankVolumeStep_WhenCorrespondingHotkeysAreCleared()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.Hotkeys.Volume.MasterUp = "Alt+WheelUp";
            cached.Hotkeys.Volume.MasterDown = "Alt+WheelDown";
            cached.Hotkeys.Volume.MicUp = "Alt+Home";
            cached.Hotkeys.Volume.MicDown = "Alt+End";
            cached.Hotkeys.Volume.MasterVolumeStepPercent = 7;
            cached.Hotkeys.Volume.MicVolumeStepPercent = 9;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.SettingsRunAtStartupDraft = cached.RunAtStartup;
            harness.ViewModel.SettingsThemeDraft = AppTheme.Dark;
            harness.ViewModel.SettingsOverlayDurationSecondsDraft = "1.5";
            harness.ViewModel.SettingsMasterVolumeUpHotkeyDraft = string.Empty;
            harness.ViewModel.SettingsMasterVolumeDownHotkeyDraft = string.Empty;
            harness.ViewModel.SettingsMicVolumeUpHotkeyDraft = string.Empty;
            harness.ViewModel.SettingsMicVolumeDownHotkeyDraft = string.Empty;
            harness.ViewModel.SettingsMasterVolumeStepPercentDraft = string.Empty;
            harness.ViewModel.SettingsMicVolumeStepPercentDraft = string.Empty;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ApplySettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Assert.Equal(string.Empty, current.Hotkeys.Volume.MasterUp);
            Assert.Equal(string.Empty, current.Hotkeys.Volume.MasterDown);
            Assert.Equal(string.Empty, current.Hotkeys.Volume.MicUp);
            Assert.Equal(string.Empty, current.Hotkeys.Volume.MicDown);
            Assert.Equal(7, current.Hotkeys.Volume.MasterVolumeStepPercent);
            Assert.Equal(9, current.Hotkeys.Volume.MicVolumeStepPercent);
            Assert.DoesNotContain(
                harness.Messages.WarningMessages,
                entry => entry.message.Contains("Volume step values must be whole numbers between 1 and 100.", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ApplySettingsAsync_Aborts_WhenStartupRegistryUpdateFails()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.RunAtStartup = false;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            AppViewModel.TryApplyStartupChangeOverrideForTests = static (_, _) => false;

            harness.ViewModel.SettingsRunAtStartupDraft = true;
            harness.ViewModel.SettingsThemeDraft = AppTheme.Dark;
            harness.ViewModel.SettingsOverlayDurationSecondsDraft = "1.5";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ApplySettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Assert.False(current.RunAtStartup);
            Assert.Equal(AppTheme.System, current.Theme);

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.False(persisted.RunAtStartup);
            Assert.Equal(AppTheme.System, persisted.Theme);

            Assert.False(harness.ViewModel.IsApplyingSettings);
            Assert.False(harness.ViewModel.RunAtStartup);
            Assert.Contains(
                harness.Messages.ErrorMessages,
                entry => string.Equals(entry.message, "Failed to update startup state.", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void RunRoutineFromTrayAsync_IgnoresAppStartRoutineThatIsNoLongerTrayEligible()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Spotify",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        UsesApplicationTrigger = true,
                        TriggerAppPath = @"C:\Apps\DefinitelyMissing\Missing.exe",
                        ShowInTrayMenu = true,
                        SwitchOutputPerApp = true
                    }
                ]
            };
            harness.SetCachedSettings(cached);

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.RunRoutineFromTrayAsync("routine-1"));

            Assert.Empty(harness.OverlayMessages);
        });
    }

    [Fact]
    public void RunAtStartup_LogsSharedOpId_WhenRegistryUpdateSucceeds_AndJsonWriteFails()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();

            string logRoot = Path.Combine(_workspace.Root, "startup-toggle-logs");
            Directory.CreateDirectory(logRoot);
            string logPath = Path.Combine(logRoot, "startup-toggle.log");
            using var logger = new Logger(logRoot, "startup-toggle.log")
            {
                MinimumLevel = LogLevel.Trace,
            };

            string registryPath = $"SOFTWARE\\AudioPilot.Tests\\{Guid.NewGuid():N}";
            string registryValueName = $"AudioPilotToggle_{Guid.NewGuid():N}";
            var registry = new InMemoryUserRegistryAccessor();
            var startupService = new StartupService(registryPath, registryValueName, logger, registry);
            using var harness = CreateHarness(
                Dispatcher.CurrentDispatcher,
                logger: logger,
                startupService: startupService,
                allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            cached.RunAtStartup = false;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            harness.ViewModel.SetIsInitializingForTests(false);
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.EnterSettingsWriteLockForTestsAsync());

            try
            {
                harness.ViewModel.RunAtStartup = true;

                string registryUpdateFragment = AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistryUpdate;
                var registryUpdateLogTask = Task.Run(() => TestLogFileAssert.WaitForLogText(logPath, registryUpdateFragment, timeoutMs: 2000));
                TestPrivateAccess.RunTaskOnDispatcher(registryUpdateLogTask);
                string logText = registryUpdateLogTask.GetAwaiter().GetResult();
                Match registryOpIdMatch = MyRegex().Match(logText);
                Assert.True(registryOpIdMatch.Success, $"Expected startup registry opId in log.\nLog text:\n{logText}");

                string invalidPrimaryBase = Path.Combine(_workspace.Root, "<>invalid-primary-base");
                string invalidFallbackBase = Path.Combine(_workspace.Root, "<>invalid-fallback-base");
                harness.SettingsService.OverrideWriteTargetsForTests(
                    Path.Combine(invalidPrimaryBase, "settings.json"),
                    Path.Combine(invalidFallbackBase, "settings.json"));

                harness.ViewModel.ReleaseSettingsWriteLockForTests();
                TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());
            }
            finally
            {
                harness.ViewModel.ReleaseSettingsWriteLockForTests();
            }

            logger.Dispose();

            string finalLogText = TestLogFileAssert.WaitForLogText(
                logPath,
                requiredFragments:
                [
                    AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistryUpdate,
                    AppConstants.Audio.LogEvents.ViewModel.App.StartupSyncWarning,
                    AppConstants.Audio.LogEvents.ViewModel.App.StartupJsonSyncFailed
                ]);

            Match opIdMatch = MyRegex().Match(finalLogText);
            Assert.True(opIdMatch.Success, $"Expected startup registry opId in log.\nLog text:\n{finalLogText}");
            string opId = opIdMatch.Groups[1].Value;

            Assert.Contains($"{AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistryUpdate} | opId={opId}", finalLogText, StringComparison.Ordinal);
            Assert.Contains($"{AppConstants.Audio.LogEvents.ViewModel.App.StartupSyncWarning} | opId={opId}", finalLogText, StringComparison.Ordinal);
            Assert.DoesNotContain($"{AppConstants.Audio.LogEvents.ViewModel.App.StartupJsonSyncSuccess} | opId={opId}", finalLogText, StringComparison.Ordinal);
            Assert.Contains($"{AppConstants.Audio.LogEvents.ViewModel.App.StartupJsonSyncFailed} | opId={opId}", finalLogText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ResetPerAppAudioRoutingAsync_ResetsAssignmentsAndShowsSuccess()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            var resetter = new FakePerAppAudioRoutingResetter
            {
                Result = new PerAppAudioRoutingResetResult(Success: true, HadAssignments: true)
            };
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, resetter);
            harness.Messages.YesNoResponse = AppDialogResult.Confirmed;

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ResetPerAppAudioRoutingForTestsAsync());

            Assert.Equal(1, resetter.ResetAllCalls);
            Assert.Contains(
                harness.Messages.SuccessMessages,
                entry => entry.caption == DialogText.Captions.ResetPerAppAudio &&
                         entry.message.Contains("Per-application audio assignments were reset.", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ApplySettingsAsync_PreservesExistingRoutines()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            cached.Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        UsesApplicationTrigger = true,
                        TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                        ShowInTrayMenu = true,
                    }
                ]
            };
            harness.SetCachedSettings(cached);

            harness.ViewModel.SettingsRunAtStartupDraft = cached.RunAtStartup;
            harness.ViewModel.SettingsThemeDraft = AppTheme.Dark;
            harness.ViewModel.SettingsOverlayDurationSecondsDraft = "1.5";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ApplySettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            AudioRoutine preserved = Assert.Single(current.Routines.Items);
            Assert.Equal("routine-1", preserved.Id);
            Assert.True(preserved.UsesApplicationTrigger);
            Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", preserved.TriggerAppPath);
            Assert.False(preserved.ShowInTrayMenu);
        });
    }

    [Fact]
    public void SaveSettingsAsync_LogsPostSaveFailure_ButStillCompletesSaveFlow()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(AppViewModelInteractionTests), "save-post-failed.log", LogLevel.Error);
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, logger: loggerScope.Logger, allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            AudioDeviceService.SetMicrophoneMuteOverrideForTests = static _ => throw new InvalidOperationException("simulated-post-save-mute-failure");

            harness.ViewModel.Hotkey.LoadFromString("Ctrl+Alt+S");
            harness.ViewModel.OverlayDurationSecondsText = "2.5";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Settings persisted = harness.SettingsService.LoadSettings();

            Assert.Equal("Ctrl+Alt+S", current.DeviceSwitching.Output.SwitchHotkey);
            Assert.Equal("Ctrl+Alt+S", persisted.DeviceSwitching.Output.SwitchHotkey);
            Assert.True(harness.ViewModel.ShowBalloonAfterSave);
            Assert.False(harness.ViewModel.IsSaving);
            Assert.True(harness.Messages.SuccessMessages.Count + harness.Messages.WarningMessages.Count > 0);

            loggerScope.Logger.Dispose();
            string logText = TestLogFileAssert.WaitForLogText(loggerScope.LogPath, "save-post-failed");
            Assert.Contains("save-post-failed", logText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SaveSettingsAsync_AppliesEndpointMuteStateAwayFromDispatcher()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            int dispatcherThreadId = Environment.CurrentManagedThreadId;
            int microphoneThreadId = 0;
            int playbackThreadId = 0;
            AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ =>
                Volatile.Write(ref microphoneThreadId, Environment.CurrentManagedThreadId);
            AudioDeviceService.SetPlaybackMuteOverrideForTests = _ =>
                Volatile.Write(ref playbackThreadId, Environment.CurrentManagedThreadId);

            harness.ViewModel.Hotkey.LoadFromString("Ctrl+Alt+S");
            harness.ViewModel.OverlayDurationSecondsText = "2.5";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

            Assert.NotEqual(0, Volatile.Read(ref microphoneThreadId));
            Assert.NotEqual(0, Volatile.Read(ref playbackThreadId));
            Assert.NotEqual(dispatcherThreadId, Volatile.Read(ref microphoneThreadId));
            Assert.NotEqual(dispatcherThreadId, Volatile.Read(ref playbackThreadId));
        });
    }

    [Fact]
    public void RunAtStartup_RemainsEnabled_WhenRegistrySucceeds_ButJsonPersistenceFails()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(AppViewModelInteractionTests), "startup-persist-failed.log", LogLevel.Trace);

            string registryPath = $"SOFTWARE\\AudioPilot.Tests\\{Guid.NewGuid():N}";
            string registryValueName = $"AudioPilotToggle_{Guid.NewGuid():N}";
            var registry = new InMemoryUserRegistryAccessor();
            var startupService = new StartupService(registryPath, registryValueName, loggerScope.Logger, registry);
            using var harness = CreateHarness(
                Dispatcher.CurrentDispatcher,
                logger: loggerScope.Logger,
                startupService: startupService,
                allowBackgroundWork: true);

            Settings cached = BuildCachedSettings();
            cached.RunAtStartup = false;
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            harness.ViewModel.SetIsInitializingForTests(false);

            string originalPrimaryDir = _workspace.PrimaryDir;
            string originalFallbackDir = _workspace.FallbackDir;
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.EnterSettingsWriteLockForTestsAsync());

            try
            {
                harness.ViewModel.RunAtStartup = true;

                string registryUpdateFragment = AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistryUpdate;
                var registryUpdateLogTask = Task.Run(() => TestLogFileAssert.WaitForLogText(loggerScope.LogPath, registryUpdateFragment, timeoutMs: 2000));
                TestPrivateAccess.RunTaskOnDispatcher(registryUpdateLogTask);

                string invalidPrimaryBase = Path.Combine(_workspace.Root, "<>invalid-primary-base");
                string invalidFallbackBase = Path.Combine(_workspace.Root, "<>invalid-fallback-base");
                harness.SettingsService.OverrideWriteTargetsForTests(
                    Path.Combine(invalidPrimaryBase, "settings.json"),
                    Path.Combine(invalidFallbackBase, "settings.json"));

                harness.ViewModel.ReleaseSettingsWriteLockForTests();
                TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

                Settings persisted = new SettingsService(originalPrimaryDir, originalFallbackDir).LoadSettings();
                string? startupEntry = registry.GetValue(registryPath, registryValueName)?.ToString();

                Assert.True(harness.ViewModel.RunAtStartup);
                Assert.False(persisted.RunAtStartup);
                Assert.False(string.IsNullOrWhiteSpace(startupEntry));
                Assert.Contains("-startup", startupEntry, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(harness.Messages.ErrorMessages);
            }
            finally
            {
                harness.ViewModel.ReleaseSettingsWriteLockForTests();
            }
        });
    }

    [Fact]
    public void SaveSettingsAsync_ShowsWarning_AndSkipsSave_WhenOverlayDurationIsInvalid()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);

            Settings cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);

            harness.ViewModel.Hotkey.LoadFromString("Ctrl+Alt+S");
            harness.ViewModel.OverlayDurationSecondsText = "999";

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.SaveSettingsForTestsAsync());

            Settings current = Assert.IsType<Settings>(harness.ViewModel.CurrentSettings);
            Assert.Equal(cached.Overlay.DurationSeconds, current.Overlay.DurationSeconds);

            Settings persisted = harness.SettingsService.LoadSettings();
            Assert.Equal(cached.Overlay.DurationSeconds, persisted.Overlay.DurationSeconds);
            Assert.False(harness.ViewModel.IsSaving);
            Assert.Contains(
                harness.Messages.WarningMessages,
                entry => entry.caption == DialogText.Captions.InvalidOverlayDuration &&
                         entry.message == DialogText.Messages.InvalidOverlayDuration);
        });
    }

    private AppViewModelHarnessBuilder.AppViewModelInteractionHarness CreateHarness(
        Dispatcher dispatcher,
        IPerAppAudioRoutingResetter? perAppAudioRoutingResetter = null,
        Logger? logger = null,
        StartupService? startupService = null,
        bool allowBackgroundWork = false)
    {
        return AppViewModelHarnessBuilder.CreateInteractionHarness(
            _workspace,
            dispatcher,
            perAppAudioRoutingResetter,
            logger,
            startupService,
            allowBackgroundWork);
    }

    private static void EnsureApplication()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
    }

    private static Settings BuildCachedSettings()
    {
        return new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
            Theme = AppTheme.System,
            RunAtStartup = false,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "out-old-1", Name = "Old Speakers", DisplayOrder = 1 },
                        new CycleDevice { Id = "out-old-2", Name = "Old Headset", DisplayOrder = 2 }
                    ],
                    SwitchRoles = ["Multimedia", "Communications"],
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O",
                    ReverseSwitchHotkey = "Ctrl+Alt+Shift+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "in-old-1", Name = "Old Mic", DisplayOrder = 1 },
                        new CycleDevice { Id = "in-old-2", Name = "Backup Mic", DisplayOrder = 2 }
                    ],
                    SwitchRoles = ["Multimedia", "Communications"],
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+I",
                    ReverseSwitchHotkey = "Ctrl+Alt+Shift+I"
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
                    NextTrack = "Ctrl+Alt+.",
                    PreviousTrack = "Ctrl+Alt+,"
                },
                Mute = new HotkeysMuteSettings
                {
                    Mic = string.Empty,
                    Sound = string.Empty,
                    Deafen = string.Empty
                },
                Listen = new HotkeysListenSettings
                {
                    ListenToInput = string.Empty,
                    MonitorOutputDeviceId = string.Empty
                },
                Volume = new HotkeysVolumeSettings
                {
                    MasterVolumeStepPercent = 0,
                    MicVolumeStepPercent = 101
                }
            },
            Miscellaneous = new MiscellaneousSettings
            {
                PreserveAudioLevels = true,
                BluetoothReconnectEnabled = true,
                DeviceReferenceFileMode = DeviceReferenceFileMode.Off,
                LogLevel = "Info",
                AutoScrollToMixerOnRestore = true
            },
            Overlay = new OverlaySettings
            {
                Enabled = true,
                Position = OverlayPosition.BottomRight,
                DurationSeconds = 2.0
            }
        };
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, params T[] items)
    {
        collection.Clear();
        foreach (T item in items)
        {
            collection.Add(item);
        }
    }

    private static void SetKnownActiveDeviceInfos(
        AppViewModel viewModel,
        IReadOnlyList<CycleDevice> outputDevices,
        IReadOnlyList<CycleDevice> inputDevices)
    {
        viewModel.SetKnownActiveDeviceInfosForTests(outputDevices, inputDevices);
    }

    private static CycleDevice CloneCycleDevice(CycleDevice device)
    {
        return new CycleDevice
        {
            Id = device.Id,
            Name = device.Name,
            DisplayOrder = device.DisplayOrder,
        };
    }

    private static void WaitForQueuedBackgroundTasks(AppViewModel viewModel)
    {
        TestPrivateAccess.RunTaskOnDispatcher(viewModel.WaitForQueuedBackgroundTasksForTestsAsync());
    }

    public void Dispose()
    {
        AppViewModel.ResetTestHooks();
        AudioDeviceService.ResetTestHooks();
        _workspace.Dispose();
    }

    [GeneratedRegex(@"opId=(startup-registry:[0-9a-f]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
