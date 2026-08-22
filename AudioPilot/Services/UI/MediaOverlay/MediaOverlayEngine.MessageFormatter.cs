using AudioPilot.Logging;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal static class MediaOverlayMessageFormatter
    {
        internal static MediaOverlayResult BuildOverlayMessage(MediaOverlayCommand command, SessionSnapshot baseline, SnapshotCaptureResult capture)
        {
            SessionSnapshot snapshot = capture.Snapshot;
            return command switch
            {
                MediaOverlayCommand.PlayPause => BuildPlayPauseMessage(snapshot, baseline),
                MediaOverlayCommand.NextTrack => BuildTrackMessage(snapshot, baseline, capture.RecoveryDisposition, "Next track", "Next track unchanged", "Next track loading", "Next track metadata loading", "Next track unknown"),
                MediaOverlayCommand.PreviousTrack => BuildTrackMessage(snapshot, baseline, capture.RecoveryDisposition, "Previous track", "Previous track unchanged", "Previous track loading", "Previous track metadata loading", "Previous track unknown"),
                _ => BuildUnexpectedCommandFallback(command),
            };
        }

        internal static MediaOverlayResult BuildPlayPauseMessage(SessionSnapshot snapshot, SessionSnapshot baseline)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) && MediaOverlayEngine.IsSessionMissing(baseline))
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    "media-overlay-playpause-hidden | reason=no-active-session",
                    nameof(BuildPlayPauseMessage));
                return MediaOverlayResult.Hidden;
            }

            string header = snapshot.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Playback resumed",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Playback paused",
                _ => "Play/pause command sent",
            };

            SessionSnapshot effectiveSnapshot = snapshot;
            if (!MediaOverlayEngine.HasTrackData(effectiveSnapshot)
                && MediaOverlayEngine.HasTrackData(baseline)
                && CanReuseBaselineTrackForPlayPause(effectiveSnapshot, baseline))
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    () => "media-overlay-playpause-baseline-reused | baseline=" + MediaOverlayEngine.FormatSnapshot(baseline) + " snapshot=" + MediaOverlayEngine.FormatSnapshot(snapshot),
                    nameof(BuildPlayPauseMessage));
                effectiveSnapshot = effectiveSnapshot with
                {
                    Title = baseline.Title,
                    Artist = baseline.Artist,
                    AlbumTitle = baseline.AlbumTitle,
                };
            }

            if (MediaOverlayEngine.HasTrackData(effectiveSnapshot) && IsSnapshotEvidenceForPlayPause(snapshot, baseline))
            {
                string title = string.IsNullOrWhiteSpace(effectiveSnapshot.Title) ? "Unknown title" : effectiveSnapshot.Title;
                return MediaOverlayResult.Track(header, title, effectiveSnapshot.Artist);
            }

            return MediaOverlayResult.Plain(header);
        }

        private static bool CanReuseBaselineTrackForPlayPause(SessionSnapshot snapshot, SessionSnapshot baseline)
        {
            bool statusChanged = snapshot.PlaybackStatus.HasValue
                && baseline.PlaybackStatus.HasValue
                && snapshot.PlaybackStatus.Value != baseline.PlaybackStatus.Value;
            if (!statusChanged)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId))
            {
                return true;
            }

            return string.Equals(snapshot.SourceAppUserModelId, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsSnapshotEvidenceForPlayPause(SessionSnapshot candidate, SessionSnapshot baseline)
        {
            bool statusChanged = candidate.PlaybackStatus.HasValue
                && baseline.PlaybackStatus.HasValue
                && candidate.PlaybackStatus.Value != baseline.PlaybackStatus.Value;

            bool sourceChanged = !string.IsNullOrWhiteSpace(candidate.SourceAppUserModelId)
                && !string.Equals(candidate.SourceAppUserModelId, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);

            bool trackChanged = MediaOverlayEngine.HasTrackData(candidate) && !MediaOverlayEngine.IsSameTrack(candidate, baseline);

            return statusChanged || sourceChanged || trackChanged;
        }

        private static MediaOverlayResult BuildUnexpectedCommandFallback(MediaOverlayCommand command)
        {
            Logger.Instance?.Warning("MediaOverlayHelper", $"media-overlay-formatter-command-unexpected | command={command}", nameof(BuildOverlayMessage));
            return MediaOverlayResult.Hidden;
        }

        private static MediaOverlayResult BuildTrackMessage(
            SessionSnapshot snapshot,
            SessionSnapshot baseline,
            TrackNavigationRecoveryDisposition recoveryDisposition,
            string confirmedHeader,
            string unchangedFallback,
            string sessionDropFallback,
            string metadataPendingFallback,
            string unknownFallback)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) && MediaOverlayEngine.IsSessionMissing(baseline))
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    "media-overlay-track-hidden | reason=no-active-session",
                    nameof(BuildTrackMessage));
                return MediaOverlayResult.Hidden;
            }

            if (recoveryDisposition.Outcome == TrackNavigationRecoveryOutcome.Loading)
            {
                if (recoveryDisposition.FallbackClassification == TrackNavigationFallbackClassification.MetadataPending)
                {
                    Logger.Instance?.Debug(
                        "MediaOverlayHelper",
                        $"media-overlay-track-fallback | reason=metadata-pending returning='{metadataPendingFallback}' baseline={MediaOverlayEngine.FormatSnapshot(baseline)} snapshot={MediaOverlayEngine.FormatSnapshot(snapshot)}",
                        nameof(BuildTrackMessage));
                    return MediaOverlayResult.Plain(metadataPendingFallback);
                }

                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"media-overlay-track-fallback | reason=loading returning='{sessionDropFallback}' baseline={MediaOverlayEngine.FormatSnapshot(baseline)} snapshot={MediaOverlayEngine.FormatSnapshot(snapshot)} classification={recoveryDisposition.FallbackClassification}",
                    nameof(BuildTrackMessage));
                return MediaOverlayResult.Plain(sessionDropFallback);
            }

            if (recoveryDisposition.Outcome == TrackNavigationRecoveryOutcome.Unchanged)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"media-overlay-track-fallback | reason=unchanged returning='{unchangedFallback}' baseline={MediaOverlayEngine.FormatSnapshot(baseline)} latest={MediaOverlayEngine.FormatSnapshot(snapshot)}",
                    nameof(BuildTrackMessage));
                return MediaOverlayResult.Plain(unchangedFallback);
            }

            if (string.IsNullOrWhiteSpace(snapshot.Title)
                && string.IsNullOrWhiteSpace(snapshot.Artist)
                && string.IsNullOrWhiteSpace(snapshot.AlbumTitle))
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"media-overlay-track-fallback | reason=missing-metadata returning='{unknownFallback}' snapshot={MediaOverlayEngine.FormatSnapshot(snapshot)}",
                    nameof(BuildTrackMessage));
                return MediaOverlayResult.Plain(unknownFallback);
            }

            string? title = snapshot.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Unknown title";
            }

            return MediaOverlayResult.Track(confirmedHeader, title, snapshot.Artist);
        }
    }
}
