using System.Collections.Concurrent;
using AudioPilot.Constants;

namespace AudioPilot.Services.Bluetooth
{
    internal sealed class RememberedBluetoothEndpointCache(
        TimeSpan? ttl = null,
        int maxEntries = AppConstants.Bluetooth.RememberedEndpointCacheMaxEntries,
        Func<DateTime>? utcNow = null)
    {
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(AppConstants.Bluetooth.RememberedEndpointCacheTtlHours);
        private const int DefaultMaxEntries = AppConstants.Bluetooth.RememberedEndpointCacheMaxEntries;

        private readonly ConcurrentDictionary<string, CacheEntry> _endpointIdByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
        private readonly int _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
        private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

        private readonly record struct CacheEntry(string EndpointId, DateTime StoredAtUtc);

        internal int EntryCountForTests => _endpointIdByKey.Count;

        public bool TryGetEndpointId(string key, out string endpointId)
        {
            endpointId = string.Empty;
            DateTime nowUtc = _utcNow();
            PruneExpiredEntries(nowUtc);

            if (string.IsNullOrWhiteSpace(key)
                || !_endpointIdByKey.TryGetValue(key, out CacheEntry entry)
                || string.IsNullOrWhiteSpace(entry.EndpointId))
            {
                return false;
            }

            if ((nowUtc - entry.StoredAtUtc) > _ttl)
            {
                ForgetEndpointId(key);
                return false;
            }

            endpointId = entry.EndpointId;
            return true;
        }

        public void RememberEndpointId(string key, string endpointId)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(endpointId))
            {
                return;
            }

            DateTime nowUtc = _utcNow();
            PruneExpiredEntries(nowUtc);
            _endpointIdByKey[key] = new CacheEntry(endpointId, nowUtc);
            TrimOverflow();
        }

        public void ForgetEndpointId(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _endpointIdByKey.TryRemove(key, out _);
        }

        private void PruneExpiredEntries(DateTime nowUtc)
        {
            foreach ((string key, CacheEntry entry) in _endpointIdByKey)
            {
                if ((nowUtc - entry.StoredAtUtc) > _ttl)
                {
                    _endpointIdByKey.TryRemove(key, out _);
                }
            }
        }

        private void TrimOverflow()
        {
            int overflow = _endpointIdByKey.Count - _maxEntries;
            if (overflow <= 0)
            {
                return;
            }

            List<KeyValuePair<string, CacheEntry>> entries = [.. _endpointIdByKey];
            entries.Sort(static (left, right) => left.Value.StoredAtUtc.CompareTo(right.Value.StoredAtUtc));

            for (int index = 0; index < overflow && index < entries.Count; index++)
            {
                _endpointIdByKey.TryRemove(entries[index].Key, out _);
            }
        }
    }
}
