using AudioPilot.Logging;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal static class MediaOverlayMessageFormatter
    {
        internal const string NoSessionMessage = "No media session detected";
        internal const string NoTrackInformationMessage = "Track information unavailable";

        internal static MediaOverlayResult BuildOverlayMessage(MediaOverlayCommand command, SessionSnapshot baseline, SnapshotCaptureResult capture)
        {
            SessionSnapshot snapshot = capture.Snapshot;
            return command switch
            {
                MediaOverlayCommand.PlayPause => BuildPlayPauseMessage(snapshot, baseline),
                MediaOverlayCommand.NextTrack => BuildTrackMessage(snapshot, baseline, capture.RecoveryDisposition, "Next track", "Next track unchanged", "Next track loading", "Next track metadata loading"),
                MediaOverlayCommand.PreviousTrack => BuildTrackMessage(snapshot, baseline, capture.RecoveryDisposition, "Previous track", "Previous track unchanged", "Previous track loading", "Previous track metadata loading"),
                _ => BuildUnexpectedCommandFallback(command),
            };
        }

        /// <summary>
        /// Reports a playback action only when the sampled status changes. A successful command dispatch
        /// can leave stale GSMTC metadata, so an unchanged status must not confirm or predict playback state.
        /// </summary>
        internal static MediaOverlayResult BuildPlayPauseMessage(SessionSnapshot snapshot, SessionSnapshot baseline, bool hasOtherSessionContext = false)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) && MediaOverlayEngine.IsSessionMissing(baseline))
            {
                if (hasOtherSessionContext)
                {
                    return MediaOverlayResult.Plain("Play/pause command sent");
                }

                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    "media-overlay-playpause-unavailable | reason=no-session-detected",
                    nameof(BuildPlayPauseMessage));
                return MediaOverlayResult.Plain(NoSessionMessage);
            }

            bool hasEvidence = IsSnapshotEvidenceForPlayPause(snapshot, baseline);
            bool statusChanged = snapshot.PlaybackStatus.HasValue
                && baseline.PlaybackStatus.HasValue
                && snapshot.PlaybackStatus != baseline.PlaybackStatus;
            string header = (statusChanged ? snapshot.PlaybackStatus : null) switch
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

            if (MediaOverlayEngine.HasTrackData(effectiveSnapshot) && hasEvidence)
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
            string metadataPendingFallback)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) && MediaOverlayEngine.IsSessionMissing(baseline))
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    "media-overlay-track-unavailable | reason=no-session-detected",
                    nameof(BuildTrackMessage));
                return MediaOverlayResult.Plain(NoSessionMessage);
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

            if (!MediaOverlayEngine.HasTrackData(snapshot))
            {
                bool sessionMissing = MediaOverlayEngine.IsSessionMissing(snapshot);
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"media-overlay-track-fallback | reason={(sessionMissing ? "no-session-detected" : "metadata-unavailable")} snapshot={MediaOverlayEngine.FormatSnapshot(snapshot)}",
                    nameof(BuildTrackMessage));
                return MediaOverlayResult.Plain(sessionMissing ? NoSessionMessage : NoTrackInformationMessage);
            }

            if (recoveryDisposition.Outcome == TrackNavigationRecoveryOutcome.Unchanged)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"media-overlay-track-fallback | reason=unchanged returning='{unchangedFallback}' baseline={MediaOverlayEngine.FormatSnapshot(baseline)} latest={MediaOverlayEngine.FormatSnapshot(snapshot)}",
                    nameof(BuildTrackMessage));
                return MediaOverlayResult.Plain(unchangedFallback);
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
