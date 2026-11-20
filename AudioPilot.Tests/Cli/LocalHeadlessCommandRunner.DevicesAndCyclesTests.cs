using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void CyclePreviewSkipsDisconnectedEntriesAndRecoversStableIdentity(bool output, bool reverse, bool endpointChanged)
    {
        var settings = new Settings();
        List<CycleDevice> configured =
        [
            new() { Id = "a", Name = "Current", StableId = "stable-a" },
            new() { Id = "b", Name = "Disconnected" },
            new() { Id = "c", Name = "Alternate" },
        ];
        settings.DeviceSwitching.Output.CycleDevices = configured;
        settings.DeviceSwitching.Input.CycleDevices = configured;
        string currentId = endpointChanged ? "new-a" : "a";
        List<CycleDevice> active = [new() { Id = currentId, Name = "Current", StableId = "stable-a" }, new() { Id = "c", Name = "Alternate" }];
        using var scope = new HeadlessRunnerScope(settings, audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetActiveOutputDeviceInfos: () => active, GetActiveInputDeviceInfos: () => active,
            GetCurrentOutputDeviceId: () => currentId, GetCurrentInputDeviceId: () => currentId, HasDefaultInputDevice: () => true,
            SwitchAudioDeviceAsync: (_, _, _, _, _, _) => throw new InvalidOperationException("Preview must not switch."),
            SwitchInputDeviceToAsync: (_, _, _) => throw new InvalidOperationException("Preview must not switch.")));
        var (CanSwitch, Output) = scope.Runner.PreviewSwitch(output, reverse, jsonOutput: true, redactOutput: false);
        Assert.True(CanSwitch);
        Assert.Equal("c", JsonNode.Parse(Output)!["data"]!["targetDeviceId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void CyclePreviewDoesNotLetNameRecoveryClaimAnExactConfiguredEndpoint(bool output, bool reverse)
    {
        var settings = new Settings();
        List<CycleDevice> configured =
        [
            new() { Id = "disconnected", Name = "Shared name" },
            new() { Id = "b", Name = "Shared name" },
            new() { Id = "c", Name = "Alternate" },
        ];
        settings.DeviceSwitching.Output.CycleDevices = configured;
        settings.DeviceSwitching.Input.CycleDevices = configured;
        List<CycleDevice> active = [new() { Id = "b", Name = "Shared name" }, new() { Id = "c", Name = "Alternate" }];
        using var scope = new HeadlessRunnerScope(settings, audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetActiveOutputDeviceInfos: () => active, GetActiveInputDeviceInfos: () => active,
            GetCurrentOutputDeviceId: () => "b", GetCurrentInputDeviceId: () => "b", HasDefaultInputDevice: () => true));
        var (CanSwitch, Output) = scope.Runner.PreviewSwitch(output, reverse, jsonOutput: true, redactOutput: false);
        Assert.True(CanSwitch);
        Assert.Equal("c", JsonNode.Parse(Output)!["data"]!["targetDeviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_DevicesGetOutputJson_ReturnsMatchingDevice()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "out-3", Name = "Headset" },
                ]));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.DevicesGetOutput,
            Key = "Headset",
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("device-get-success", parsed["data"]?["diagCode"]?.GetValue<string>());
        Assert.Equal("out-3", parsed["data"]?["device"]?["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_DevicesFindInput_NoMatchesReturnsPreconditionFailure()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveInputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "in-1", Name = "Desk Mic" },
                ]));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.DevicesFindInput,
            Key = "usb",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:device-find-none] No input devices matched 'usb'.", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SwitchOutputDryRunJson_UsesOverrideCurrentAndActiveDevices(bool redactOutput)
    {
        using var scope = new HeadlessRunnerScope(
            new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Speakers" },
                            new CycleDevice { Id = "out-2", Name = "Headset" },
                        ]
                    },
                    Input = new DeviceSwitchingInputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "in-1", Name = "Mic" },
                        ]
                    }
                },
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "out-1", Name = "Speakers" },
                    new CycleDevice { Id = "out-2", Name = "Headset" },
                ],
                GetActiveInputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "in-1", Name = "Mic" },
                ],
                GetCurrentOutputDeviceId: static () => "out-1",
                HasDefaultInputDevice: static () => true));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.SwitchOutput,
            DryRun = true,
            JsonOutput = true,
            RedactOutput = redactOutput,
        });

        Assert.Equal(0, result.ExitCode);
        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.GetValue<string>());
        Assert.Equal("output", parsed["data"]?["kind"]?.GetValue<string>());
        Assert.True(parsed["data"]?["dryRun"]?.GetValue<bool>());
        Assert.Equal(CliOutputFormatter.FormatDeviceId("out-1", redactOutput), parsed["data"]?["currentDeviceId"]?.GetValue<string>());
        Assert.Equal(CliOutputFormatter.FormatDeviceId("out-2", redactOutput), parsed["data"]?["targetDeviceId"]?.GetValue<string>());
        if (redactOutput)
        {
            Assert.DoesNotContain("out-1", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("out-2", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Headset", result.Output, StringComparison.Ordinal);
        }
        Assert.Equal("switch-dry-run", parsed["data"]?["diagCode"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_SwitchInput_ReturnsRequireCurrentMismatch_WhenCurrentLookupFails()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetCurrentInputDeviceId: static () => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.SwitchInput,
            Key = "in-required",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("[diag-code:require-current-mismatch] Current input device does not match --require-current value.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CycleAddOutput_PersistsUpdatedCycle()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "out-2", Name = "Headset" },
                ]));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.CycleAddOutput,
            Key = "out-2",
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);

        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.True(parsed["data"]?["success"]?.GetValue<bool>());
        Assert.Equal("cycle-add-success", parsed["data"]?["diagCode"]?.GetValue<string>());

        string cycle = scope.Runner.GetCycle(output: true, jsonOutput: false, redactOutput: false);
        Assert.Contains("Headset", cycle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_CycleAddOutput_ByExactName_PersistsUpdatedCycle()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "out-2", Name = "Headset" },
                ]));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.CycleAddOutput,
            Key = "Headset",
        });

        Assert.Equal(0, result.ExitCode);
        string cycle = scope.Runner.GetCycle(output: true, jsonOutput: false, redactOutput: false);
        Assert.Contains("Headset", cycle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_CycleAddOutput_ReturnsFailure_WhenDeviceIsAlreadyConfigured()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-2", Name = "Headset" },
                        ]
                    }
                },
            },
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "out-2", Name = "Headset" },
                ]));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.CycleAddOutput,
            Key = "out-2",
            JsonOutput = true,
        });

        Assert.Equal(3, result.ExitCode);
        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.False(parsed["data"]?["success"]?.GetValue<bool>() ?? true);
        Assert.Equal("cycle-add-failed", parsed["data"]?["diagCode"]?.GetValue<string>());
        Assert.Equal("Device 'out-2' is already configured in the cycle.", parsed["data"]?["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_CycleRemoveInput_ReturnsFailure_WhenDeviceIsNotConfigured()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "in-1", Name = "Desk Mic" },
                    ]
                }
            },
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.CycleRemoveInput,
            Key = "in-2",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:cycle-remove-failed] Device 'in-2' is not configured in the cycle.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CycleReorderOutput_ReturnsFailure_WhenIdsDoNotMatchConfiguredCycle()
    {
        using var scope = new HeadlessRunnerScope(new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "out-1", Name = "Speakers" },
                        new CycleDevice { Id = "out-2", Name = "Headset" },
                    ]
                }
            },
        });

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.CycleReorderOutput,
            Value = "out-2\nout-3",
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:cycle-reorder-failed] Device 'out-3' is not configured in the cycle.", result.Output);
    }

    [Fact]
    public async Task WaitForDeviceAsync_ReturnsFound_WhenDeviceAppearsOnSecondPoll()
    {
        int polls = 0;
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: () =>
                {
                    polls++;
                    return polls >= 2
                        ?
                        [
                            new CycleDevice { Id = "out-2", Name = "Headset" },
                        ]
                        : [];
                },
                DelayAsync: static _ => Task.CompletedTask));

        (bool found, string output) = await scope.Runner.WaitForDeviceAsync(
            "out-2",
            timeoutMs: 1000,
            outputOnly: true,
            inputOnly: false,
            jsonOutput: true,
            redactOutput: false);

        Assert.True(found);
        JsonObject parsed = JsonNode.Parse(output)!.AsObject();
        Assert.True(parsed["data"]?["found"]?.GetValue<bool>());
        Assert.Equal("wait-device-found", parsed["data"]?["diagCode"]?.GetValue<string>());
        Assert.Equal("output", parsed["data"]?["scope"]?.GetValue<string>());
    }

    [Fact]
    public async Task WaitForDeviceAsync_ReturnsFound_WhenDeviceAppearsAfterDeviceStateEvent()
    {
        bool available = false;
        Action? onDeviceStateChanged = null;
        var subscriptionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: () =>
                    Volatile.Read(ref available)
                        ? [new CycleDevice { Id = "out-2", Name = "Headset" }]
                        : [],
                SubscribeDeviceStateChanged: handler =>
                {
                    onDeviceStateChanged = handler;
                    subscriptionReady.TrySetResult();
                    return new CallbackDisposable(() => onDeviceStateChanged = null);
                }));

        Task<(bool found, string output)> waitTask = scope.Runner.WaitForDeviceAsync(
            "out-2",
            timeoutMs: 10000,
            outputOnly: true,
            inputOnly: false,
            jsonOutput: true,
            redactOutput: false);

        await subscriptionReady.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Volatile.Write(ref available, true);
        Assert.NotNull(onDeviceStateChanged);
        onDeviceStateChanged();

        (bool found, string output) = await waitTask;

        Assert.True(found);
        JsonObject parsed = JsonNode.Parse(output)!.AsObject();
        Assert.True(parsed["data"]?["found"]?.GetValue<bool>());
        Assert.Equal("wait-device-found", parsed["data"]?["diagCode"]?.GetValue<string>());
    }

    [Fact]
    public async Task WaitForDeviceAsync_ReturnsTimeout_WhenDeviceNeverAppears()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () => [],
                GetActiveInputDeviceInfos: static () => [],
                DelayAsync: static _ => Task.CompletedTask));

        (bool found, string output) = await scope.Runner.WaitForDeviceAsync(
            "missing-device",
            timeoutMs: 0,
            outputOnly: false,
            inputOnly: false,
            jsonOutput: false,
            redactOutput: false);

        Assert.False(found);
        Assert.Contains("[diag-code:wait-device-timeout]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForDeviceAsync_DisposeCancelsOutstandingEventWait()
    {
        var subscriptionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () => [],
                SubscribeDeviceStateChanged: _ =>
                {
                    subscriptionReady.TrySetResult();
                    return new CallbackDisposable(static () => { });
                }));

        Task<(bool found, string output)> waitTask = scope.Runner.WaitForDeviceAsync(
            "missing-device",
            timeoutMs: 30000,
            outputOnly: true,
            inputOnly: false,
            jsonOutput: false,
            redactOutput: false);

        await subscriptionReady.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        scope.Runner.Dispose();
        (bool found, _) = await waitTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.False(found);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExecuteAsync_WaitForDevice_RedactsFoundAndTimeoutResponses(bool available, bool jsonOutput)
    {
        const string deviceId = "private-endpoint-identifier";
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: () => available ? [new CycleDevice { Id = deviceId, Name = "Private headset" }] : []));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.WaitForDevice,
            Key = deviceId,
            Value = "0",
            WaitOutputOnly = true,
            JsonOutput = jsonOutput,
            RedactOutput = true,
        });

        Assert.Equal(available ? 0 : 5, result.ExitCode);
        Assert.DoesNotContain(deviceId, result.Output, StringComparison.Ordinal);
        Assert.Contains("device-id[", result.Output, StringComparison.Ordinal);
        Assert.Contains(available ? "wait-device-found" : "wait-device-timeout", result.Output, StringComparison.Ordinal);
        if (jsonOutput)
        {
            JsonObject data = (JsonObject)JsonNode.Parse(result.Output!)!["data"]!;
            Assert.Equal(available, data["found"]!.GetValue<bool>());
            Assert.Equal("output", data["scope"]!.GetValue<string>());
            Assert.Equal(!available, data.ContainsKey("timeoutMs"));
        }
    }

}
