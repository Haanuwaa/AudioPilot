using System.Globalization;
using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DirectSwitch_UsesActiveEndpointWithoutCycle(bool playback, bool dryRun)
    {
        int writes = 0;
        using var scope = new HeadlessRunnerScope(new Settings(), audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetActiveOutputDeviceInfos: () => [new CycleDevice { Id = "output", Name = "Speakers" }],
            GetActiveInputDeviceInfos: () => [new CycleDevice { Id = "input", Name = "Mic" }],
            GetCurrentOutputDeviceId: () => "old-output", GetCurrentInputDeviceId: () => "old-input",
            GetDefaultPlaybackMute: () => false, GetDefaultCaptureMute: () => false,
            SwitchAudioDeviceAsync: (target, mic, sound, deafen, preserve, opId) =>
            {
                Assert.True(playback);
                Assert.Equal("output", target);
                writes++;
                return (true, "Speakers");
            },
            SwitchInputDeviceToAsync: (target, name, opId) =>
            {
                Assert.False(playback);
                Assert.Equal("input", target);
                writes++;
                return (true, name);
            }));
        var result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = playback ? CliAction.SwitchOutputToDevice : CliAction.SwitchInputToDevice,
            Value = playback ? "[name]Speakers" : "[id]input",
            DryRun = dryRun,
            JsonOutput = true,
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(dryRun ? 0 : 1, writes);
        Assert.Equal(playback ? "output" : "input", JsonNode.Parse(result.Output!)!["data"]!["targetDeviceId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, 98f, 5f, 100f)]
    [InlineData(false, 2f, -10f, 0f)]
    [InlineData(true, 40.25f, 0.5f, 40.75f)]
    [InlineData(false, 45f, 0f, 45f)]
    public async Task VolumeAdjust_ClampsAndPinsEndpointAcrossDefaultChange(bool playback, float before, float delta, float expected)
    {
        string currentId = "original";
        int writes = 0;
        (bool, float, bool) Read(string id)
        {
            Assert.Equal("original", id);
            currentId = "replacement";
            return (true, before, true);
        }
        (bool, float, bool) Write(string id, float value)
        {
            Assert.Equal("original", id);
            Assert.Equal(expected, value);
            writes++;
            return (true, value, value == 0);
        }
        using var scope = new HeadlessRunnerScope(new Settings(), audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetCurrentOutputDeviceId: () => currentId, GetCurrentInputDeviceId: () => currentId,
            GetPlaybackVolumeByDeviceId: Read, GetCaptureVolumeByDeviceId: Read,
            TrySetPlaybackVolumeByDeviceId: Write, TrySetCaptureVolumeByDeviceId: Write));
        var result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = playback ? CliAction.VolumeAdjustMaster : CliAction.VolumeAdjustMic,
            Value = delta.ToString(CultureInfo.InvariantCulture),
            JsonOutput = true,
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(delta == 0 ? 0 : 1, writes);
        JsonNode data = JsonNode.Parse(result.Output!)!["data"]!;
        Assert.Equal(before, data["previousPercent"]!.GetValue<float>());
        Assert.Equal(expected, data["percent"]!.GetValue<float>());
        Assert.Equal(delta == 0 || expected == 0, data["muted"]!.GetValue<bool>());
        Assert.Equal("original", data["deviceId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, 50f, true, 0)]
    [InlineData(true, float.NaN, true, 0)]
    [InlineData(true, 50f, false, 1)]
    public async Task VolumeAdjust_ReadOrWriteFailure_DoesNotClaimSuccess(bool readSuccess, float before, bool writeSuccess, int expectedWrites)
    {
        int writes = 0;
        using var scope = new HeadlessRunnerScope(new Settings(), audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
            GetActiveOutputDeviceInfos: () => [new CycleDevice { Id = "a", Name = "Speakers" }],
            GetPlaybackVolumeByDeviceId: _ => (readSuccess, before, false),
            TrySetPlaybackVolumeByDeviceId: (_, value) => { writes++; return (writeSuccess, value, false); }));
        var result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.VolumeAdjustMaster,
            Key = "[name]Speakers",
            Value = "5",
            JsonOutput = true,
        });
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(expectedWrites, writes);
        Assert.Contains("volume-adjust-failed", result.Output!, StringComparison.Ordinal);
    }
}
