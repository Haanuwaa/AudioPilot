using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineSaveTests : IDisposable
{
    private readonly TestSettingsWorkspace _workspace;

    public AppViewModelRoutineSaveTests()
    {
        _workspace = new TestSettingsWorkspace(nameof(AppViewModelRoutineSaveTests));
    }

    [Fact]
    public void SaveRoutinesAsync_UpdatesCachedAndPersistedSettings_AndRefreshesRoutineHotkeys()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var harness = AppViewModelHarnessBuilder.CreateRoutineSaveHarness(_workspace, Dispatcher.CurrentDispatcher, Logger.Instance);
            SettingsService settingsService = harness.SettingsService;
            HotkeyService hotkeys = harness.Hotkeys;
            AppViewModel viewModel = harness.ViewModel;

            var cachedSettings = new Settings
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "old-routine",
                            Name = "Old Routine",
                            Enabled = true,
                            OutputDeviceId = "out-old",
                            OutputDeviceName = "Old Speakers",
                            Hotkey = "Ctrl+Alt+O"
                        }
                    ]
                }
            };

            TestPrivateAccess.SetField(viewModel, "_cachedSettings", cachedSettings);

            ObservableCollection<AudioRoutine> routines = viewModel.Routines;
            routines.Add(new AudioRoutine
            {
                Id = "routine-2",
                Name = "Routine 2",
                Enabled = true,
                DisplayOrder = 2,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
                InputDeviceId = "in-2",
                InputDeviceName = "Studio Mic",
                Hotkey = string.Empty,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                SwitchOutputPerApp = true,
            });
            routines.Add(new AudioRoutine
            {
                Id = "routine-1",
                Name = "Routine 1",
                Enabled = true,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                Hotkey = "Ctrl+Alt+T",
                ShowInTrayMenu = true
            });

            var native = new RecordingAppDialogService();
            TestPrivateAccess.SetField(viewModel, "_dialogs", native);
            try
            {
                TestPrivateAccess.RunTaskOnDispatcher(viewModel.SaveRoutinesAsync());

                Settings? currentSettings = viewModel.CurrentSettings;
                Assert.NotNull(currentSettings);
                Assert.False(viewModel.HasUnsavedRoutineChanges);

                Assert.Collection(
                    currentSettings!.Routines.Items,
                    first =>
                    {
                        Assert.Equal("routine-2", first.Id);
                        Assert.Equal("Routine 2", first.Name);
                        Assert.Equal(1, first.DisplayOrder);
                        Assert.True(first.UsesApplicationTrigger);
                        Assert.Equal(@"C:\Apps\Discord\Discord.exe", first.TriggerAppPath);
                        Assert.True(first.SwitchOutputPerApp);
                    },
                    second =>
                    {
                        Assert.Equal("routine-1", second.Id);
                        Assert.Equal("Routine 1", second.Name);
                        Assert.Equal(2, second.DisplayOrder);
                        Assert.Equal("Ctrl+Alt+T", second.Hotkey);
                        Assert.True(second.ShowInTrayMenu);
                    });

                Settings persisted = settingsService.LoadSettings();
                Assert.Collection(
                    persisted.Routines.Items,
                    first =>
                    {
                        Assert.Equal("routine-2", first.Id);
                        Assert.True(first.UsesApplicationTrigger);
                        Assert.Equal(@"C:\Apps\Discord\Discord.exe", first.TriggerAppPath);
                        Assert.True(first.SwitchOutputPerApp);
                    },
                    second =>
                    {
                        Assert.Equal("routine-1", second.Id);
                        Assert.Equal("Ctrl+Alt+T", second.Hotkey);
                        Assert.True(second.ShowInTrayMenu);
                    });

                List<(int Id, string Description)> registrations = TestPrivateAccess.GetRegisteredHotkeys(hotkeys);
                Assert.DoesNotContain(registrations, entry => entry.Description.Contains("Discord.exe", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(registrations, entry => entry.Id == 10000 && entry.Description.Contains("Ctrl+Alt+T", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(registrations, entry => entry.Description.Contains("Ctrl+Alt+O", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
            }
        });
    }

    [Fact]
    public void SaveRoutinesAsync_DoesNotPersist_WhenConflictConfirmationIsDeclined()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var harness = AppViewModelHarnessBuilder.CreateRoutineSaveHarness(_workspace, Dispatcher.CurrentDispatcher, Logger.Instance);
            SettingsService settingsService = harness.SettingsService;
            AppViewModel viewModel = harness.ViewModel;

            Settings cachedSettings = new() { Routines = new RoutinesSettings { Items = [] } };
            TestPrivateAccess.SetField(viewModel, "_cachedSettings", cachedSettings);

            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            });
            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            });

            var native = new RecordingAppDialogService
            {
                YesNoResponse = AppDialogResult.Declined,
            };
            TestPrivateAccess.SetField(viewModel, "_dialogs", native);
            try
            {
                TestPrivateAccess.RunTaskOnDispatcher(viewModel.SaveRoutinesAsync());

                Assert.Single(native.YesNoMessages);
                Assert.Empty(settingsService.LoadSettings().Routines.Items);
                Assert.True(viewModel.HasUnsavedRoutineChanges);
            }
            finally
            {
            }
        });
    }

    [Fact]
    public void SaveRoutinesAsync_AutoSaveMode_SkipsConflictPrompt_AndLeavesEditsPending()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var harness = AppViewModelHarnessBuilder.CreateRoutineSaveHarness(_workspace, Dispatcher.CurrentDispatcher, Logger.Instance);
            SettingsService settingsService = harness.SettingsService;
            AppViewModel viewModel = harness.ViewModel;

            Settings cachedSettings = new() { Routines = new RoutinesSettings { Items = [] } };
            TestPrivateAccess.SetField(viewModel, "_cachedSettings", cachedSettings);

            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            });
            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            });

            var native = new RecordingAppDialogService();
            TestPrivateAccess.SetField(viewModel, "_dialogs", native);
            try
            {
                TestPrivateAccess.RunTaskOnDispatcher(
                    TestPrivateAccess.InvokeNonPublicTask(viewModel, "SaveRoutinesAsync", false, true));

                Assert.Empty(native.YesNoMessages);
                Assert.Empty(native.SuccessMessages);
                Assert.Empty(native.WarningMessages);
                Assert.Empty(settingsService.LoadSettings().Routines.Items);
                Assert.True(viewModel.HasUnsavedRoutineChanges);
            }
            finally
            {
            }
        });
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
