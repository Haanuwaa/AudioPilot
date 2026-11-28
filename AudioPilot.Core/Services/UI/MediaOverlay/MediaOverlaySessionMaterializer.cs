namespace AudioPilot.Services.UI.MediaOverlay
{
    internal static class MediaOverlaySessionMaterializer
    {
        public static async Task<IReadOnlyList<MediaOverlaySessionSnapshot>> MaterializeAsync<TSession>(
            IReadOnlyList<TSession> sessions,
            Func<TSession, CancellationToken, Task<MediaOverlaySessionSnapshot>> snapshotFactory,
            Action<Exception> failureHandler,
            CancellationToken cancellationToken,
            Func<TSession, bool>? includeSession = null)
        {
            ArgumentNullException.ThrowIfNull(sessions);
            ArgumentNullException.ThrowIfNull(snapshotFactory);
            ArgumentNullException.ThrowIfNull(failureHandler);

            if (sessions.Count == 0)
            {
                return [];
            }

            List<TSession> included = [with(sessions.Count)];
            for (int index = 0; index < sessions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    bool include = includeSession is null || includeSession(sessions[index]);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (include)
                    {
                        included.Add(sessions[index]);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failureHandler(ex);
                }
            }

            if (included.Count == 0)
            {
                return [];
            }

            if (included.Count == 1)
            {
                try
                {
                    MediaOverlaySessionSnapshot snapshot = await snapshotFactory(included[0], cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return MediaOverlayEngine.IsSessionMissing(snapshot) ? [] : [snapshot];
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failureHandler(ex);
                    return [];
                }
            }

            var results = new (bool HasSnapshot, MediaOverlaySessionSnapshot Snapshot, Exception? Error)[included.Count];
            var tasks = new Task[included.Count];
            for (int index = 0; index < included.Count; index++)
            {
                int slot = index;
                TSession session = included[slot];
                tasks[slot] = MaterializeSlotAsync(slot, session, snapshotFactory, results, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = new List<MediaOverlaySessionSnapshot>(included.Count);
            for (int index = 0; index < results.Length; index++)
            {
                (bool hasSnapshot, MediaOverlaySessionSnapshot snapshot, Exception? error) = results[index];
                if (error != null)
                {
                    failureHandler(error);
                }
                else if (hasSnapshot)
                {
                    snapshots.Add(snapshot);
                }
            }

            return snapshots;
        }

        private static async Task MaterializeSlotAsync<TSession>(
            int slot,
            TSession session,
            Func<TSession, CancellationToken, Task<MediaOverlaySessionSnapshot>> snapshotFactory,
            (bool HasSnapshot, MediaOverlaySessionSnapshot Snapshot, Exception? Error)[] results,
            CancellationToken cancellationToken)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = await snapshotFactory(session, cancellationToken).ConfigureAwait(false);
                if (!MediaOverlayEngine.IsSessionMissing(snapshot))
                {
                    results[slot] = (true, snapshot, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Propagated after Task.WhenAll via ThrowIfCancellationRequested().
            }
            catch (Exception ex)
            {
                results[slot] = (false, default, ex);
            }
        }
    }
}
