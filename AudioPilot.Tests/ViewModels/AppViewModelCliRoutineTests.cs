using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using Newtonsoft.Json.Linq;

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

        JObject root = JObject.Parse(result.Output!);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.Equal("routine-selector-ambiguous", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Multiple routines match 'Desk'. Use the routine id instead.", root["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.Value<int>());
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

        JObject root = JObject.Parse(result.Output!);
        Assert.Equal("routine-disabled", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Routine 'Desk' is disabled.", root["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.Value<int>());
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

        JObject root = JObject.Parse(result.Output!);
        Assert.Equal("routine-has-no-targets", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Routine 'Desk' has no configured targets.", root["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.Value<int>());
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
        Assert.Equal("[diag-code:routine-trigger-app-not-running] Routine 'Spotify' requires the target application 'MissingApp' to be running.", result.Output);
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

        JObject root = JObject.Parse(result.Output!);
        Assert.Equal("routine-trigger-app-not-running", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Routine 'Spotify' requires the target application 'MissingApp' to be running.", root["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.Value<int>());
        Assert.Equal("routine-1", root["data"]?["error"]?["routineId"]?.Value<string>());
        Assert.Equal("Spotify", root["data"]?["error"]?["routineName"]?.Value<string>());
        Assert.Equal("Application launch", root["data"]?["error"]?["triggerMode"]?.Value<string>());
        Assert.True(root["data"]?["error"]?["usesApplicationTrigger"]?.Value<bool>());
        Assert.Equal(@"C:\DefinitelyMissing\MissingApp.exe", root["data"]?["error"]?["triggerAppPath"]?.Value<string>());
        Assert.Equal("MissingApp", root["data"]?["error"]?["triggerApplicationName"]?.Value<string>());
        Assert.True(root["data"]?["error"]?["requiresRunningTriggerProcess"]?.Value<bool>());
        Assert.True(root["data"]?["error"]?["switchOutputPerApp"]?.Value<bool>());
        Assert.Equal("out-1", root["data"]?["error"]?["outputDeviceId"]?.Value<string>());
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
                    }
                ]
            }
        });

        CliExecutionResult result = await viewModel.RunRoutineFromCliAsync("Spotify", jsonOutput: false);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-trigger-app-not-running] Routine 'Spotify' requires the target application 'SpotifyAB SpotifyMusic' to be running.", result.Output);
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

            JObject root = JObject.Parse(result.Output!);
            JObject? error = root["data"]?["error"] as JObject;
            Assert.NotNull(error);
            Assert.False(string.IsNullOrWhiteSpace(error!["outputFailureDetail"]?.Value<string>()));
            Assert.False(string.IsNullOrWhiteSpace(error["inputFailureDetail"]?.Value<string>()));
            Assert.False(error["outputSucceeded"]?.Value<bool?>());
            Assert.False(error["inputSucceeded"]?.Value<bool?>());
        });
    }

    [Fact]
    public void RunRoutineFromCliAsync_WhenRoutineHasOnlyVolumeTargets_DoesNotFailPrecondition()
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
                        }
                    ]
                }
            };

            harness.SettingsService.SaveSettings(settings);
            harness.SetCachedSettings(settings);

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
        var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
        var reconnectService = new FakeBluetoothReconnectService { NextResult = false };
        var reconnectCoordinator = new BluetoothReconnectCoordinator(reconnectService, Logger.Instance);
        TestPrivateAccess.SetField(harness.ViewModel, "_routineBluetoothReconnectCoordinator", reconnectCoordinator);
        return harness;
    }
}
