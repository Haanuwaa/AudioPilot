using AudioPilot.Cli;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliVolumeAdjustmentTests
{
    [Theory]
    [InlineData("master", "+5", CliAction.VolumeAdjustMaster)]
    [InlineData("mic", "-10", CliAction.VolumeAdjustMic)]
    [InlineData("master", "+0.5", CliAction.VolumeAdjustMaster)]
    [InlineData("mic", "0", CliAction.VolumeAdjustMic)]
    public async Task Adjust_ParsesAndDispatchesSignedDelta(string target, string delta, CliAction expected)
    {
        Assert.True(CliCommand.TryParse(["volume", "adjust", target, delta, "--device-id", "endpoint", "--json", "--redact"], out var command, out var error), error);
        Assert.Equal(expected, command.Action);
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out var restored));
        Assert.Equal(expected, restored.Action);
        Assert.Equal(delta, restored.Value);
        var runtime = new FakeRuntime();
        var result = await CliCommandExecutor.ExecuteAsync(restored, runtime);
        Assert.Equal(0, result.ExitCode);
        Assert.True(runtime.LastVolumeRelative);
        Assert.True(runtime.LastRedactOutput);
        Assert.Equal("[id]endpoint", runtime.LastVolumeDeviceId);
        Assert.Equal(float.Parse(delta, System.Globalization.CultureInfo.InvariantCulture), runtime.LastVolumePercent);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("101")]
    [InlineData("-101")]
    [InlineData("1,5")]
    public async Task Adjust_RejectsInvalidDeltasAtParserAndExecutionBoundary(string delta)
    {
        Assert.False(CliCommand.TryParse(["volume", "adjust", "master", delta], out _, out _));
        var runtime = new FakeRuntime();
        var result = await CliCommandExecutor.ExecuteAsync(new CliCommand { Action = CliAction.VolumeAdjustMaster, Value = delta }, runtime);
        Assert.Equal(2, result.ExitCode);
        Assert.Null(runtime.LastVolumePercent);
    }

    [Theory]
    [InlineData("--device", "--json")]
    [InlineData("--device-id")]
    [InlineData("--device", "Speakers", "--device-id", "a")]
    public void Adjust_RejectsMissingOrConflictingDeviceSelectors(params string[] flags)
    {
        Assert.False(CliCommand.TryParse(["volume", "adjust", "master", "5", .. flags], out _, out _));
    }

    [Fact]
    public async Task DirectSwitch_DispatchesWithoutCycling()
    {
        Assert.True(CliCommand.TryParse(["switch", "output", "--device", "Speakers"], out var command, out _));
        var runtime = new FakeRuntime { SwitchOutputResult = false };
        var result = await CliCommandExecutor.ExecuteAsync(command, runtime);
        Assert.Equal(0, result.ExitCode);
        Assert.Same(command, runtime.LastDirectSwitchCommand);
    }
}
