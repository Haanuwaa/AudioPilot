using Windows.Media.Control;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayCommandSnapshotCache
    {
        private readonly Lock _lock = new();
        private readonly Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager>> _managerFactory;
        private readonly Dictionary<long, Task<GlobalSystemMediaTransportControlsSessionManager>> _managerByCommandSequence = [];
        private readonly Dictionary<long, Task<IReadOnlyList<MediaOverlaySessionSnapshot>>> _snapshotsByCommandSequence = [];

        public MediaOverlayCommandSnapshotCache()
            : this(static cancellationToken =>
                GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken))
        {
        }

        internal MediaOverlayCommandSnapshotCache(
            Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager>> managerFactory)
        {
            _managerFactory = managerFactory ?? throw new ArgumentNullException(nameof(managerFactory));
        }

        internal int ManagerEntryCountForTests
        {
            get
            {
                lock (_lock)
                {
                    return _managerByCommandSequence.Count;
                }
            }
        }

        internal int SnapshotEntryCountForTests
        {
            get
            {
                lock (_lock)
                {
                    return _snapshotsByCommandSequence.Count;
                }
            }
        }

        public Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync(
            long commandSequence,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_managerByCommandSequence.TryGetValue(commandSequence, out Task<GlobalSystemMediaTransportControlsSessionManager>? existing))
                {
                    return existing;
                }

                Task<GlobalSystemMediaTransportControlsSessionManager> created = RemoveManagerOnFailureAsync(
                    commandSequence,
                    RequestManagerAsync(commandSequence, cancellationToken));
                _managerByCommandSequence[commandSequence] = created;
                return created;
            }
        }

        public Task<IReadOnlyList<MediaOverlaySessionSnapshot>> GetSessionSnapshotsAsync(
            long commandSequence,
            Func<Task<IReadOnlyList<MediaOverlaySessionSnapshot>>> factory)
        {
            lock (_lock)
            {
                if (_snapshotsByCommandSequence.TryGetValue(commandSequence, out Task<IReadOnlyList<MediaOverlaySessionSnapshot>>? existing))
                {
                    return existing;
                }

                Task<IReadOnlyList<MediaOverlaySessionSnapshot>> materializedSnapshots;
                try
                {
                    materializedSnapshots = factory();
                }
                catch (Exception ex)
                {
                    return Task.FromException<IReadOnlyList<MediaOverlaySessionSnapshot>>(ex);
                }

                Task<IReadOnlyList<MediaOverlaySessionSnapshot>> created = RemoveSessionSnapshotsOnFailureAsync(
                    commandSequence,
                    materializedSnapshots);
                _snapshotsByCommandSequence[commandSequence] = created;
                return created;
            }
        }

        public void InvalidateSnapshots(long commandSequence)
        {
            lock (_lock)
            {
                _snapshotsByCommandSequence.Remove(commandSequence);
            }
        }

        public void Clear(long commandSequence)
        {
            lock (_lock)
            {
                _managerByCommandSequence.Remove(commandSequence);
                _snapshotsByCommandSequence.Remove(commandSequence);
            }
        }

        private async Task<GlobalSystemMediaTransportControlsSessionManager> RemoveManagerOnFailureAsync(
            long commandSequence,
            Task<GlobalSystemMediaTransportControlsSessionManager> managerTask)
        {
            try
            {
                return await managerTask;
            }
            catch
            {
                Clear(commandSequence);
                throw;
            }
        }

        private async Task<IReadOnlyList<MediaOverlaySessionSnapshot>> RemoveSessionSnapshotsOnFailureAsync(
            long commandSequence,
            Task<IReadOnlyList<MediaOverlaySessionSnapshot>> sessionSnapshotsTask)
        {
            try
            {
                return await sessionSnapshotsTask;
            }
            catch
            {
                InvalidateSnapshots(commandSequence);
                throw;
            }
        }

        private async Task<GlobalSystemMediaTransportControlsSessionManager> RequestManagerAsync(
            long commandSequence,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _managerFactory(cancellationToken);
            }
            catch
            {
                Clear(commandSequence);
                throw;
            }
        }
    }
}
