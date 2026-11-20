using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliMediaSeekTests
{
    [Theory]
    [InlineData("forward", null, false)]
    [InlineData("backward", "30", true)]
    public async Task ParsesAndAwaitsSeekResult(string direction, string? seconds, bool backward)
    {
        string[] args = seconds == null ? ["media", "seek", direction, "--json"] : ["media", "seek", direction, seconds, "--json"];
        Assert.True(CliCommand.TryParse(args, out CliCommand command, out string? error), error);
        var completion = new TaskCompletionSource<MediaSeekResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime
        {
            MediaSeekOverride = (reverse, step) =>
            {
                Assert.Equal(backward, reverse);
                Assert.Equal(seconds == null ? null : (int?)30, step);
                return completion.Task;
            }
        };

        Task<CliExecutionResult> pending = CliCommandExecutor.ExecuteAsync(command, runtime);
        Assert.False(pending.IsCompleted);
        completion.SetResult(new(false, "media-seek-unavailable", "Seeking unavailable"));
        CliExecutionResult result = await pending;

        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(result.Output);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("data").GetProperty("success").GetBoolean());
        Assert.Equal("media-seek-unavailable", json.RootElement.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public void SeekJsonIncludesBothPositionsWithoutInternalTrackMetadata()
    {
        var result = new MediaSeekResult(true, "media-seek-accepted", "Seek forward: 1:24 → 1:34", 94, 84)
        {
            Track = new("Private track title", "Private artist", "Private album", 1),
        };

        string output = CliOutputFormatter.FormatMediaSeek(result, jsonOutput: true);

        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(84, data.GetProperty("previousPositionSeconds").GetDouble());
        Assert.Equal(94, data.GetProperty("targetPositionSeconds").GetDouble());
        Assert.DoesNotContain("Private", output);
        Assert.False(data.TryGetProperty("positionChangeText", out _));
        Assert.False(data.TryGetProperty("track", out _));
    }

    [Fact]
    public async Task MissingPlayerTimelineHasDistinctJsonFailure()
    {
        var runtime = new FakeRuntime
        {
            MediaSeekOverride = (_, _) => Task.FromResult(new MediaSeekResult(false,
                "media-seek-timeline-unavailable", "Player timeline unavailable"))
        };
        Assert.True(CliCommand.TryParse(["media", "seek", "forward", "--json"], out CliCommand command, out _));

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(command, runtime);

        Assert.Equal(1, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Output!);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal("media-seek-timeline-unavailable", data.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("targetPositionSeconds").ValueKind);
    }

    [Theory]
    [InlineData("1.5m")]
    [InlineData("1m 30s")]
    [InlineData("1:30")]
    public void DurationInputIsCanonicalizedForIpcAndConfiguration(string input)
    {
        Assert.True(CliCommand.TryParse(["media", "seek", "forward", input], out CliCommand command, out string? error), error);
        Assert.Equal("90", command.Value);
        var settings = new Settings();
        Assert.True(CliConfigManager.TrySet(settings, "media-seek-step-seconds", input, out error), error);
        Assert.Equal(90, settings.Hotkeys.Media.SeekStepSeconds);
        Assert.True(CliConfigManager.TryGet(settings, "media-seek-step-seconds", out string value, out error), error);
        Assert.Equal("90", value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("3601")]
    [InlineData("NaN")]
    [InlineData("99999999999999999")]
    public void RejectsInvalidSeconds(string value)
    {
        Assert.False(CliCommand.TryParse(["media", "seek", "forward", value], out _, out _));
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("--json")]
    public void RequiresExplicitDirection(string direction)
    {
        Assert.False(CliCommand.TryParse(["media", "seek", direction], out _, out _));
    }

    [Fact]
    public async Task ExecutorValidatesUntrustedPipeValuesBeforeCallingRuntime()
    {
        var runtime = new FakeRuntime { MediaSeekOverride = (_, _) => throw new InvalidOperationException("Must not send") };
        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(new CliCommand { Action = CliAction.MediaSeek, Value = "-10" }, runtime);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void ConfigurationRoundTripsAndRejectsInvalidStep()
    {
        var settings = new Settings();
        Assert.Empty(settings.Hotkeys.Media.SeekForward);
        Assert.Empty(settings.Hotkeys.Media.SeekBackward);
        Assert.Equal(10, settings.Hotkeys.Media.SeekStepSeconds);
        Assert.True(CliConfigManager.TrySet(settings, "seek-forward-hotkey", "Ctrl+Shift+Right", out _));
        Assert.True(CliConfigManager.TrySet(settings, "seek-backward-hotkey", "Ctrl+Shift+Left", out _));
        Assert.True(CliConfigManager.TrySet(settings, "media-seek-step-seconds", "30", out _));
        Assert.False(CliConfigManager.TrySet(settings, "media-seek-step-seconds", "0", out _));

        Settings imported = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings.Clone(), SettingsJson.Options), SettingsJson.ImportOptions)!;
        Assert.Equal("Ctrl+Shift+Right", imported.Hotkeys.Media.SeekForward);
        Assert.Equal("Ctrl+Shift+Left", imported.Hotkeys.Media.SeekBackward);
        Assert.Equal(30, imported.Hotkeys.Media.SeekStepSeconds);
    }
}
