using AudioPilot.Cli;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class RoutineCheckCommandTests
{
    [Theory]
    [InlineData("--json", true)]
    [InlineData("--redact", true)]
    [InlineData("--dry-run", false)]
    [InlineData("--allow-any-path", false)]
    public void ParserAndPipeEnvelopePreserveTheReadOnlyCommand(string flag, bool valid)
    {
        Assert.Equal(valid, CliCommand.TryParse(["routine", "check", "Example routine", flag], out var command, out _));
        if (!valid) return;
        Assert.Equal(CliAction.RoutineCheck, command.Action);
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out var decoded));
        Assert.Equal("Example routine", decoded!.Key);
        Assert.Equal(command.JsonOutput, decoded.JsonOutput);
        Assert.Equal(command.RedactOutput, decoded.RedactOutput);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task ExecutorPreservesCheckExitCodesAndFlags(int exitCode)
    {
        var runtime = new FakeRuntime { RunRoutineResult = new(exitCode, "check-result") };
        var result = await CliCommandExecutor.ExecuteAsync(new() { Action = CliAction.RoutineCheck, Key = "Example routine", JsonOutput = true, RedactOutput = true }, runtime);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("check-result", result.Output);
        Assert.Equal("Example routine", runtime.LastRoutineSelector);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task MissingSelectorIsRejectedBeforeRuntimeInspection()
    {
        var runtime = new FakeRuntime();
        var result = await CliCommandExecutor.ExecuteAsync(new() { Action = CliAction.RoutineCheck, JsonOutput = true }, runtime);
        Assert.Equal(2, result.ExitCode);
        Assert.Null(runtime.LastRoutineSelector);
    }
}
