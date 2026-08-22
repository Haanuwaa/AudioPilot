using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{


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

        JObject parsed = JObject.Parse(result.Output!);
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.Value<string>());
        Assert.Equal("mute-mic-set-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(3, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
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

        JObject parsed = JObject.Parse(result.Output!);
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.Value<string>());
        Assert.Equal("mic", parsed["data"]?["target"]?.Value<string>());
        Assert.True(parsed["data"]?["enabled"]?.Value<bool>());
        Assert.Equal("mute-mic-status", parsed["data"]?["diagCode"]?.Value<string>());
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
        Assert.Equal("Failed to toggle playback mute.", result.Output);
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

        JObject parsed = JObject.Parse(result.Output!);
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.Value<string>());
        Assert.Equal("listen-set-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(3, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
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
        Assert.Equal("Failed to toggle input listen state.", result.Output);
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
        JObject parsed = JObject.Parse(result.Output!);
        Assert.True(parsed["data"]?["success"]?.Value<bool>());
        Assert.Equal("master", parsed["data"]?["kind"]?.Value<string>());
        Assert.Equal(72, parsed["data"]?["percent"]?.Value<int>());
        Assert.False(parsed["data"]?["muted"]?.Value<bool>());
        Assert.Equal("volume-get-success", parsed["data"]?["diagCode"]?.Value<string>());
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


    [Fact]
    public async Task ExecuteAsync_VolumeGetMasterWithDeviceId_ReturnsTargetedVolumeSnapshot()
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
        });

        Assert.Equal(0, result.ExitCode);
        JObject parsed = JObject.Parse(result.Output!);
        Assert.Equal("out-2", parsed["data"]?["deviceId"]?.Value<string>());
        Assert.Equal(40, parsed["data"]?["percent"]?.Value<int>());
        Assert.True(parsed["data"]?["muted"]?.Value<bool>());
    }


    [Fact]
    public async Task ExecuteAsync_VolumeSetMicWithDeviceId_ReturnsTargetedText()
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
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[diag-code:volume-set-success] Microphone volume 22% (unmuted) for device 'in-5'.", result.Output);
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
        JObject parsed = JObject.Parse(result.Output!);
        Assert.Equal("out-2", parsed["data"]?["deviceId"]?.Value<string>());
        Assert.Equal(40, parsed["data"]?["percent"]?.Value<int>());
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
        JObject history = JObject.Parse(historyJson);
        JToken entry = Assert.Single(Assert.IsType<JArray>(history["data"]?["entries"]));

        Assert.Equal("mute", entry["kind"]?.Value<string>());
        Assert.Equal("mute-mic-on", entry["action"]?.Value<string>());
        Assert.True(entry["success"]?.Value<bool>());
        Assert.Equal("mic", entry["target"]?.Value<string>());

        string opId = entry["opId"]?.Value<string>() ?? throw new InvalidOperationException("Missing opId in history entry.");
        var (found, detailJson) = scope.Runner.GetDiagnosticsHistoryDetail(opId, jsonOutput: true, redactOutput: false);

        Assert.True(found);
        JObject detail = JObject.Parse(detailJson);
        Assert.Equal(opId, detail["data"]?["opId"]?.Value<string>());
        Assert.Equal("mute-mic-on", detail["data"]?["action"]?.Value<string>());
        Assert.True(detail["data"]?["enabled"]?.Value<bool>());
    }

}
