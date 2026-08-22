using Windows.Media.Control;

namespace AudioPilot.Platform
{
    public static partial class MediaKeyHelper
    {
        internal static async Task<IMediaPlaybackSession?> GetPlaybackSessionAsync(CancellationToken cancellationToken)
        {
            GlobalSystemMediaTransportControlsSession? session = await GetSingleMediaSessionAsync("media-playback", cancellationToken).ConfigureAwait(false);
            return session == null ? null : new NativePlaybackSession(session);
        }

        private static async Task<GlobalSystemMediaTransportControlsSession?> GetSingleMediaSessionAsync(string operation, CancellationToken cancellationToken)
        {
            SystemMediaManagerLease lease = await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
            SystemMediaSessionState state = ReadSystemMediaSessionState(lease);
            try
            {
                var (selected, reason) = SelectSingleMediaSession(state.Sessions, state.Current,
                    static session => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                GetLogger()?.Debug("MediaKeyHelper", () => $"{operation}-selection | reason={reason} sessions={state.Sessions.Count} source={(selected == null ? "none" : GetSafeSessionSourceForLog(selected))}");
                return selected;
            }
            catch (NotSupportedException)
            {
                GetLogger()?.Info("MediaKeyHelper", $"{operation}-selection-unavailable | reason=ambiguous-session sessions={state.Sessions.Count}");
                throw;
            }
        }

        /// <summary>Preserves the current target even when another session supports more controls; ambiguous fallbacks are rejected.</summary>
        internal static (T? Session, string Reason) SelectSingleMediaSession<T>(IReadOnlyList<T> sessions, T? current, Func<T, bool> isPlaying) where T : class
        {
            if (current != null) return (current, "current");
            if (sessions.Count == 0) return (null, "none");
            if (sessions.Count == 1) return (sessions[0], "only-session");

            T? selected = null;
            foreach (T candidate in sessions)
            {
                if (!isPlaying(candidate)) continue;
                if (selected != null) throw new NotSupportedException("Multiple playing sessions have no designated current session.");
                selected = candidate;
            }

            return selected != null ? (selected, "only-playing") : throw new NotSupportedException("No unambiguous media session.");
        }

        private static async Task<bool> IsSessionPresentAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken cancellationToken)
        {
            SystemMediaManagerLease lease = await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
            return ReadSystemMediaSessionState(lease).Sessions.Any(candidate => Equals(candidate, session));
        }

        private sealed class NativePlaybackSession(GlobalSystemMediaTransportControlsSession session) : IMediaPlaybackSession
        {
            public Task<bool> IsPresentAsync(CancellationToken cancellationToken) => IsSessionPresentAsync(session, cancellationToken);

            public MediaPlaybackState ReadState()
            {
                var info = session.GetPlaybackInfo();
                return new(info.PlaybackStatus, info.Controls.IsPlayEnabled, info.Controls.IsPauseEnabled);
            }

            public Task<bool> SetPlayingAsync(bool playing) => playing ? session.TryPlayAsync().AsTask() : session.TryPauseAsync().AsTask();
        }
    }
}
