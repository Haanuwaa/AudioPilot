using System.Diagnostics;
using System.Globalization;
using AudioPilot.Logging;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed partial class MediaOverlayEngine
    {
        public async Task<SessionSnapshot> GetCurrentMediaSnapshotAsync(CancellationToken cancellationToken = default)
        {
            long commandSequence = _sessionTracker.BeginReadOnlySnapshot();
            long started = Stopwatch.GetTimestamp();

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_timingProfile.MaxCaptureDurationMs));

            try
            {
                (SessionSnapshot snapshot, bool hasCurrentSession, int? fallbackCandidateCount) =
                    await TryGetCurrentMediaSnapshotAsync(commandSequence, timeoutCts.Token);
                ThrowIfSuperseded(commandSequence, timeoutCts.Token);
                long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Logger.Instance.Debug("MediaOverlayHelper", () => BuildCurrentMediaSnapshotLog(
                    snapshot, hasCurrentSession, fallbackCandidateCount, elapsedMs));
                return snapshot;
            }
            catch (OperationCanceledException)
            {
                string outcome = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? "timeout"
                    : "canceled";
                Logger.Instance.Debug("MediaOverlayHelper", () =>
                    $"media-current-snapshot | outcome={outcome} elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance.Debug("MediaOverlayHelper", () =>
                    $"media-current-snapshot | outcome=capture-failed exceptionType={ex.GetType().Name} hresult=0x{ex.HResult:X8} elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
                return SessionSnapshot.Empty;
            }
            finally
            {
                _commandSnapshotCache.Clear(commandSequence);
            }
        }

        internal static string BuildCurrentMediaSnapshotLog(
            SessionSnapshot snapshot, bool hasCurrentSession, int? fallbackCandidateCount, long elapsedMs)
        {
            string outcome = IsSessionMissing(snapshot)
                ? hasCurrentSession || fallbackCandidateCount > 0 ? "session-unavailable" : "no-session"
                : HasTrackData(snapshot) ? "available" : "metadata-unavailable";
            string candidateCount = fallbackCandidateCount?.ToString(CultureInfo.InvariantCulture) ?? "not-enumerated";
            return $"media-current-snapshot | outcome={outcome} currentSession={hasCurrentSession} fallbackCandidates={candidateCount} source={LogPrivacy.Id(snapshot.SourceAppUserModelId)} playbackStatus={snapshot.PlaybackStatus?.ToString() ?? "unknown"} hasTitle={!string.IsNullOrWhiteSpace(snapshot.Title)} hasArtist={!string.IsNullOrWhiteSpace(snapshot.Artist)} hasAlbum={!string.IsNullOrWhiteSpace(snapshot.AlbumTitle)} elapsedMs={elapsedMs}";
        }

        private Task<SessionSnapshot> TryGetCurrentSnapshotAsync(long commandSequence, CancellationToken cancellationToken)
        {
            return TryGetCurrentSnapshotAsync(null, commandSequence, null, false, cancellationToken);
        }

        private async Task<(SessionSnapshot Snapshot, bool HasCurrentSession, int? FallbackCandidateCount)> TryGetCurrentMediaSnapshotAsync(
            long commandSequence, CancellationToken cancellationToken)
        {
            ThrowIfSuperseded(commandSequence, cancellationToken);

            if (_currentSnapshotOverride != null)
            {
                SessionSnapshot overrideSnapshot = await _currentSnapshotOverride(
                    null,
                    commandSequence,
                    cancellationToken);
                if (!IsSessionMissing(overrideSnapshot))
                {
                    return (overrideSnapshot, true, null);
                }

                if (_sessionSnapshotsOverride != null)
                {
                    List<SessionSnapshot> overrideSnapshots = await TryGetSessionSnapshotsAsync(commandSequence, cancellationToken);
                    ThrowIfSuperseded(commandSequence, cancellationToken);
                    return (SelectBestCurrentMediaSnapshot(overrideSnapshots), false, overrideSnapshots.Count);
                }
            }

            GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence, cancellationToken);
            GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session != null)
            {
                SessionSnapshot currentSnapshot = await TryGetCurrentMaterializedSnapshotAsync(
                    session,
                    commandSequence,
                    cancellationToken);
                ThrowIfSuperseded(commandSequence, cancellationToken);
                return (currentSnapshot, true, null);
            }

            List<SessionSnapshot> materializedSnapshots = await TryGetSessionSnapshotsAsync(commandSequence, cancellationToken);
            ThrowIfSuperseded(commandSequence, cancellationToken);
            return (SelectBestCurrentMediaSnapshot(materializedSnapshots), false, materializedSnapshots.Count);
        }

        private async Task<SessionSnapshot> TryGetCurrentSnapshotAsync(
            string? preferredSourceAppUserModelId,
            long commandSequence,
            SessionSnapshot? preferredReferenceSnapshot,
            bool allowSingleCandidateMetadataChangeFallback,
            CancellationToken cancellationToken)
        {
            if (_currentSnapshotOverride != null)
            {
                SessionSnapshot overrideSnapshot = await _currentSnapshotOverride(
                    preferredSourceAppUserModelId,
                    commandSequence,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
                {
                    if (preferredReferenceSnapshot is SessionSnapshot reference
                        && overrideSnapshot.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        && !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(reference, overrideSnapshot))
                    {
                        return overrideSnapshot;
                    }

                    return _preferredSourceResolver.ResolvePreferredSourceSnapshots(
                        preferredSourceAppUserModelId,
                        preferredReferenceSnapshot,
                        allowSingleCandidateMetadataChangeFallback,
                        commandSequence,
                        [overrideSnapshot]);
                }

                return overrideSnapshot;
            }

            try
            {
                ThrowIfSuperseded(commandSequence, cancellationToken);

                if (!string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
                {
                    SessionSnapshot preferredSnapshot = await TryResolvePreferredSourceSnapshotAsync(
                        preferredSourceAppUserModelId,
                        preferredReferenceSnapshot,
                        allowSingleCandidateMetadataChangeFallback,
                        commandSequence,
                        cancellationToken);
                    ThrowIfSuperseded(commandSequence, cancellationToken);
                    return preferredSnapshot;
                }

                GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence, cancellationToken);
                GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
                if (session == null)
                {
                    return SessionSnapshot.Empty;
                }

                SessionSnapshot snapshot = await TryGetCurrentMaterializedSnapshotAsync(
                    session,
                    commandSequence,
                    cancellationToken);
                ThrowIfSuperseded(commandSequence, cancellationToken);
                return snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"Failed to capture current media snapshot for source={LogPrivacy.Id(preferredSourceAppUserModelId)}. {ex.GetType().Name}",
                    nameof(TryGetCurrentSnapshotAsync));
                return SessionSnapshot.Empty;
            }
        }

        private async Task<SessionSnapshot> TryGetCurrentMaterializedSnapshotAsync(
            GlobalSystemMediaTransportControlsSession session,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            string? currentSourceAppUserModelId = CleanValue(session.SourceAppUserModelId);
            if (string.IsNullOrWhiteSpace(currentSourceAppUserModelId))
            {
                return await TryBuildSnapshotAsync(session, cancellationToken);
            }

            IReadOnlyList<SessionSnapshot> materializedSnapshots = await GetMaterializedSessionSnapshotsAsync(commandSequence, cancellationToken);
            List<SessionSnapshot> matchingSnapshots = GetSnapshotsForSource(materializedSnapshots, currentSourceAppUserModelId);
            if (matchingSnapshots.Count > 0)
            {
                return _preferredSourceResolver.ResolvePreferredSourceSnapshots(
                    currentSourceAppUserModelId,
                    preferredReferenceSnapshot: null,
                    allowSingleCandidateMetadataChangeFallback: false,
                    commandSequence,
                    matchingSnapshots);
            }

            return await TryBuildSnapshotAsync(session, cancellationToken);
        }

        private async Task<IReadOnlyList<SessionSnapshot>> GetMaterializedSessionSnapshotsAsync(
            long commandSequence,
            CancellationToken cancellationToken)
        {
            return await _commandSnapshotCache.GetSessionSnapshotsAsync(
                commandSequence,
                async () =>
                {
                    try
                    {
                        ThrowIfSuperseded(commandSequence, cancellationToken);
                        GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence, cancellationToken);
                        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
                        return await MediaOverlaySessionMaterializer.MaterializeAsync(
                            sessions,
                            async (session, token) =>
                            {
                                ThrowIfSuperseded(commandSequence, token);
                                return await TryBuildSnapshotAsync(session, token);
                            },
                            ex => Logger.Instance?.Debug(
                                "MediaOverlayHelper",
                                $"Failed to materialize one media session. {ex.GetType().Name}",
                                nameof(GetMaterializedSessionSnapshotsAsync)),
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance?.Debug(
                            "MediaOverlayHelper",
                            $"Failed to enumerate and materialize media sessions. {ex.GetType().Name}",
                            nameof(GetMaterializedSessionSnapshotsAsync));
                    }

                    return [];
                });
        }

        private async Task<Dictionary<string, SessionSnapshot>> TryGetSnapshotsBySourceAsync(
            long commandSequence,
            CancellationToken cancellationToken)
        {
            if (_snapshotsBySourceOverride != null)
            {
                return await _snapshotsBySourceOverride(commandSequence, cancellationToken);
            }

            var snapshots = new Dictionary<string, SessionSnapshot>(StringComparer.OrdinalIgnoreCase);

            try
            {
                IReadOnlyList<SessionSnapshot> materializedSnapshots = await GetMaterializedSessionSnapshotsAsync(commandSequence, cancellationToken);
                for (int index = 0; index < materializedSnapshots.Count; index++)
                {
                    ThrowIfSuperseded(commandSequence, cancellationToken);
                    SessionSnapshot snapshot = materializedSnapshots[index];
                    if (string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId))
                    {
                        continue;
                    }

                    if (snapshots.TryGetValue(snapshot.SourceAppUserModelId, out SessionSnapshot existing))
                    {
                        if (MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, HasTrackData(snapshot), !string.IsNullOrWhiteSpace(snapshot.AlbumTitle), snapshot.PositionSeconds)
                            > MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(existing.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, HasTrackData(existing), !string.IsNullOrWhiteSpace(existing.AlbumTitle), existing.PositionSeconds))
                        {
                            snapshots[snapshot.SourceAppUserModelId] = snapshot;
                        }

                        continue;
                    }

                    snapshots[snapshot.SourceAppUserModelId] = snapshot;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Failed to enumerate media sessions by source. {ex.GetType().Name}",
                    nameof(TryGetSnapshotsBySourceAsync));
            }

            return snapshots;
        }

        private async Task<List<SessionSnapshot>> TryGetSessionSnapshotsAsync(
            long commandSequence,
            CancellationToken cancellationToken)
        {
            if (_sessionSnapshotsOverride != null)
            {
                return await _sessionSnapshotsOverride(commandSequence, cancellationToken);
            }

            var snapshots = new List<SessionSnapshot>();

            try
            {
                snapshots.AddRange(await GetMaterializedSessionSnapshotsAsync(commandSequence, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Failed to enumerate media sessions. {ex.GetType().Name}",
                    nameof(TryGetSessionSnapshotsAsync));
            }

            return snapshots;
        }

        private async Task<SessionSnapshot> TryResolvePreferredSourceSnapshotAsync(
            string preferredSourceAppUserModelId,
            SessionSnapshot? preferredReferenceSnapshot,
            bool allowSingleCandidateMetadataChangeFallback,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<SessionSnapshot> materializedSnapshots = await GetMaterializedSessionSnapshotsAsync(commandSequence, cancellationToken);
            List<SessionSnapshot> matchingSnapshots = GetSnapshotsForSource(materializedSnapshots, preferredSourceAppUserModelId);

            return _preferredSourceResolver.ResolvePreferredSourceSnapshots(
                preferredSourceAppUserModelId,
                preferredReferenceSnapshot,
                allowSingleCandidateMetadataChangeFallback,
                commandSequence,
                matchingSnapshots);
        }

        private static List<SessionSnapshot> GetSnapshotsForSource(
            IReadOnlyList<SessionSnapshot> materializedSnapshots,
            string preferredSourceAppUserModelId)
        {
            List<SessionSnapshot> matchingSnapshots = [];
            for (int index = 0; index < materializedSnapshots.Count; index++)
            {
                SessionSnapshot snapshot = materializedSnapshots[index];
                if (string.Equals(snapshot.SourceAppUserModelId, preferredSourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
                {
                    matchingSnapshots.Add(snapshot);
                }
            }

            return matchingSnapshots;
        }

        private static SessionSnapshot SelectBestCurrentMediaSnapshot(List<SessionSnapshot> materializedSnapshots)
        {
            SessionSnapshot best = SessionSnapshot.Empty;
            int bestScore = int.MinValue;

            for (int index = 0; index < materializedSnapshots.Count; index++)
            {
                SessionSnapshot candidate = materializedSnapshots[index];
                if (IsSessionMissing(candidate))
                {
                    continue;
                }

                int candidateScore = MediaOverlayPreferredSourceCandidateEvaluator.ComputeSnapshotSelectionScore(candidate);
                if (candidateScore > bestScore)
                {
                    best = candidate;
                    bestScore = candidateScore;
                }
            }

            return best;
        }

        /// <summary>
        /// Reads playback state after awaiting metadata so a slow provider does not pair a fresh title
        /// with a playback status captured before the command took effect. Optional state reads fail independently
        /// so an unavailable timeline does not discard successfully captured metadata or playback state.
        /// </summary>
        private static async Task<SessionSnapshot> TryBuildSnapshotAsync(
            GlobalSystemMediaTransportControlsSession session,
            CancellationToken cancellationToken)
        {
            string? sourceAppUserModelId = null;
            string? title = null;
            string? artist = null;
            string? albumTitle = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceAppUserModelId = CleanValue(session.SourceAppUserModelId);
                GlobalSystemMediaTransportControlsSessionMediaProperties media = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
                title = CleanValue(media?.Title);
                artist = CleanValue(media?.Artist);
                albumTitle = CleanValue(media?.AlbumTitle);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Failed to read media properties for source={LogPrivacy.Id(sourceAppUserModelId)}. {ex.GetType().Name}",
                    nameof(TryBuildSnapshotAsync));
            }

            GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                playbackStatus = session.GetPlaybackInfo()?.PlaybackStatus;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Failed to read playback state for source={LogPrivacy.Id(sourceAppUserModelId)}. {ex.GetType().Name}",
                    nameof(TryBuildSnapshotAsync));
            }

            long? positionSeconds = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
                positionSeconds = timeline?.Position.TotalSeconds >= 0
                    ? (long?)Math.Floor(timeline.Position.TotalSeconds)
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Failed to read media timeline for source={LogPrivacy.Id(sourceAppUserModelId)}. {ex.GetType().Name}",
                    nameof(TryBuildSnapshotAsync));
            }

            return new SessionSnapshot(playbackStatus, title, artist, albumTitle, sourceAppUserModelId, positionSeconds);
        }

        private static string? CleanValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim();
        }
    }
}
