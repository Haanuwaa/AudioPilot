using AudioPilot.Logging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AudioPilot.Platform
{
    public static partial class MediaKeyHelper
    {
        internal static async Task<IMediaSeekSession?> GetSeekSessionAsync(CancellationToken cancellationToken)
        {
            SystemMediaManagerLease lease = await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SystemMediaSessionState state = ReadSystemMediaSessionState(lease);
            var (selected, reason) = SelectSeekSession(state.Sessions, state.Current, session =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ReadSeekTimeline(session, requirePlaying: !Equals(session, state.Current));
            });
            GetLogger()?.Debug("MediaKeyHelper", () => $"media-seek-selection | reason={reason} sessions={state.Sessions.Count} source={(selected == null ? "none" : GetSafeSessionSourceForLog(selected))}");
            bool requirePlaying = !Equals(selected, state.Current) && (state.Current != null || state.Sessions.Count > 1);
            return selected == null ? null : new NativeSeekSession(selected, requirePlaying);
        }

        internal static (T? Session, string Reason) SelectSeekSession<T>(IReadOnlyList<T> sessions, T? current,
            Func<T, MediaSeekTimeline> readTimeline) where T : class
        {
            if (current != null && CanUseTimeline(readTimeline(current))) return (current, "current");
            if (current == null && sessions.Count == 0) return (null, "none");
            if (current == null && sessions.Count == 1) return (sessions[0], "only-session");

            T? selected = null;
            foreach (T candidate in sessions)
            {
                if (Equals(candidate, current) || Equals(candidate, selected)) continue;
                var timeline = readTimeline(candidate);
                if (!timeline.IsPlaying || !CanUseTimeline(timeline)) continue;
                if (selected != null) throw new NotSupportedException("Multiple playing media sessions support seeking.");
                selected = candidate;
            }

            if (selected != null) return (selected, "only-seekable-playing");
            if (current != null) return (current, "current-unavailable");
            throw new NotSupportedException("No unambiguous seekable media session.");
        }

        private static bool CanUseTimeline(MediaSeekTimeline timeline) => timeline.CanSeek && timeline.Minimum >= TimeSpan.Zero && timeline.Maximum > timeline.Minimum;

        private static MediaSeekTimeline ReadSeekTimeline(GlobalSystemMediaTransportControlsSession session, bool requirePlaying)
        {
            var playback = session.GetPlaybackInfo();
            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (!playback.Controls.IsPlaybackPositionEnabled || (requirePlaying && !playing)) return default;

            var timeline = session.GetTimelineProperties();
            TimeSpan minimum = timeline.MinSeekTime > timeline.StartTime ? timeline.MinSeekTime : timeline.StartTime;
            TimeSpan maximum = timeline.EndTime > TimeSpan.Zero && timeline.EndTime < timeline.MaxSeekTime ? timeline.EndTime : timeline.MaxSeekTime;
            return new(true, playing, playback.PlaybackRate ?? 1, timeline.Position, minimum, maximum, timeline.LastUpdatedTime, null);
        }

        private sealed class NativeSeekSession(GlobalSystemMediaTransportControlsSession session, bool requirePlaying) : IMediaSeekSession
        {
            public object Identity => session;
            public Task<bool> IsPresentAsync(CancellationToken cancellationToken) => IsSessionPresentAsync(session, cancellationToken);

            public async Task<MediaSeekTimeline> ReadAsync(CancellationToken cancellationToken)
            {
                MediaSeekTimeline timeline = ReadSeekTimeline(session, requirePlaying);
                if (!CanUseTimeline(timeline))
                {
                    return timeline;
                }

                MediaSeekTrackIdentity? identity = null;
                IRandomAccessStreamReference? artwork = null;
                using var metadataDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                metadataDeadline.CancelAfter(150);
                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync().AsTask(metadataDeadline.Token).WaitAsync(metadataDeadline.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(properties.Title))
                    {
                        identity = new(properties.Title, properties.Artist, properties.AlbumTitle, properties.TrackNumber);
                        artwork = MediaOverlayThumbnail.Get(properties);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    GetLogger()?.Debug("MediaKeyHelper", "media-seek-metadata-unavailable | reason=timeout");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    GetLogger()?.Debug("MediaKeyHelper", $"media-seek-metadata-unavailable | error={ex.GetType().Name}");
                }

                timeline = ReadSeekTimeline(session, requirePlaying);
                GetLogger()?.Log(CanUseTimeline(timeline) ? LogLevel.Debug : LogLevel.Info,
                    "MediaKeyHelper", () => FormattableString.Invariant(
                        $"media-seek-timeline | source={GetSafeSessionSourceForLog(session)} playing={timeline.IsPlaying} canSeek={timeline.CanSeek} positionSeconds={timeline.Position.TotalSeconds:F3} minimumSeconds={timeline.Minimum.TotalSeconds:F3} maximumSeconds={timeline.Maximum.TotalSeconds:F3} updatedAt={timeline.UpdatedAt:O}"));
                return timeline with { TrackIdentity = identity, Artwork = artwork };
            }

            public Task<bool> SeekAsync(long positionTicks) => session.TryChangePlaybackPositionAsync(positionTicks).AsTask();
        }
    }
}
