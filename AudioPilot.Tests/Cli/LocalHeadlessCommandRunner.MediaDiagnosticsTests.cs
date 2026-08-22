using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{
    [Theory]
    [InlineData(CliAction.MediaPlayPause, "dispatched")]
    [InlineData(CliAction.MediaNextTrack, "dispatched")]
    [InlineData(CliAction.MediaPreviousTrack, "dispatched")]
    [InlineData(CliAction.MediaPlayPause, "unknown")]
    [InlineData(CliAction.MediaNextTrack, "unknown")]
    [InlineData(CliAction.MediaPreviousTrack, "unknown")]
    [InlineData(CliAction.MediaPlayPause, "fault")]
    [InlineData(CliAction.MediaNextTrack, "fault")]
    [InlineData(CliAction.MediaPreviousTrack, "fault")]
    [InlineData(CliAction.MediaPlayPause, "failed")]
    [InlineData(CliAction.MediaNextTrack, "failed")]
    [InlineData(CliAction.MediaPreviousTrack, "failed")]
    public async Task FireAndForgetMediaHistoryPreservesRouteAndOutcome(CliAction action, string outcome)
    {
        int sends = 0;
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = _ =>
            {
                sends++;
                return new(outcome == "dispatched", MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc,
                    SuppressFallback: true, CandidateSourceAppUserModelId: "private-player",
                    FailureReason: outcome == "unknown" ? "gsmc-command-timeout" : outcome == "fault" ? "gsmc-command-fault" : outcome == "failed" ? "gsmc-command-rejected" : null);
            };
            MediaKeyHelper.SendInputOverrideForTests = _ => throw new InvalidOperationException("No retry allowed.");
            using var scope = new HeadlessRunnerScope(new Settings { Miscellaneous = new() { RedactLogContent = false } });
            Assert.Equal(0, (await scope.Runner.ExecuteAsync(new CliCommand { Action = action })).ExitCode);
            Assert.Equal(1, sends);
            var history = JsonNode.Parse(scope.Runner.GetDiagnosticsHistory(true, 1, "media", false))!["data"]!["entries"]!.AsArray();
            var entry = Assert.Single(history)!;
            Assert.Equal(outcome == "dispatched", entry["success"]!.GetValue<bool>());
            Assert.Equal(outcome == "dispatched" ? "media-command-dispatched" : outcome is "unknown" or "fault" ? "media-command-unconfirmed" : "media-command-send-failed", entry["diagCode"]!.GetValue<string>());
            Assert.Equal("CurrentGsmc", entry["details"]!["route"]!.GetValue<string>());
            Assert.Equal(outcome == "fault" ? "unknown" : outcome, entry["details"]!["outcome"]!.GetValue<string>());
            Assert.Equal("headless", entry["details"]!["mode"]!.GetValue<string>());
            string text = scope.Runner.GetDiagnosticsHistory(false, 1, "media", false);
            Assert.Contains(outcome is "unknown" or "fault" ? " | unconfirmed | " : outcome == "failed" ? " | failed | " : " | success | ", text);
            Assert.DoesNotContain("private-player", scope.Runner.GetDiagnosticsHistory(true, 1, "media", true));
        }
        finally { MediaKeyHelper.ResetTestHooks(); }
    }
}
