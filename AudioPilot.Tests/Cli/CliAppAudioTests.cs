using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliAppAudioTests
{
    [Theory]
    [InlineData("app list --json", CliAction.AppList, null)]
    [InlineData("app volume get --process Steam.EXE", CliAction.AppVolumeGet, "name:Steam")]
    [InlineData("app volume set 37.5 --pid 123 --redact", CliAction.AppVolumeSet, "pid:123")]
    [InlineData("app volume adjust -5 --pid 123", CliAction.AppVolumeAdjust, "pid:123")]
    [InlineData("app mute on --process steam", CliAction.AppMuteOn, "name:steam")]
    [InlineData("app mute off --pid 123", CliAction.AppMuteOff, "pid:123")]
    [InlineData("app mute toggle --pid 123", CliAction.AppMuteToggle, "pid:123")]
    public async Task CommandsSurvivePipeForwardingAndDispatch(string input, CliAction action, string? selector)
    {
        Assert.True(CliCommand.TryParse(input.Split(' '), out var command, out string? error), error);
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out var forwarded));
        Assert.Equal(action, forwarded.Action);
        Assert.Equal(selector, forwarded.Key);
        Assert.Equal(command.Value, forwarded.Value);
        Assert.Equal(command.JsonOutput, forwarded.JsonOutput);
        Assert.Equal(command.RedactOutput, forwarded.RedactOutput);
        var runtime = new FakeRuntime
        {
            AppAudioOverride = request =>
        {
            Assert.Same(forwarded, request);
            return Task.FromResult(new CliExecutionResult(5, "test-result"));
        }
        };
        Assert.Equal(new CliExecutionResult(5, "test-result"), await CliCommandExecutor.ExecuteAsync(forwarded, runtime));
    }

    [Theory]
    [InlineData("app volume get")]
    [InlineData("app volume get --pid 0")]
    [InlineData("app volume get --pid 4294967295")]
    [InlineData("app volume get --pid 1 --process game")]
    [InlineData("app volume get --process game*")]
    [InlineData("app volume get --process C:\\game.exe")]
    [InlineData("app volume get --process --json")]
    [InlineData("app volume set NaN --pid 1")]
    [InlineData("app volume set -1 --pid 1")]
    [InlineData("app volume adjust 101 --pid 1")]
    [InlineData("app mute --pid 1")]
    [InlineData("app list --pid 1")]
    [InlineData("app list --json --json")]
    public void InvalidArgumentsAreRejected(string input) => Assert.False(CliCommand.TryParse(input.Split(' '), out _, out _));

    [Fact]
    public void ReadFailuresRetainOtherSessionsWithoutClaimingCompleteResults()
    {
        var readable = new Session(11, "game");
        var expired = new Session(12, "other") { IsCurrent = false };
        var failed = new Session(13, "player") { ReadFails = true };
        var result = Execute("app list --json", readable, expired, failed);
        Assert.Equal(3, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output!);
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.Equal("app-audio-partial", data.GetProperty("diagCode").GetString());
        Assert.Equal(2, data.GetProperty("failedSessions").GetInt32());
        Assert.Equal(11, data.GetProperty("apps")[0].GetProperty("processId").GetInt32());
        Assert.Equal(0, readable.Writes + expired.Writes + failed.Writes);
        Assert.True(readable.Disposed && expired.Disposed && failed.Disposed);
    }

    [Theory]
    [InlineData("app volume adjust 5 --pid 11", .85f, false)]
    [InlineData("app volume set 50 --pid 11", .5f, false)]
    [InlineData("app mute on --pid 11", .8f, true)]
    public void WritesUseFreshStateAfterPreflight(string input, float expectedVolume, bool expectedMute)
    {
        var session = new Session(11, "game", .5f, true)
        {
            BeforeRead = (current, count) =>
            {
                if (count == 2) { current.Volume = .8f; current.Muted = false; }
            }
        };
        Assert.Equal(0, Execute(input, session).ExitCode);
        Assert.Equal(expectedVolume, session.Volume, precision: 5);
        Assert.Equal(expectedMute, session.Muted);
        Assert.Equal(1, session.Writes);
    }

    [Fact]
    public void SelectionRejectsAmbiguityAndMissingSessionsWithoutWriting()
    {
        var first = new Session(11, "game");
        var second = new Session(12, "game");
        var result = Execute("app mute on --process game --json", first, second);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("app-audio-ambiguous-process", result.Output);
        Assert.Equal(0, first.Writes + second.Writes);
        Assert.True(first.Disposed && second.Disposed);
        Assert.Equal(5, Execute("app volume get --pid 13", new Session(11, "game")).ExitCode);
    }

    [Fact]
    public void AdjustClampsEachSessionAndPreservesMuteAndOtherProcesses()
    {
        var first = new Session(11, "game", .98f, true);
        var second = new Session(11, "game", .20f);
        var other = new Session(12, "game", .6f);
        var result = Execute("app volume adjust 5 --pid 11 --json", first, second, other);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1f, first.Volume);
        Assert.Equal(.25f, second.Volume);
        Assert.True(first.Muted);
        Assert.False(second.Muted);
        Assert.Equal(0, other.Writes);
        Assert.True(first.Disposed && second.Disposed && other.Disposed);
    }

    [Theory]
    [InlineData("app volume set 0 --pid 11", 0f)]
    [InlineData("app volume adjust -100 --pid 11", 0f)]
    [InlineData("app volume adjust 0 --pid 11", .5f)]
    public void VolumeNeverChangesMute(string input, float expected)
    {
        var session = new Session(11, "game", .5f, true);
        Assert.Equal(0, Execute(input, session).ExitCode);
        Assert.Equal(expected, session.Volume);
        Assert.True(session.Muted);
        Assert.Equal(expected == .5f ? 0 : 1, session.Writes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ToggleTreatsTheSelectedSessionsAsAGroup(bool allMuted, bool expected)
    {
        var first = new Session(11, "game", .4f, true);
        var second = new Session(11, "game", .8f, allMuted);
        Assert.Equal(0, Execute("app mute toggle --pid 11", first, second).ExitCode);
        Assert.Equal(expected, first.Muted);
        Assert.Equal(expected, second.Muted);
        Assert.Equal(.4f, first.Volume);
        Assert.Equal(.8f, second.Volume);
    }

    [Fact]
    public void ReadAndListAggregateWithoutWritingAndRedactIdentities()
    {
        var first = new Session(9876, "private-game", .3f, true);
        var second = new Session(9876, "private-game", .7f, false);
        var result = Execute("app list --json --redact", first, second);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("9876", result.Output);
        Assert.DoesNotContain("private-game", result.Output);
        using var json = JsonDocument.Parse(result.Output!);
        var apps = json.RootElement.GetProperty("data").GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal(70f, apps[0].GetProperty("percent").GetSingle());
        Assert.False(apps[0].GetProperty("muted").GetBoolean());
        Assert.Equal(2, apps[0].GetProperty("sessionCount").GetInt32());
        Assert.Equal(0, first.Writes + second.Writes);
    }

    [Fact]
    public void FailedPreflightAndReusedProcessPreventWritesAndReleaseSessions()
    {
        var first = new Session(11, "game");
        var second = new Session(11, "game") { ReadFails = true };
        Assert.Equal(3, Execute("app volume set 10 --pid 11", first, second).ExitCode);
        Assert.Equal(0, first.Writes + second.Writes);
        Assert.True(first.Disposed && second.Disposed);
        var stale = new Session(11, "game") { IsCurrent = false };
        Assert.Equal(5, Execute("app mute on --pid 11", stale).ExitCode);
        Assert.Equal(0, stale.Writes);
        Assert.True(stale.Disposed);
    }

    [Fact]
    public void PartialWriteFailureIsReportedWithoutRollingBackOtherSessions()
    {
        var first = new Session(11, "game");
        var second = new Session(11, "game") { WriteFails = true };
        var result = Execute("app volume set 10 --pid 11 --json", first, second);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("app-audio-partial", result.Output);
        Assert.Equal(.1f, first.Volume);
        Assert.Equal(.5f, second.Volume);
        Assert.True(first.Disposed && second.Disposed);
    }

    [Fact]
    public void CancellationStopsFurtherWritesAndReleasesEverySession()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = new Session(11, "game") { AfterWrite = cancellation.Cancel };
        var second = new Session(11, "game");
        Assert.True(CliCommand.TryParse(["app", "mute", "on", "--pid", "11"], out var command, out _));
        Assert.Throws<OperationCanceledException>(() => CliAppAudioOperation.Execute(command, _ => [first, second], cancellation.Token));
        Assert.Equal(1, first.Writes);
        Assert.Equal(0, second.Writes);
        Assert.True(first.Disposed && second.Disposed);
    }

    [Fact]
    public void InvalidForwardedRequestDoesNotEnumerate()
    {
        var command = new CliCommand { Action = CliAction.AppVolumeSet, Key = "pid:11", Value = "NaN" };
        Assert.Equal(2, CliAppAudioOperation.Execute(command, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken).ExitCode);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    public void HelpAndCompletionExposeAppCommands(string shell)
    {
        Assert.True(CliCommand.TryParse(["app", "--help"], out var command, out _));
        Assert.Equal("app", command.Key);
        Assert.Contains("app volume", CliCommand.GetHelpText("app"));
        string completion = CliShellCompletionGenerator.GetScript(shell);
        Assert.Contains("--pid", completion);
        Assert.Contains("--process", completion);
    }

    private static CliExecutionResult Execute(string input, params Session[] sessions)
    {
        Assert.True(CliCommand.TryParse(input.Split(' '), out var command, out string? error), error);
        return CliAppAudioOperation.Execute(command, _ => sessions, TestContext.Current.CancellationToken);
    }

    private sealed class Session(int pid, string name, float volume = .5f, bool muted = false) : CliAppAudioOperation.ISession
    {
        public int ProcessId => pid;
        public string ProcessName => name;
        public bool IsCurrent { get; set; } = true;
        public float Volume { get; set; } = volume;
        public bool Muted { get; set; } = muted;
        public Action<Session, int>? BeforeRead { get; init; }
        private int _reads;
        public bool ReadFails { get; init; }
        public bool WriteFails { get; init; }
        public Action? AfterWrite { get; init; }
        public bool Disposed { get; private set; }
        public int Writes { get; private set; }
        public (float Volume, bool Muted) Read()
        {
            BeforeRead?.Invoke(this, ++_reads);
            return ReadFails ? throw new InvalidOperationException() : (Volume, Muted);
        }
        public void SetVolume(float value) { Write(); Volume = value; }
        public void SetMute(bool value) { Write(); Muted = value; }
        private void Write()
        {
            if (WriteFails) throw new InvalidOperationException();
            Writes++;
            AfterWrite?.Invoke();
        }
        public void Dispose() => Disposed = true;
    }
}
