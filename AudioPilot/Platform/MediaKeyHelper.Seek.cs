using AudioPilot.Logging;
using Windows.Media.Control;

namespace AudioPilot.Platform
{
    public static partial class MediaKeyHelper
    {
        internal static async Task<IMediaSeekSession?> GetSeekSessionAsync(CancellationToken cancellationToken)
        {
            SystemMediaManagerLease lease = await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
            SystemMediaSessionState state = ReadSystemMediaSessionState(lease);
            GlobalSystemMediaTransportControlsSession? selected = state.Current;
            string selection = selected == null ? "none" : "current";
            if (selected == null)
            {
                if (state.Sessions.Count == 1)
                {
                    selected = state.Sessions[0];
                    selection = "only-session";
                }
                else
                {
                    foreach (GlobalSystemMediaTransportControlsSession candidate in state.Sessions)
                    {
                        if (candidate.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                        if (selected != null)
                        {
                            GetLogger()?.Info("MediaKeyHelper", $"media-seek-selection-unavailable | reason=multiple-playing-sessions sessions={state.Sessions.Count}");
                            throw new NotSupportedException("Multiple playing sessions have no designated current session.");
                        }
                        selected = candidate;
                        selection = "only-playing";
                    }

                    if (selected == null && state.Sessions.Count > 0)
                    {
                        GetLogger()?.Info("MediaKeyHelper", $"media-seek-selection-unavailable | reason=no-unambiguous-session sessions={state.Sessions.Count}");
                        throw new NotSupportedException("No unambiguous media session.");
                    }
                }
            }

            GetLogger()?.Debug("MediaKeyHelper", () => $"media-seek-selection | reason={selection} sessions={state.Sessions.Count} source={(selected == null ? "none" : GetSafeSessionSourceForLog(selected))}");
            return selected == null ? null : new NativeSeekSession(selected);
        }

        private sealed class NativeSeekSession(GlobalSystemMediaTransportControlsSession session) : IMediaSeekSession
        {
            public object Identity => session;

            public async Task<MediaSeekTimeline> ReadAsync(CancellationToken cancellationToken)
            {
                if (!session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled)
                {
                    GetLogger()?.Debug("MediaKeyHelper", () => $"media-seek-control-disabled | source={GetSafeSessionSourceForLog(session)}");
                    return default;
                }

                MediaSeekTrackIdentity? identity = null;
                using var metadataDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                metadataDeadline.CancelAfter(150);
                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync().AsTask(metadataDeadline.Token).WaitAsync(metadataDeadline.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(properties.Title))
                    {
                        identity = new(properties.Title, properties.Artist, properties.AlbumTitle, properties.TrackNumber);
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

                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                TimeSpan minimum = timeline.MinSeekTime > timeline.StartTime ? timeline.MinSeekTime : timeline.StartTime;
                TimeSpan maximum = timeline.EndTime > TimeSpan.Zero && timeline.EndTime < timeline.MaxSeekTime ? timeline.EndTime : timeline.MaxSeekTime;
                GetLogger()?.Log(minimum < TimeSpan.Zero || maximum <= minimum ? LogLevel.Info : LogLevel.Debug,
                    "MediaKeyHelper", () => FormattableString.Invariant(
                        $"media-seek-timeline | source={GetSafeSessionSourceForLog(session)} status={playback.PlaybackStatus} canSeek={playback.Controls.IsPlaybackPositionEnabled} positionSeconds={timeline.Position.TotalSeconds:F3} startSeconds={timeline.StartTime.TotalSeconds:F3} endSeconds={timeline.EndTime.TotalSeconds:F3} minimumSeconds={timeline.MinSeekTime.TotalSeconds:F3} maximumSeconds={timeline.MaxSeekTime.TotalSeconds:F3} updatedAt={timeline.LastUpdatedTime:O}"));
                return new(playback.Controls.IsPlaybackPositionEnabled,
                    playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    playback.PlaybackRate ?? 1, timeline.Position, minimum, maximum, timeline.LastUpdatedTime, identity);
            }

            public Task<bool> SeekAsync(long positionTicks) => session.TryChangePlaybackPositionAsync(positionTicks).AsTask();
        }
    }
}
