using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RoutineCommunicationsFailure_IdentifiesFailedFlow(bool playback)
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            DeviceSwitching = new() { BluetoothReconnectEnabled = false },
            Routines = new()
            {
                Items = [new()
            {
                Id = "calls", Name = "Calls",
                CommunicationsOutput = playback ? new() { Id = "out" } : null,
                CommunicationsInput = playback ? null : new() { Id = "in", Playback = false },
            }]
            },
        }, audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetDefaultPlaybackMute: () => false, GetDefaultCaptureMute: () => false,
            SwitchAudioDeviceAsync: (_, _, _, _, _, _) => (false, null),
            SwitchInputDeviceToAsync: (_, _, _) => (false, null)));
        var result = await scope.Runner.ExecuteAsync(new CliCommand { Action = CliAction.RoutineRun, Key = "calls" });
        Assert.Equal(3, result.ExitCode);
        Assert.Contains(playback ? "communications-output" : "communications-input", result.Output);
    }

    [Fact]
    public async Task RoutineConditionFailure_ReturnsPreconditionCodeWithoutVolumeWrites()
    {
        int writes = 0;
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items = [new()
            { Id = "conditional", Name = "Conditional", MasterVolumePercent = 30,
                Conditions = new() { Device = new() { Id = "missing", Playback = false } } }]
            }
        },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetDefaultPlaybackMute: () => false, GetDefaultCaptureMute: () => false,
                GetActiveInputDeviceInfos: () => [],
                TrySetPlaybackVolume: _ => { writes++; return (true, 30, false); }));
        var result = await scope.Runner.ExecuteAsync(new CliCommand { Action = CliAction.RoutineRun, Key = "conditional", JsonOutput = true });
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("routine-condition-device-unavailable", result.Output);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExecuteAsync_RoutineRun_AppliesVolumeOnlyRoutine_AndReportsFailure(bool playback, bool applySuccess)
    {
        int writes = 0;
        (bool Success, float Percent, bool Muted) Apply(float percent)
        {
            writes++;
            Assert.Equal(35f, percent);
            return (applySuccess, percent, false);
        }
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items = [new AudioRoutine
                {
                    Id = "volume-only", Name = "Quiet", Enabled = true,
                    MasterVolumePercent = playback ? 35 : null,
                    MicVolumePercent = playback ? null : 35,
                }]
            }
        }, audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetDefaultPlaybackMute: () => false, GetDefaultCaptureMute: () => false,
            TrySetPlaybackVolume: Apply, TrySetCaptureVolume: Apply));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand { Action = CliAction.RoutineRun, Key = "Quiet" });

        Assert.Equal(1, writes);
        Assert.Equal(applySuccess ? 0 : 3, result.ExitCode);
        if (!applySuccess)
            Assert.Contains(playback ? "Master volume could not be applied." : "Microphone volume could not be applied.", result.Output, StringComparison.Ordinal);
        JsonNode history = JsonNode.Parse(scope.Runner.GetDiagnosticsHistory(jsonOutput: true, limit: 1, type: "routine", redactOutput: false))!;
        JsonNode entry = Assert.Single(history["data"]!["entries"]!.AsArray())!;
        Assert.Equal(applySuccess, entry["success"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExecuteAsync_RoutineRun_AssignsAllRoles_WhenDefaultMissingOrOnlyOneRoleMatches(bool output, bool missingDefault)
    {
        const string targetId = "routine-role-target";
        NRole[] roles = [NRole.Console, NRole.Multimedia, NRole.Communications];
        var assignments = roles.ToDictionary(role => role, role =>
            missingDefault ? null : role == NRole.Multimedia ? targetId : $"old-{role}");
        var appliedRoles = new List<NRole>();
        string? ReadDefault(NRole role) => assignments[role]
            ?? throw new COMException("No default endpoint", unchecked((int)0x80070490));
        void ApplyRole(string target, NRole role)
        {
            appliedRoles.Add(role);
            assignments[role] = target;
        }
        (bool Success, string? DeviceName) SwitchRoles(string target, string? opId)
        {
            Assert.Equal(targetId, target);
            Task<bool> operation = output
                ? DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(target, roles, ApplyRole, ReadDefault,
                    Logger.Instance, opId!, nameof(ExecuteAsync_RoutineRun_AssignsAllRoles_WhenDefaultMissingOrOnlyOneRoleMatches), TestContext.Current.CancellationToken)
                : DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(target, "Target", roles, ApplyRole, ReadDefault,
                    Logger.Instance, opId!, nameof(ExecuteAsync_RoutineRun_AssignsAllRoles_WhenDefaultMissingOrOnlyOneRoleMatches),
                    emitVerifyRetryWarning: false, traceComRetry: false, TestContext.Current.CancellationToken);
            return (operation.GetAwaiter().GetResult(), "Target");
        }

        using var scope = new HeadlessRunnerScope(new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings { BluetoothReconnectEnabled = false, PreserveAudioLevels = false },
            Routines = new RoutinesSettings
            {
                Items = [new AudioRoutine
                {
                    Id = "routine-roles", Name = "Desk", Enabled = true,
                    OutputDeviceId = output ? targetId : string.Empty,
                    InputDeviceId = output ? string.Empty : targetId,
                }]
            }
        }, audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetDefaultPlaybackDeviceSnapshot: () => (assignments[NRole.Multimedia], "Target"),
            GetDefaultPlaybackMute: () => false, GetDefaultCaptureMute: () => false,
            SwitchAudioDeviceAsync: (target, _, _, _, _, opId) => SwitchRoles(target, opId),
            SwitchInputDeviceToAsync: (target, _, opId) => SwitchRoles(target, opId)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand { Action = CliAction.RoutineRun, Key = "Desk" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(roles, appliedRoles);
        Assert.All(assignments.Values, value => Assert.Equal(targetId, value));
        JsonNode history = JsonNode.Parse(scope.Runner.GetDiagnosticsHistory(jsonOutput: true, limit: 10, type: "routine", redactOutput: false))!;
        JsonNode entry = Assert.Single(history["data"]!["entries"]!.AsArray())!;
        Assert.True(entry[output ? "outputSucceeded" : "inputSucceeded"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_RoutineRun_ReportsSwitchFailure_EvenWhenDetectionRoleAlreadyMatches(bool output)
    {
        int switchCalls = 0;
        (bool Success, string? DeviceName) FailSwitch()
        {
            switchCalls++;
            return (false, null);
        }
        using var scope = new HeadlessRunnerScope(new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings { BluetoothReconnectEnabled = false },
            Routines = new RoutinesSettings
            {
                Items = [new AudioRoutine
                {
                    Id = "routine-roles", Name = "Desk", Enabled = true,
                    OutputDeviceId = output ? "target" : string.Empty,
                    InputDeviceId = output ? string.Empty : "target",
                }]
            }
        }, audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetDefaultPlaybackDeviceSnapshot: () => ("target", "Target"),
            GetDefaultPlaybackMute: () => false, GetDefaultCaptureMute: () => false,
            SwitchAudioDeviceAsync: (_, _, _, _, _, _) => FailSwitch(),
            SwitchInputDeviceToAsync: (_, _, _) => FailSwitch()));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand { Action = CliAction.RoutineRun, Key = "Desk" });

        Assert.Equal(1, switchCalls);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains(output ? "Output failure:" : "Input failure:", result.Output, StringComparison.Ordinal);
    }


    [Fact]
    public void FormatRoutineRunResult_TrayOnlyUsesManualTriggerLabel()
    {
        var routine = new AudioRoutine { ShowInTrayMenu = true, MasterVolumePercent = 50 };
        JsonObject root = JsonNode.Parse(CliOutputFormatter.FormatRoutineRunResult(routine, null, null, jsonOutput: true))!.AsObject();
        Assert.Equal("Manual", root["data"]?["triggerMode"]?.GetValue<string>());
        Assert.Equal("Tray menu", root["data"]?["triggerSummary"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_RoutineExport_WritesJsonFile()
    {
        string exportPath = Path.Combine(Path.GetTempPath(), $"audiopilot-routines-{Guid.NewGuid():N}.json");

        try
        {
            using var scope = new HeadlessRunnerScope(
                new Settings
                {
                    Routines = new RoutinesSettings
                    {
                        Items =
                        [
                            new AudioRoutine
                            {
                                Id = "routine-1",
                                Name = "Desk",
                                OutputDeviceId = "out-1",
                                OutputDeviceName = "Speakers",
                            },
                        ]
                    }
                });

            CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
            {
                Action = CliAction.RoutineExport,
                Key = exportPath,
                AllowAnyPath = true,
                JsonOutput = true,
            });

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(exportPath));

            JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
            Assert.True(parsed["data"]?["success"]?.GetValue<bool>());
            Assert.Equal(1, parsed["data"]?["routineCount"]?.GetValue<int>());

            JsonObject exported = JsonNode.Parse(File.ReadAllText(exportPath))!.AsObject();
            Assert.Equal("1.0.0", exported["SchemaVersion"]?.GetValue<string>());
            Assert.Equal("routine-1", exported["Routines"]?.AsArray().FirstOrDefault()?["Id"]?.GetValue<string>());
        }
        finally
        {
            if (File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }
        }
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WithAmbiguousName_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
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
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Speakers"
                },
                new AudioRoutine
                {
                    Id = "routine-2",
                    Name = "Desk",
                    Enabled = true,
                    OutputDeviceId = "out-2",
                    OutputDeviceName = "Headset"
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-selector-ambiguous] Multiple routines match 'Desk'. Use the routine id instead.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenRoutineDisabled_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
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

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-disabled] Routine 'Desk' is disabled.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenRoutineHasNoTargets_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
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

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-has-no-targets] Routine 'Desk' has no configured targets.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenAppStartRoutineTargetNotRunning_ReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(new Settings
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

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Spotify",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:routine-target-app-not-running] Routine 'Spotify' requires the target application 'MissingApp' to be running.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_WhenAppStartRoutineTargetNotRunning_JsonIncludesRoutineMetadata()
    {
        using var scope = new HeadlessRunnerScope(new Settings
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

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Spotify",
            JsonOutput = true,
        });

        Assert.Equal(5, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject root = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("routine-target-app-not-running", root["data"]?["error"]?["code"]?.GetValue<string>());
        Assert.Equal("routine-1", root["data"]?["error"]?["routineId"]?.GetValue<string>());
        Assert.Equal("Spotify", root["data"]?["error"]?["routineName"]?.GetValue<string>());
        Assert.Equal("Application launch", root["data"]?["error"]?["triggerMode"]?.GetValue<string>());
        Assert.Equal("MissingApp", root["data"]?["error"]?["targetApplicationName"]?.GetValue<string>());
        Assert.True(root["data"]?["error"]?["requiresRunningTargetApplication"]?.GetValue<bool>());
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_AttemptsInputReconnect_AfterOutputFailure()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService { NextResult = false };
        using var scope = new HeadlessRunnerScope(new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                BluetoothReconnectEnabled = true
            },
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
                    OutputDeviceName = "Bluetooth Headset",
                    InputDeviceId = "missing-input-device",
                    InputDeviceName = "Bluetooth Microphone",
                }
            ]
            }
        }, fakeReconnectService);

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("[diag-code:routine-run-failed] Failed to run routine 'Desk'.", result.Output, StringComparison.Ordinal);
        Assert.Contains("Output failure:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Input failure:", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, fakeReconnectService.Calls);
        Assert.Equal(["output", "input"], fakeReconnectService.Kinds);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_IncludesOutputExceptionFailureDetail()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings
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
                            OutputDeviceId = "out-2",
                            OutputDeviceName = "Headset"
                        }
                    ]
                }
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetDefaultPlaybackDeviceSnapshot: static () => ("out-1", "Speakers"),
                SwitchAudioDeviceAsync: static (_, _, _, _, _, _) => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Output failure: Output switch threw InvalidOperationException.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_IncludesInputExceptionFailureDetail()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings
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
                            InputDeviceId = "in-2",
                            InputDeviceName = "Microphone"
                        }
                    ]
                }
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                SwitchInputDeviceToAsync: static (_, _, _) => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Input failure: Input switch threw InvalidOperationException.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineRun_PartialFailureHistory_UsesPartialDiagCode()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings
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
                            OutputDeviceId = "out-2",
                            OutputDeviceName = "Headset",
                            InputDeviceId = "in-2",
                            InputDeviceName = "Microphone"
                        }
                    ]
                }
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetDefaultPlaybackDeviceSnapshot: static () => ("out-1", "Speakers"),
                SwitchAudioDeviceAsync: static (_, _, _, _, _, _) => (true, "Headset"),
                SwitchInputDeviceToAsync: static (_, _, _) => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);

        string historyJson = scope.Runner.GetDiagnosticsHistory(jsonOutput: true, limit: 10, type: "routine", redactOutput: false);
        JsonObject history = JsonNode.Parse(historyJson)!.AsObject();
        JsonNode entry = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(history["data"]?["entries"])));
        Assert.Equal("routine-run-partial", entry["diagCode"]?.GetValue<string>());
        Assert.True(entry["outputSucceeded"]?.GetValue<bool>());
        Assert.False(entry["inputSucceeded"]?.GetValue<bool>());
    }


    [Fact]
    public async Task ExecuteAsync_RoutineRun_IncludesPerAppDeferredFailureDetail()
    {
        string currentProcessPath = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path is unavailable.");

        using var scope = new HeadlessRunnerScope(
            new Settings
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
                            UsesApplicationTrigger = true,
                            TriggerAppPath = currentProcessPath,
                            SwitchOutputPerApp = true,
                            OutputDeviceId = "out-2",
                            OutputDeviceName = "Headset"
                        }
                    ]
                }
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                SwitchApplicationOutputDeviceDetailedAsync: static (_, _, _, _) => new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.DeferredNoAudio, null)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineRun,
            Key = "Desk",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Output failure: Per-app output routing is pending until the application produces audio.", result.Output);
    }


    [Fact]
    public void FormatRoutineError_Json_IncludesPartialSuccessMetadata()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
        };

        string json = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: true,
            routine: routine,
            outputSucceeded: true,
            appliedOutputDeviceName: "Speakers",
            inputSucceeded: false,
            appliedInputDeviceName: null);

        JsonObject parsed = JsonNode.Parse(json)!.AsObject();
        JsonNode error = Assert.IsType<JsonObject>(parsed["data"]?["error"]);
        Assert.True(error["partialFailure"]?.GetValue<bool>());
        Assert.True(error["outputSucceeded"]?.GetValue<bool>());
        Assert.Equal("Speakers", error["appliedOutputDeviceName"]?.GetValue<string>());
        Assert.False(error["inputSucceeded"]?.GetValue<bool>());
        Assert.Null(error["appliedInputDeviceName"]?.GetValue<string>());
    }


    [Fact]
    public void FormatRoutineError_Text_IncludesPartialSuccessMetadata()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
        };

        string text = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: false,
            routine: routine,
            outputSucceeded: true,
            appliedOutputDeviceName: "Speakers",
            inputSucceeded: false,
            appliedInputDeviceName: null);

        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Partial result: succeeded output 'Speakers'; failed input 'Microphone'.", text);
    }


    [Fact]
    public void FormatRoutineError_Json_IncludesFailureDetails()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
        };

        string json = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: true,
            routine: routine,
            outputSucceeded: false,
            appliedOutputDeviceName: null,
            outputFailureDetail: "Per-app output routing is pending until the application produces audio.",
            inputSucceeded: false,
            appliedInputDeviceName: null,
            inputFailureDetail: "Input switch threw InvalidOperationException.");

        JsonObject parsed = JsonNode.Parse(json)!.AsObject();
        JsonNode error = Assert.IsType<JsonObject>(parsed["data"]?["error"]);
        Assert.Equal("Per-app output routing is pending until the application produces audio.", error["outputFailureDetail"]?.GetValue<string>());
        Assert.Equal("Input switch threw InvalidOperationException.", error["inputFailureDetail"]?.GetValue<string>());
    }


    [Fact]
    public void FormatRoutineError_Text_IncludesFailureDetails()
    {
        string text = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: false,
            outputFailureDetail: "Per-app output routing is pending until the application produces audio.",
            inputFailureDetail: "Input switch threw InvalidOperationException.");

        Assert.Equal("[diag-code:routine-run-failed] Failed to run routine 'Desk'. Output failure: Per-app output routing is pending until the application produces audio. Input failure: Input switch threw InvalidOperationException.", text);
    }


    [Fact]
    public void FormatRoutineError_Text_Redact_ReplacesSensitiveValues()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            InputDeviceId = "in-1",
            InputDeviceName = "Microphone",
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
        };

        string text = CliOutputFormatter.FormatRoutineError(
            3,
            "routine-run-failed",
            "Failed to run routine 'Desk'.",
            jsonOutput: false,
            routine: routine,
            targetApplicationName: "Discord",
            outputSucceeded: true,
            appliedOutputDeviceName: "Speakers",
            inputSucceeded: false,
            appliedInputDeviceName: null,
            redactOutput: true);

        Assert.DoesNotContain("Desk", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Speakers", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Discord", text, StringComparison.Ordinal);
        Assert.Contains("value[", text);
        Assert.Contains("device[", text);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineListJson_IncludesTriggerMetadata()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-discord",
                    Name = "Discord",
                    Enabled = true,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Headset",
                    UsesApplicationTrigger = true,
                    TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                    SwitchOutputPerApp = true,
                    ShowInTrayMenu = true,
                },
                new AudioRoutine
                {
                    Id = "routine-discord-alt",
                    Name = "Discord Alt",
                    Enabled = true,
                    OutputDeviceId = "out-2",
                    OutputDeviceName = "Speakers",
                    UsesApplicationTrigger = true,
                    SwitchOutputPerApp = true,
                    TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineList,
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        JsonArray routines = Assert.IsType<JsonArray>(parsed["data"]?["routines"]);
        JsonNode routine = Assert.IsType<JsonObject>(routines.Single(static item => string.Equals(item?["id"]?.GetValue<string>(), "routine-discord", StringComparison.Ordinal)));
        Assert.Equal("routine-discord", routine["id"]?.GetValue<string>());
        Assert.Equal("Application launch", routine["triggerMode"]?.GetValue<string>());
        Assert.True(routine["usesApplicationTrigger"]?.GetValue<bool>());
        Assert.Equal(@"C:\Apps\Discord\Discord.exe", routine["triggerAppPath"]?.GetValue<string>());
        Assert.True(routine["switchOutputPerApp"]?.GetValue<bool>());
        Assert.True(routine["showInTrayMenu"]?.GetValue<bool>());
        Assert.Equal("Application launch: Discord | Application audio only | Tray menu", routine["triggerSummary"]?.GetValue<string>());
        AssertRoutineTimingFieldsOmitted((JsonObject)routine);
        Assert.Contains("conflicts with 1 other enabled routine", routine["conflictSummary"]?.GetValue<string>(), StringComparison.Ordinal);
    }


    [Fact]
    public void FormatRoutineRunResult_Json_IncludesTriggerMetadata()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-desk",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            TriggerKind = RoutineTriggerKind.DeviceChange,
            ShowInTrayMenu = true,
        };

        string json = CliOutputFormatter.FormatRoutineRunResult(routine, "Speakers", null, jsonOutput: true);

        JsonObject parsed = JsonNode.Parse(json)!.AsObject();
        JsonNode data = Assert.IsType<JsonObject>(parsed["data"]);
        Assert.Equal("routine-desk", data["id"]?.GetValue<string>());
        Assert.Equal("Device change", data["triggerMode"]?.GetValue<string>());
        Assert.False(data["usesApplicationTrigger"]?.GetValue<bool>());
        Assert.Equal(string.Empty, data["triggerAppPath"]?.GetValue<string>());
        Assert.False(data["restorePreviousAudioOnDeactivate"]?.GetValue<bool>());
        Assert.False(data["switchOutputPerApp"]?.GetValue<bool>());
        Assert.True(data["showInTrayMenu"]?.GetValue<bool>());
        Assert.Equal("Device change | Tray menu", data["triggerSummary"]?.GetValue<string>());
        AssertRoutineTimingFieldsOmitted((JsonObject)data);
        Assert.Equal("Speakers", data["appliedOutputDeviceName"]?.GetValue<string>());
        Assert.Equal("routine-run-success", data["diagCode"]?.GetValue<string>());
    }

    private static void AssertRoutineTimingFieldsOmitted(JsonObject data)
    {
        Assert.False(data.ContainsKey("executionDelayMs"));
        Assert.False(data.ContainsKey("cooldownSeconds"));
        Assert.False(data.ContainsKey("triggerAppStableForMs"));
        Assert.False(data.ContainsKey("timingPreset"));
        Assert.False(data.ContainsKey("timingSummary"));
    }


    [Fact]
    public void FormatRoutineList_Text_IncludesConflictSummary()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                TriggerKind = RoutineTriggerKind.DeviceChange,
                ShowInTrayMenu = true,
            },
            new()
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
                TriggerKind = RoutineTriggerKind.DeviceChange,
                ShowInTrayMenu = true,
            }
        ];

        string text = CliOutputFormatter.FormatRoutineList(routines, jsonOutput: false);

        Assert.DoesNotContain("Timing:", text, StringComparison.Ordinal);
        Assert.Contains("Conflict: Device change conflicts with 1 other enabled routine: different output targets.", text, StringComparison.Ordinal);
    }


    [Fact]
    public void FormatRoutineRunResult_Text_OmitsTimingSummary()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Desk",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };

        string text = CliOutputFormatter.FormatRoutineRunResult(routine, "Speakers", null, jsonOutput: false);

        Assert.DoesNotContain("Timing:", text, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ExecuteAsync_RoutineListJson_Redact_RedactsNamesAndPaths()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
            [
                new AudioRoutine
                {
                    Id = "routine-discord",
                    Name = "Discord",
                    Enabled = true,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Headset",
                    UsesApplicationTrigger = true,
                    TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                    SwitchOutputPerApp = true,
                    ShowInTrayMenu = true,
                }
            ]
            }
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RoutineList,
            JsonOutput = true,
            RedactOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        JsonNode routine = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(parsed["data"]?["routines"])));
        Assert.Equal("routine-discord", routine["id"]?.GetValue<string>());
        Assert.StartsWith("routine[", routine["name"]?.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("path[", routine["triggerAppPath"]?.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device[", routine["outputDeviceName"]?.GetValue<string>(), StringComparison.Ordinal);
    }

}
