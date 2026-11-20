using System.Diagnostics;
using System.Globalization;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.UI;
using AudioPilot.Services.UI.MediaOverlay;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public void MediaPlayPause() => DispatchMediaCommand("PlayPause", "Play/Pause", "media-play-pause", MediaKeyHelper.TryPressPlayPauseDetailed);

        public void MediaNextTrack() => DispatchMediaCommand("NextTrack", "Next Track", "media-next-track", MediaKeyHelper.TryPressNextTrackDetailed);

        public void MediaPreviousTrack() => DispatchMediaCommand("PreviousTrack", "Previous Track", "media-previous-track", MediaKeyHelper.TryPressPreviousTrackDetailed);

        private void DispatchMediaCommand(string command, string label, string historyAction, Func<MediaKeyHelper.MediaCommandSendOutcome> send)
        {
            long started = Stopwatch.GetTimestamp();
            var outcome = send();
            var details = new Dictionary<string, string>
            {
                ["command"] = command,
                ["mode"] = "headless",
                ["route"] = outcome.Route.ToString(),
                ["outcome"] = outcome.Sent ? "dispatched" : outcome.IsUnconfirmed ? "unknown" : "failed",
            };
            if (outcome.FailureReason != null) details["reason"] = outcome.FailureReason;
            if (outcome.ErrorCode is int errorCode) details["errorCode"] = errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (outcome.CandidateSourceAppUserModelId is string source) details["candidateSource"] = LogPrivacy.Id(source);
            RecordCliActionHistory(
                ExecutionHistoryKind.Media,
                historyAction,
                outcome.Sent,
                skipped: false,
                outcome.Sent ? $"Dispatched {label} media command." : outcome.IsUnconfirmed ? $"{label} media command was submitted; its outcome is unknown." : $"Failed to dispatch {label} media command.",
                outcome.Sent ? null : outcome.IsUnconfirmed ? "Check playback before retrying." : "Media command send failed.",
                target: command,
                diagCode: outcome.Sent ? "media-command-dispatched" : outcome.IsUnconfirmed ? "media-command-unconfirmed" : "media-command-send-failed",
                elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                details: details);
        }

        private readonly MediaSeekService _mediaSeekService = new(logger: Logger.Instance);
        private readonly MediaPlaybackService _mediaPlaybackService = new(logger: Logger.Instance);

        public async Task<MediaPlaybackResult> MediaSetPlayingAsync(bool playing)
        {
            long started = Stopwatch.GetTimestamp();
            MediaPlaybackResult result = await _mediaPlaybackService.SetPlayingAsync(playing).ConfigureAwait(false);
            if (result.RequestSent) _mediaSeekService.InvalidatePosition();
            Logger.Instance.Log(result.Success ? LogLevel.Debug : LogLevel.Info, "MediaPlaybackService",
                () => FormattableString.Invariant($"media-playback-result | code={result.Code} requested={result.RequestedState} observed={result.ObservedState ?? "unknown"} requestSent={result.RequestSent} confirmed={result.Confirmed} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} source=cli-headless"));
            RecordCliActionHistory(ExecutionHistoryKind.Media, playing ? "media-play" : "media-pause",
                result.Success, skipped: result.Success && !result.RequestSent, result.Message, result.Success ? null : result.Message,
                diagCode: result.Code, elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }

        public async Task<MediaSeekResult> MediaSeekAsync(bool backward, int? seconds)
        {
            int step = seconds ?? MediaSeekStep.Normalize(LoadSettings().Hotkeys.Media.SeekStepSeconds);
            if (step is < 1 or > MediaSeekStep.MaximumSeconds)
            {
                return new(false, "media-seek-invalid-step", "Use a seek step between 1 and 3600 seconds.");
            }

            long started = Stopwatch.GetTimestamp();
            MediaSeekResult result = await _mediaSeekService.SeekAsync(backward ? -step : step).ConfigureAwait(false);
            Logger.Instance.Log(result.Success ? LogLevel.Debug : LogLevel.Info, "MediaSeekService",
                () => FormattableString.Invariant($"media-seek-result | code={result.Code} offsetSeconds={(backward ? -step : step)} previousSeconds={result.PreviousPositionSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "none"} targetSeconds={result.TargetPositionSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "none"} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} source=cli-headless"));
            RecordCliActionHistory(ExecutionHistoryKind.Media, backward ? "media-seek-backward" : "media-seek-forward",
                result.Success, skipped: false, result.Message, result.Success ? null : result.Message,
                diagCode: result.Code, elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }

        public async Task<string> GetMediaStatusAsync(bool jsonOutput, bool redactOutput)
        {
            MediaOverlaySessionSnapshot snapshot = await new MediaOverlayCommandService()
                .GetCurrentMediaSnapshotAsync(throwOnCaptureFailure: true);

            return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput, redactOutput);
        }
    }
}
