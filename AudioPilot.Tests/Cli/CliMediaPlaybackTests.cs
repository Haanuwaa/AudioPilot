using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliMediaPlaybackTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HeadlessRuntimePreservesResultWithoutCreatingAudioServices(bool playing, bool supported)
    {
        var session = new FakeMediaPlaybackSession { Status = FakeMediaPlaybackSession.Desired(!playing), CanPlay = supported, CanPause = supported };
        using var runner = new LocalHeadlessCommandRunner(
            new LocalHeadlessCommandRunner.RuntimeServiceFactories(CreateAudioService: () => throw new InvalidOperationException("Must not initialize devices")),
            mediaPlaybackService: session.CreateService());
        var command = new CliCommand { Action = playing ? CliAction.MediaPlay : CliAction.MediaPause, JsonOutput = true };

        CliExecutionResult result = await runner.ExecuteAsync(command);

        Assert.Equal(supported ? 0 : 1, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Output!);
        Assert.Equal(supported, json.RootElement.GetProperty("data").GetProperty("confirmed").GetBoolean());
        if (supported)
        {
            CliExecutionResult repeated = await runner.ExecuteAsync(command);
            using JsonDocument repeatJson = JsonDocument.Parse(repeated.Output!);
            Assert.False(repeatJson.RootElement.GetProperty("data").GetProperty("requestSent").GetBoolean());
            Assert.Equal(1, session.RequestCount);
        }
        else
        {
            Assert.Equal(0, session.RequestCount);
        }
    }

    [Theory]
    [InlineData("play", true, true)]
    [InlineData("pause", false, true)]
    [InlineData("play", true, false)]
    [InlineData("pause", false, false)]
    public async Task ParsesExplicitAction_AndAwaitsResult(string action, bool playing, bool success)
    {
        Assert.True(CliCommand.TryParse(["media", action, "--json"], out CliCommand command, out string? error), error);
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out CliCommand forwarded));
        Assert.Equal(command.Action, forwarded.Action);
        Assert.True(forwarded.JsonOutput);
        var completion = new TaskCompletionSource<MediaPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime { MediaPlaybackOverride = requested => { Assert.Equal(playing, requested); return completion.Task; } };

        Task<CliExecutionResult> pending = CliCommandExecutor.ExecuteAsync(forwarded, runtime);
        Assert.False(pending.IsCompleted);
        completion.SetResult(new(success, success ? "media-playback-accepted" : "media-playback-unsupported", "Result", playing ? "Playing" : "Paused", RequestSent: success));
        CliExecutionResult result = await pending;

        Assert.Equal(success ? 0 : 1, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Output!);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(success, data.GetProperty("success").GetBoolean());
        Assert.Equal(playing ? "Playing" : "Paused", data.GetProperty("requestedState").GetString());
        Assert.False(data.GetProperty("confirmed").GetBoolean());
        Assert.Equal(success, data.GetProperty("requestSent").GetBoolean());
    }

    [Theory]
    [InlineData("play", "--redact")]
    [InlineData("pause", "--redact")]
    [InlineData("play", "true")]
    [InlineData("pause", "--unknown")]
    public void RejectsUnsupportedArguments(string action, string argument)
    {
        Assert.False(CliCommand.TryParse(["media", action, argument], out _, out _));
    }

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    public async Task TextOutputReportsResult(string action)
    {
        Assert.True(CliCommand.TryParse(["media", action], out CliCommand command, out _));
        var runtime = new FakeRuntime { MediaPlaybackOverride = _ => Task.FromResult(new MediaPlaybackResult(false, "media-playback-no-session", "No media session detected.", "Paused")) };
        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(command, runtime);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("No media session detected.", result.Output);
    }
}
