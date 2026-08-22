namespace AudioPilot.Services.UI.MediaOverlay
{
    internal static class MediaOverlaySessionMaterializer
    {
        public static async Task<IReadOnlyList<MediaOverlaySessionSnapshot>> MaterializeAsync<TSession>(
            IReadOnlyList<TSession> sessions,
            Func<TSession, CancellationToken, Task<MediaOverlaySessionSnapshot>> snapshotFactory,
            Action<Exception> failureHandler,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sessions);
            ArgumentNullException.ThrowIfNull(snapshotFactory);
            ArgumentNullException.ThrowIfNull(failureHandler);

            var snapshots = new List<MediaOverlaySessionSnapshot>(sessions.Count);
            for (int index = 0; index < sessions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    MediaOverlaySessionSnapshot snapshot = await snapshotFactory(sessions[index], cancellationToken);
                    if (!MediaOverlayEngine.IsSessionMissing(snapshot))
                    {
                        snapshots.Add(snapshot);
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

            return snapshots;
        }
    }
}
