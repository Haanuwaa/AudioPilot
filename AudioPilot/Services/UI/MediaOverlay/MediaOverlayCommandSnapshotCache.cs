using Windows.Media.Control;

namespace AudioPilot.Services.UI.MediaOverlay
{
    /// <summary>
    /// Shares captures within a media command. Entries are published before their factories run, and a failed
    /// completion evicts only its own entry so synchronous failures and superseded requests cannot poison the cache.
    /// </summary>
    internal sealed class MediaOverlayCommandSnapshotCache
    {
        private readonly Lock _lock = new();
        private readonly Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager>> _managerFactory;
        private readonly Dictionary<long, CacheEntry<GlobalSystemMediaTransportControlsSessionManager>> _managerByCommandSequence = [];
        private readonly Dictionary<long, CacheEntry<IReadOnlyList<MediaOverlaySessionSnapshot>>> _snapshotsByCommandSequence = [];

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
            return GetOrCreateAsync(_managerByCommandSequence, commandSequence,
                () => _managerFactory(cancellationToken), clearSnapshotsOnFailure: true);
        }

        public Task<IReadOnlyList<MediaOverlaySessionSnapshot>> GetSessionSnapshotsAsync(
            long commandSequence,
            Func<Task<IReadOnlyList<MediaOverlaySessionSnapshot>>> factory)
        {
            return GetOrCreateAsync(_snapshotsByCommandSequence, commandSequence, factory);
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

        private Task<T> GetOrCreateAsync<T>(
            Dictionary<long, CacheEntry<T>> cache,
            long commandSequence,
            Func<Task<T>> factory,
            bool clearSnapshotsOnFailure = false)
        {
            lock (_lock)
            {
                if (cache.TryGetValue(commandSequence, out CacheEntry<T>? existing))
                {
                    return existing.Task;
                }

                var entry = new CacheEntry<T>();
                cache[commandSequence] = entry;
                entry.Task = RunFactoryAsync(cache, commandSequence, entry, factory, clearSnapshotsOnFailure);
                return entry.Task;
            }
        }

        private async Task<T> RunFactoryAsync<T>(
            Dictionary<long, CacheEntry<T>> cache,
            long commandSequence,
            CacheEntry<T> entry,
            Func<Task<T>> factory,
            bool clearSnapshotsOnFailure)
        {
            try
            {
                return await factory().ConfigureAwait(false);
            }
            catch
            {
                lock (_lock)
                {
                    if (cache.TryGetValue(commandSequence, out CacheEntry<T>? current) && ReferenceEquals(current, entry))
                    {
                        cache.Remove(commandSequence);
                        if (clearSnapshotsOnFailure)
                        {
                            _snapshotsByCommandSequence.Remove(commandSequence);
                        }
                    }
                }
                throw;
            }
        }

        private sealed class CacheEntry<T>
        {
            public Task<T> Task { get; set; } = null!;
        }
    }
}
