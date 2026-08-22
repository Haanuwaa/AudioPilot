using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{
    [Theory]
    [InlineData(CliAction.MuteSoundOn, "mute-sound-set-failed")]
    [InlineData(CliAction.MuteMicOn, "mute-mic-set-failed")]
    public async Task ExecuteAsync_MuteReportsAudioServiceFailure(CliAction action, string expectedCode)
    {
        using var audioService = new AudioDeviceService(new FakeInputListenPropertyWriter());
        audioService.Dispose();
        using var runner = new LocalHeadlessCommandRunner(new LocalHeadlessCommandRunner.RuntimeServiceFactories(
            CreateAudioService: () => audioService));

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand { Action = action, JsonOutput = true });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(expectedCode, JsonNode.Parse(result.Output!)!["data"]!["error"]!["code"]!.GetValue<string>());
        JsonObject history = JsonNode.Parse(runner.GetDiagnosticsHistory(jsonOutput: true, limit: 1, type: null, redactOutput: true))!.AsObject();
        Assert.False(Assert.Single(history["data"]!["entries"]!.AsArray())!["success"]!.GetValue<bool>());
    }


    [Fact]
    public async Task ExecuteAsync_MuteMicOnJson_ReturnsErrorEnvelope_WhenAudioWriteThrows()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                TrySetMicrophoneMute: static _ => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.MuteMicOn,
            JsonOutput = true,
        });

        Assert.Equal(3, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.GetValue<string>());
        Assert.Equal("mute-mic-set-failed", parsed["data"]?["error"]?["code"]?.GetValue<string>());
        Assert.Equal(3, parsed["data"]?["error"]?["exitCode"]?.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteAsync_MuteMicOnJson_ReturnsStatusEnvelope_WhenMuteSucceeds()
    {
        bool muted = false;
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                TrySetMicrophoneMute: enabled =>
                {
                    muted = enabled;
                    return true;
                },
                GetDefaultCaptureMute: () => muted));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.MuteMicOn,
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.GetValue<string>());
        Assert.Equal("mic", parsed["data"]?["target"]?.GetValue<string>());
        Assert.True(parsed["data"]?["enabled"]?.GetValue<bool>());
        Assert.Equal("mute-mic-status", parsed["data"]?["diagCode"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_MuteSoundToggle_ReturnsTextError_WhenMuteLookupThrows()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetDefaultPlaybackMute: static () => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.MuteSoundToggle,
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:mute-sound-toggle-failed] Failed to toggle playback mute.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ListenOnJson_ReturnsErrorEnvelope_WhenAudioWriteThrows()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                TrySetListenToInput: static _ => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.ListenOn,
            JsonOutput = true,
        });

        Assert.Equal(3, result.ExitCode);
        Assert.NotNull(result.Output);

        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.GetValue<string>());
        Assert.Equal("listen-set-failed", parsed["data"]?["error"]?["code"]?.GetValue<string>());
        Assert.Equal(3, parsed["data"]?["error"]?["exitCode"]?.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteAsync_ListenToggle_ReturnsTextError_WhenAudioToggleThrows()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                TryToggleListenToInput: static () => throw new InvalidOperationException("boom")));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.ListenToggle,
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:listen-toggle-failed] Failed to toggle input listen state.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeGetMasterJson_ReturnsVolumeSnapshot()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetDefaultPlaybackVolume: static () => (true, 72.2f, false)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeGetMaster,
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.True(parsed["data"]?["success"]?.GetValue<bool>());
        Assert.Equal("master", parsed["data"]?["kind"]?.GetValue<string>());
        Assert.Equal(72, parsed["data"]?["percent"]?.GetValue<int>());
        Assert.False(parsed["data"]?["muted"]?.GetValue<bool>());
        Assert.Equal("volume-get-success", parsed["data"]?["diagCode"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_VolumeSetMic_ReturnsAppliedVolume()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                TrySetCaptureVolume: static percent => (true, percent, percent <= 0f)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeSetMic,
            Value = "15",
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[diag-code:volume-set-success] Microphone volume 15% (unmuted).", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_VolumeGetMasterWithDeviceId_ReturnsTargetedVolumeSnapshot(bool redactOutput)
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetPlaybackVolumeByDeviceId: static deviceId => deviceId == "out-2" ? (true, 40f, true) : (false, 0f, false)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeGetMaster,
            Key = CliDeviceSelectorResolver.EncodeExactId("out-2"),
            JsonOutput = true,
            RedactOutput = redactOutput,
        });

        Assert.Equal(0, result.ExitCode);
        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal(CliOutputFormatter.FormatDeviceId("out-2", redactOutput), parsed["data"]?["deviceId"]?.GetValue<string>());
        Assert.Equal(40, parsed["data"]?["percent"]?.GetValue<int>());
        Assert.True(parsed["data"]?["muted"]?.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_VolumeSetMicWithDeviceId_ReturnsTargetedText(bool redactOutput)
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                TrySetCaptureVolumeByDeviceId: static (deviceId, percent) => deviceId == "in-5" ? (true, percent, false) : (false, 0f, false)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeSetMic,
            Key = CliDeviceSelectorResolver.EncodeExactId("in-5"),
            Value = "22",
            RedactOutput = redactOutput,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"[diag-code:volume-set-success] Microphone volume 22% (unmuted) for device '{CliOutputFormatter.FormatDeviceId("in-5", redactOutput)}'.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeGetMasterWithDeviceName_ResolvesExactActiveName()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "out-2", Name = "Speakers" },
                ],
                GetPlaybackVolumeByDeviceId: static deviceId => deviceId == "out-2" ? (true, 40f, false) : (false, 0f, false)));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeGetMaster,
            Key = CliDeviceSelectorResolver.EncodeExactName("Speakers"),
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        JsonObject parsed = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("out-2", parsed["data"]?["deviceId"]?.GetValue<string>());
        Assert.Equal(40, parsed["data"]?["percent"]?.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteAsync_VolumeGetMasterWithAmbiguousDeviceName_ReturnsFailure()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetActiveOutputDeviceInfos: static () =>
                [
                    new CycleDevice { Id = "out-1", Name = "Speakers" },
                    new CycleDevice { Id = "out-2", Name = "Speakers" },
                ]));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeGetMaster,
            Key = CliDeviceSelectorResolver.EncodeExactName("Speakers"),
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:volume-get-failed] output device selector 'Speakers' is ambiguous. Matching IDs: out-1, out-2.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeGetMic_ReturnsFailure_WhenDeviceIsUnavailable()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetDefaultCaptureVolume: static () => (false, 0f, false),
                HasDefaultInputDevice: static () => false));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeGetMic,
        });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("[diag-code:volume-get-failed] No default recording device is available.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MuteMicOn_RecordsDiagnosticsHistoryAndSupportsDetailLookup()
    {
        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                TrySetMicrophoneMute: static _ => true));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.MuteMicOn,
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);

        string historyJson = scope.Runner.GetDiagnosticsHistory(jsonOutput: true, limit: 10, type: "mute", redactOutput: false);
        JsonObject history = JsonNode.Parse(historyJson)!.AsObject();
        JsonNode entry = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(history["data"]?["entries"])));

        Assert.Equal("mute", entry["kind"]?.GetValue<string>());
        Assert.Equal("mute-mic-on", entry["action"]?.GetValue<string>());
        Assert.True(entry["success"]?.GetValue<bool>());
        Assert.Equal("mic", entry["target"]?.GetValue<string>());

        string opId = entry["opId"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing opId in history entry.");
        var (found, detailJson) = scope.Runner.GetDiagnosticsHistoryDetail(opId, jsonOutput: true, redactOutput: false);

        Assert.True(found);
        JsonObject detail = JsonNode.Parse(detailJson)!.AsObject();
        Assert.Equal(opId, detail["data"]?["opId"]?.GetValue<string>());
        Assert.Equal("mute-mic-on", detail["data"]?["action"]?.GetValue<string>());
        Assert.True(detail["data"]?["enabled"]?.GetValue<bool>());
    }

}
