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

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_timingProfile.MaxCaptureDurationMs));

            try
            {
                return await TryGetCurrentMediaSnapshotAsync(commandSequence, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"Failed to capture current media snapshot. {ex.GetType().Name}",
                    nameof(GetCurrentMediaSnapshotAsync));
                return SessionSnapshot.Empty;
            }
            finally
            {
                _commandSnapshotCache.Clear(commandSequence);
            }
        }

        private async Task<SessionSnapshot> TryGetCurrentSnapshotAsync(long commandSequence, CancellationToken cancellationToken)
        {
            return await TryGetCurrentSnapshotAsync(null, commandSequence, null, false, cancellationToken);
        }

        private async Task<SessionSnapshot> TryGetCurrentMediaSnapshotAsync(long commandSequence, CancellationToken cancellationToken)
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
                    return overrideSnapshot;
                }

                if (_sessionSnapshotsOverride != null)
                {
                    IReadOnlyList<SessionSnapshot> overrideSnapshots = await TryGetSessionSnapshotsAsync(commandSequence, cancellationToken);
                    ThrowIfSuperseded(commandSequence, cancellationToken);
                    return SelectBestCurrentMediaSnapshot(overrideSnapshots);
                }
            }

            GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence, cancellationToken);
            GlobalSystemMediaTransportControlsSession? session = ResolveSession(manager, preferredSourceAppUserModelId: null);
            if (session != null)
            {
                SessionSnapshot currentSnapshot = await TryGetCurrentMaterializedSnapshotAsync(
                    session,
                    commandSequence,
                    cancellationToken);
                ThrowIfSuperseded(commandSequence, cancellationToken);
                return currentSnapshot;
            }

            IReadOnlyList<SessionSnapshot> materializedSnapshots = await TryGetSessionSnapshotsAsync(commandSequence, cancellationToken);
            ThrowIfSuperseded(commandSequence, cancellationToken);
            return SelectBestCurrentMediaSnapshot(materializedSnapshots);
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
                GlobalSystemMediaTransportControlsSession? session = ResolveSession(manager, preferredSourceAppUserModelId);
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

        private static SessionSnapshot SelectBestCurrentMediaSnapshot(IReadOnlyList<SessionSnapshot> materializedSnapshots)
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

        private static async Task<SessionSnapshot> TryBuildSnapshotAsync(
            GlobalSystemMediaTransportControlsSession session,
            CancellationToken cancellationToken)
        {
            string? sourceAppUserModelId = null;
            GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus;
            long? positionSeconds;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = session.GetPlaybackInfo();
                playbackStatus = playbackInfo?.PlaybackStatus;
                sourceAppUserModelId = CleanValue(session.SourceAppUserModelId);
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
                    $"Failed to read media session state for source={LogPrivacy.Id(sourceAppUserModelId)}. {ex.GetType().Name}",
                    nameof(TryBuildSnapshotAsync));
                return SessionSnapshot.Empty;
            }

            string? title = null;
            string? artist = null;
            string? albumTitle = null;
            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties media = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
                (title, artist, albumTitle) = BuildTrackInfo(media?.Title, media?.Artist, media?.AlbumTitle);
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

            return new SessionSnapshot(playbackStatus, title, artist, albumTitle, sourceAppUserModelId, positionSeconds);
        }

        private static GlobalSystemMediaTransportControlsSession? ResolveSession(
            GlobalSystemMediaTransportControlsSessionManager manager,
            string? preferredSourceAppUserModelId)
        {
            if (!string.IsNullOrWhiteSpace(preferredSourceAppUserModelId))
            {
                var sessions = manager.GetSessions();
                return sessions.FirstOrDefault(s => string.Equals(
                    CleanValue(s.SourceAppUserModelId),
                    preferredSourceAppUserModelId,
                    StringComparison.OrdinalIgnoreCase));
            }

            return manager.GetCurrentSession();
        }

        private static (string? Title, string? Artist, string? AlbumTitle) BuildTrackInfo(string? title, string? artist, string? albumTitle)
        {
            string cleanTitle = CleanValue(title) ?? string.Empty;
            string cleanArtist = CleanValue(artist) ?? string.Empty;
            string cleanAlbumTitle = CleanValue(albumTitle) ?? string.Empty;

            return (
                string.IsNullOrWhiteSpace(cleanTitle) ? null : cleanTitle,
                string.IsNullOrWhiteSpace(cleanArtist) ? null : cleanArtist,
                string.IsNullOrWhiteSpace(cleanAlbumTitle) ? null : cleanAlbumTitle);
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
