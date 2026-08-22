using System.Text.Json.Nodes;
using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("AppDialogServiceIsolation")]
public sealed class AppViewModelCliRoutineTests : IDisposable
{
    private readonly TestSettingsWorkspace _workspace;

    public AppViewModelCliRoutineTests()
    {
        _workspace = new TestSettingsWorkspace(nameof(AppViewModelCliRoutineTests));
    }

    [Fact]
    public void DeviceAvailability_UsesStartupBaselineAndCoalescesAutomaticActions()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            using var harness = CreateCliFailureHarness();
            var routine = new AudioRoutine
            {
                Id = "devices",
                Name = "Devices",
                MasterVolumePercent = 30,
                Triggers = [new RoutineTrigger { Id = "first", Kind = RoutineTriggerKind.DeviceAvailability, Device = new() { Id = "out" } }, new() { Id = "input", Kind = RoutineTriggerKind.DeviceAvailability, Device = new() { Id = "in", Playback = false } }]
            };
            harness.SetCachedSettings(new Settings { Routines = new RoutinesSettings { Items = [routine] } });
            bool connected = false;
            int writes = 0;
            harness.ViewModel.RoutineExecutionOperationsForTests = new(
                playback => connected ? [new() { Id = playback ? "out" : "in" }] : [], (_, target) => target,
                (_, _, _, _, _) => throw new InvalidOperationException("Unexpected reconnect"),
                (_, _, _, _, _) => Task.CompletedTask,
                _ => throw new InvalidOperationException("Unexpected switch"),
                (_, _, _, _) => throw new InvalidOperationException("Unexpected routing"),
                (_, _, _, _) => { writes++; return true; });
            harness.ViewModel.EnableRoutineAppStartMonitoring();
            Assert.Equal(0, writes);
            connected = true;
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ExecuteDeviceChangeTriggeredRoutinesAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, writes);
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.ExecuteDeviceChangeTriggeredRoutinesAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, writes);
        });
    }

    [Fact]
    public void GuiBackedRoutineConditionFailure_MatchesHeadlessPreconditionAndKeepsAudioUntouched()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            using var harness = CreateCliFailureHarness();
            var routine = new AudioRoutine
            {
                Id = "conditional",
                Name = "Conditional",
                MasterVolumePercent = 30,
                Conditions = new() { Device = new() { Id = "missing", Playback = false } }
            };
            harness.SetCachedSettings(new Settings { Routines = new RoutinesSettings { Items = [routine] } });
            int writes = 0;
            harness.ViewModel.RoutineExecutionOperationsForTests = new(
                playback => { Assert.False(playback); return []; }, (_, target) => target,
                (_, _, _, _, _) => throw new InvalidOperationException("Unexpected reconnect"),
                (_, _, _, _, _) => Task.CompletedTask,
                _ => throw new InvalidOperationException("Unexpected switch"),
                (_, _, _, _) => throw new InvalidOperationException("Unexpected routing"),
                (_, _, _, _) => { writes++; return true; });
            var run = harness.ViewModel.RunRoutineFromCliAsync(routine.Id, true);
            TestPrivateAccess.RunTaskOnDispatcher(run);
            var result = run.GetAwaiter().GetResult();
            Assert.Equal(5, result.ExitCode);
            Assert.Contains("routine-condition-device-unavailable", result.Output);
            Assert.Equal(0, writes);
            Assert.Empty(TestPrivateAccess.GetField<RoutineLifetimeService>(harness.ViewModel, "_routineLifetime").Sessions);
        });
    }

    [Theory]
    [InlineData("hotkey", false)]
    [InlineData("tray", false)]
    [InlineData("cli", false)]
    [InlineData("hotkey", true)]
    [InlineData("tray", true)]
    [InlineData("cli", true)]
    [InlineData("automatic", false)]
    [InlineData("scheduled", false)]
    public void ApplicationRouting_TargetsConfiguredProcessAndKeepsAutomaticLifetime(string source, bool alreadyActive)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            using var harness = CreateCliFailureHarness();
            var routine = new AudioRoutine
            {
                Id = "manual-app",
                Name = "Music",
                Enabled = true,
                TriggerKind = source == "scheduled" ? RoutineTriggerKind.Scheduled : RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Trigger.exe",
                TargetAppPath = @"C:\Apps\Player.exe",
                OutputDeviceId = "speakers",
                InputDeviceId = "mic",
                SwitchOutputPerApp = true,
                RestorePreviousAudioOnDeactivate = true,
                ShowInTrayMenu = true,
                Hotkey = "Ctrl+Alt+R",
            };
            var settings = new Settings { Routines = new RoutinesSettings { Items = [routine] } };
            harness.SetCachedSettings(settings);
            var processes = new FakeRoutineProcessSnapshotProvider();
            processes.CaptureAllSnapshots.Add(new(42, @"C:\Apps\Trigger.exe"));
            processes.CaptureAllSnapshots.Add(new(84, @"C:\Apps\Player.exe"));
            processes.SetTryCaptureResult(processes.CaptureAllSnapshots[0]);
            processes.SetTryCaptureResult(processes.CaptureAllSnapshots[1]);
            TestPrivateAccess.SetField(harness.ViewModel, "_routineProcessSnapshotProvider", processes);
            var lifetime = TestPrivateAccess.GetField<RoutineLifetimeService>(harness.ViewModel, "_routineLifetime");
            RoutineStatefulSession? original = alreadyActive
                ? lifetime.Register(routine, 42, new("old-out", "Old speakers", "old-in", "Old microphone")) : null;
            var writes = new List<(bool Playback, string DeviceId, uint ProcessId)>();
            harness.ViewModel.RoutineExecutionOperationsForTests = new(
                _ => [], (_, target) => target,
                (_, _, _, _, _) => throw new InvalidOperationException("Unexpected reconnect."),
                (_, _, _, _, _) => Task.CompletedTask,
                _ => throw new InvalidOperationException("Manual app routing changed system defaults."),
                (playback, target, pid, _) =>
                {
                    writes.Add((playback, target.Id, pid));
                    return ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Applied, target.Id));
                }, (_, _, _, _) => throw new InvalidOperationException("Unexpected volume change."));

            Task run = source switch
            {
                "hotkey" => TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel, "ExecuteRoutineFromHotkeyAsync", routine),
                "tray" => harness.ViewModel.RunRoutineFromTrayAsync(routine.Id),
                "automatic" or "scheduled" => TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel, "ExecuteRoutineForResolvedProcessAsync", routine, source == "automatic" ? 42 : 0, false, source, null, source == "automatic", TestContext.Current.CancellationToken),
                _ => harness.ViewModel.RunRoutineFromCliAsync(routine.Id, false),
            };
            TestPrivateAccess.RunTaskOnDispatcher(run);
            Assert.Equal([(true, "speakers", 84u), (false, "mic", 84u)], writes);
            if (source == "automatic")
            {
                var session = Assert.Single(lifetime.Sessions);
                Assert.Equal(42, session.RootProcessId);
                Assert.Equal(84, session.RoutingLease!.RootProcessId);
                routine.Triggers = [.. routine.Triggers, new() { Id = "steam", Kind = RoutineTriggerKind.SteamBigPicture },
                    new() { Id = "schedule", Kind = RoutineTriggerKind.Scheduled }];
                AudioRoutine[] triggers = [.. routine.ExpandAutomaticTriggers()];
                foreach (AudioRoutine trigger in triggers.Skip(1))
                    TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel,
                        "ExecuteRoutineForResolvedProcessAsync", trigger, 0, false, "automatic", null,
                        trigger.IsStatefulTrigger, TestContext.Current.CancellationToken));
                Assert.Equal(2, writes.Count);
                Assert.Equal(2, lifetime.Sessions.Count);
                var resets = new List<(uint, bool, bool)>();
                harness.ViewModel.ResetRoutineRoutingForTests = (pid, output, input) => { resets.Add((pid, output, input)); return true; };
                var endings = lifetime.CaptureDeactivations(static _ => true);
                TestPrivateAccess.RunTaskOnDispatcher(lifetime.DeactivateAsync(endings[0],
                    (ended, restore) => TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel, "ReleaseRoutineApplicationRoutingAsync", ended, restore)));
                Assert.Empty(resets);
                Assert.Single(lifetime.GetLeases());
                TestPrivateAccess.RunTaskOnDispatcher(lifetime.DeactivateAsync(endings[1],
                    (ended, restore) => TestPrivateAccess.InvokeNonPublicTask(harness.ViewModel, "ReleaseRoutineApplicationRoutingAsync", ended, restore)));
                Assert.Equal((84u, true, true), Assert.Single(resets));
                Assert.Empty(lifetime.GetLeases());
            }
            else if (alreadyActive) Assert.Same(original, Assert.Single(lifetime.Sessions));
            else Assert.Empty(lifetime.Sessions);
        });
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WithAmbiguousName_ReturnsPreconditionFailure()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        MasterVolumePercent = 35,
                    },
                    new AudioRoutine
                    {
                        Id = "routine-2",
                        Name = "Desk",
                        Enabled = true,
                        MasterVolumePercent = 35,
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Desk", jsonOutput: false);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-selector-ambiguous] Multiple routines match 'Desk'. Use the routine id instead.", result.Output);
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WithAmbiguousName_JsonOutput_ReturnsErrorEnvelope()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        MasterVolumePercent = 35,
                    },
                    new AudioRoutine
                    {
                        Id = "routine-2",
                        Name = "Desk",
                        Enabled = true,
                        MasterVolumePercent = 35,
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Desk", jsonOutput: true);

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject root = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.GetValue<string>());
        Assert.Equal("routine-selector-ambiguous", root["data"]?["error"]?["code"]?.GetValue<string>());
        Assert.Equal("Multiple routines match 'Desk'. Use the routine id instead.", root["data"]?["error"]?["message"]?.GetValue<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.GetValue<int>());
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WhenRoutineDisabled_ReturnsPreconditionFailure()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = false,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Desk", jsonOutput: false);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-disabled] Routine 'Desk' is disabled.", result.Output);
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WhenRoutineDisabled_JsonOutput_ReturnsErrorEnvelope()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = false,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers"
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Desk", jsonOutput: true);

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject root = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("routine-disabled", root["data"]?["error"]?["code"]?.GetValue<string>());
        Assert.Equal("Routine 'Desk' is disabled.", root["data"]?["error"]?["message"]?.GetValue<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.GetValue<int>());
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WhenRoutineHasNoTargets_ReturnsPreconditionFailure()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Desk", jsonOutput: false);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-has-no-targets] Routine 'Desk' has no configured targets.", result.Output);
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WhenRoutineHasNoTargets_JsonOutput_ReturnsErrorEnvelope()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Desk", jsonOutput: true);

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject root = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("routine-has-no-targets", root["data"]?["error"]?["code"]?.GetValue<string>());
        Assert.Equal("Routine 'Desk' has no configured targets.", root["data"]?["error"]?["message"]?.GetValue<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.GetValue<int>());
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WhenAppStartRoutineTargetNotRunning_ReturnsPreconditionFailure()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
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
                        TriggerAppPath = @"C:\DefinitelyMissing\MissingApp.exe",
                        SwitchOutputPerApp = true,
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Spotify", jsonOutput: false);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-target-app-not-running] Routine 'Spotify' requires the target application 'MissingApp' to be running.", result.Output);
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WhenAppStartRoutineTargetNotRunning_JsonOutputReturnsErrorEnvelope()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
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
                        TriggerAppPath = @"C:\DefinitelyMissing\MissingApp.exe",
                        SwitchOutputPerApp = true,
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Spotify", jsonOutput: true);

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject root = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("routine-target-app-not-running", root["data"]?["error"]?["code"]?.GetValue<string>());
        Assert.Equal("Routine 'Spotify' requires the target application 'MissingApp' to be running.", root["data"]?["error"]?["message"]?.GetValue<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.GetValue<int>());
        Assert.Equal("routine-1", root["data"]?["error"]?["routineId"]?.GetValue<string>());
        Assert.Equal("Spotify", root["data"]?["error"]?["routineName"]?.GetValue<string>());
        Assert.Equal("Application launch", root["data"]?["error"]?["triggerMode"]?.GetValue<string>());
        Assert.True(root["data"]?["error"]?["usesApplicationTrigger"]?.GetValue<bool>());
        Assert.Equal(@"C:\DefinitelyMissing\MissingApp.exe", root["data"]?["error"]?["triggerAppPath"]?.GetValue<string>());
        Assert.Equal("MissingApp", root["data"]?["error"]?["targetApplicationName"]?.GetValue<string>());
        Assert.True(root["data"]?["error"]?["requiresRunningTargetApplication"]?.GetValue<bool>());
        Assert.True(root["data"]?["error"]?["switchOutputPerApp"]?.GetValue<bool>());
        Assert.Equal("out-1", root["data"]?["error"]?["outputDeviceId"]?.GetValue<string>());
    }

    [Fact]
    public async Task RunRoutineFromCliAsync_WhenPackagedAppTargetNotRunning_ReturnsPreconditionFailure()
    {
        var viewModel = CreateViewModel(new Settings
        {
            Routines = new RoutinesSettings
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
                        TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                        SwitchOutputPerApp = true,
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Spotify", jsonOutput: false);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-target-app-not-running] Routine 'Spotify' requires the target application 'SpotifyAB SpotifyMusic' to be running.", result.Output);
    }

    [Fact]
    public void RunRoutineFromCliAsync_WhenRoutineSwitchFails_IncludesFailureDetailsInTextOutput()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var harness = CreateCliFailureHarness();
            Settings settings = new()
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "routine-1",
                            Name = "Desk",
                            Enabled = true,
                            OutputDeviceId = "missing-output-device",
                            OutputDeviceName = "Headset",
                            InputDeviceId = "missing-input-device",
                            InputDeviceName = "Microphone",
                        }
                    ]
                }
            };

            harness.SettingsService.SaveSettings(settings);
            harness.SetCachedSettings(settings);

            Task<CliExecutionResult> runRoutineTask = harness.ViewModel.RunRoutineFromCliAsync("Desk", jsonOutput: false);
            TestPrivateAccess.RunTaskOnDispatcher(runRoutineTask);
            CliExecutionResult result = runRoutineTask.GetAwaiter().GetResult();

            Assert.Equal(3, result.ExitCode);
            Assert.NotNull(result.Output);
            Assert.Contains("[diag-code:routine-run-failed] Failed to run routine 'Desk'.", result.Output, StringComparison.Ordinal);
            Assert.Contains("Output failure:", result.Output, StringComparison.Ordinal);
            Assert.Contains("Input failure:", result.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RunRoutineFromCliAsync_WhenRoutineSwitchFails_IncludesFailureDetailsInJsonOutput()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var harness = CreateCliFailureHarness();
            Settings settings = new()
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "routine-1",
                            Name = "Desk",
                            Enabled = true,
                            OutputDeviceId = "missing-output-device",
                            OutputDeviceName = "Headset",
                            InputDeviceId = "missing-input-device",
                            InputDeviceName = "Microphone",
                        }
                    ]
                }
            };

            harness.SettingsService.SaveSettings(settings);
            harness.SetCachedSettings(settings);

            Task<CliExecutionResult> runRoutineTask = harness.ViewModel.RunRoutineFromCliAsync("Desk", jsonOutput: true);
            TestPrivateAccess.RunTaskOnDispatcher(runRoutineTask);
            CliExecutionResult result = runRoutineTask.GetAwaiter().GetResult();

            Assert.Equal(3, result.ExitCode);
            Assert.NotNull(result.Output);

            JsonObject root = JsonNode.Parse(result.Output!)!.AsObject();
            JsonObject? error = root["data"]?["error"] as JsonObject;
            Assert.NotNull(error);
            Assert.False(string.IsNullOrWhiteSpace(error!["outputFailureDetail"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(error["inputFailureDetail"]?.GetValue<string>()));
            Assert.False(error["outputSucceeded"]?.GetValue<bool?>());
            Assert.False(error["inputSucceeded"]?.GetValue<bool?>());
        });
    }

    [Theory]
    [InlineData(RoutineTriggerKind.Hotkey)]
    [InlineData(RoutineTriggerKind.SteamBigPicture)]
    public void RunRoutineFromCliAsync_WhenRoutineHasOnlyVolumeTargets_DoesNotFailPrecondition(RoutineTriggerKind trigger)
    {
        TestExecutionGuards.RunSta(() =>
        {
            bool invoked = false;
            AppViewModel.ApplyRoutineAbsoluteVolumeOverrideForTests = (playback, targetDeviceId, targetPercent, _) =>
            {
                invoked = true;
                Assert.True(playback);
                Assert.Null(targetDeviceId);
                Assert.Equal(35, targetPercent);
                return true;
            };

            using var harness = CreateCliFailureHarness();
            Settings settings = new()
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "routine-1",
                            Name = "Desk",
                            Enabled = true,
                            MasterVolumePercent = 35,
                            TriggerKind = trigger,
                        }
                    ]
                }
            };

            harness.SettingsService.SaveSettings(settings);
            harness.SetCachedSettings(settings);

            typeof(AppViewModel).GetMethod("RefreshRoutineRuntimeTriggers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(harness.ViewModel, null);

            Task<CliExecutionResult> runRoutineTask = harness.ViewModel.RunRoutineFromCliAsync("Desk", jsonOutput: false);
            TestPrivateAccess.RunTaskOnDispatcher(runRoutineTask);
            CliExecutionResult result = runRoutineTask.GetAwaiter().GetResult();

            Assert.NotEqual(5, result.ExitCode);
            Assert.DoesNotContain("routine-has-no-targets", result.Output ?? string.Empty, StringComparison.Ordinal);
            Assert.True(invoked);
        });
    }

    public void Dispose()
    {
        AppViewModel.ResetTestHooks();
        _workspace.Dispose();
    }

    private AppViewModel CreateViewModel(Settings settings)
    {
        return AppViewModelHarnessBuilder.CreateSettingsBackedViewModelShell(_workspace.PrimaryDir, _workspace.FallbackDir, settings, Logger.Instance);
    }

    private AppViewModelHarnessBuilder.AppViewModelInteractionHarness CreateCliFailureHarness()
    {
        var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
        var reconnectService = new FakeBluetoothReconnectService { NextResult = false };
        var reconnectCoordinator = new BluetoothReconnectCoordinator(reconnectService, Logger.Instance);
        TestPrivateAccess.SetField(harness.ViewModel, "_routineBluetoothReconnectCoordinator", reconnectCoordinator);
        return harness;
    }
}
