using System.Collections.Concurrent;
using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal readonly record struct VolumeCacheCleanupResult(int ExpiredVolumeEntries, int ExpiredAliases);

    internal sealed class VolumeCacheStore(
        Logger logger,
        ConcurrentDictionary<string, string> normalizedNameCache,
        Func<string, string> normalizeForMatching,
        TimeSpan volumeCacheTtl,
        int maxVolumeCacheEntries,
        int maxVolumeAliasEntries,
        int maxNormalizedNameCacheEntries,
        Func<long>? timestampTicksProvider = null)
    {
        private readonly record struct VolumeCacheEntry(float Volume, long TimestampTicks);

        private readonly Logger _logger = logger;
        private readonly ConcurrentDictionary<string, string> _normalizedNameCache = normalizedNameCache;
        private readonly Func<string, string> _normalizeForMatching = normalizeForMatching;
        private readonly TimeSpan _volumeCacheTtl = volumeCacheTtl;
        private readonly int _maxVolumeCacheEntries = maxVolumeCacheEntries;
        private readonly int _maxVolumeAliasEntries = maxVolumeAliasEntries;
        private readonly int _maxNormalizedNameCacheEntries = maxNormalizedNameCacheEntries;
        private readonly Func<long> _timestampTicksProvider = timestampTicksProvider ?? GetMonotonicTimestampTicks;
        private readonly ConcurrentDictionary<string, VolumeCacheEntry> _appVolumeCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _volumeCacheAliases = new(StringComparer.OrdinalIgnoreCase);

        public void UpdateCache(string displayName, string? processName, float volume)
        {
            string? canonicalKey = null;
            var entry = new VolumeCacheEntry(volume, _timestampTicksProvider());

            if (!string.IsNullOrWhiteSpace(displayName) &&
                displayName != "Master Volume" &&
                displayName != "Microphone Volume" &&
                displayName != "System Sounds")
            {
                string normalized = _normalizeForMatching(displayName);
                if (!string.IsNullOrEmpty(normalized))
                {
                    canonicalKey = normalized;
                    _appVolumeCache[canonicalKey] = entry;
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("VolumeControlService", () => $"Cached volume for display name '{LogPrivacy.Label(normalized)}': {volume}%");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(processName))
            {
                string normalizedProcess = _normalizeForMatching(processName);
                if (!string.IsNullOrEmpty(normalizedProcess))
                {
                    if (canonicalKey == null)
                    {
                        canonicalKey = normalizedProcess;
                        _appVolumeCache[canonicalKey] = entry;
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("VolumeControlService", () => $"Cached volume for process '{LogPrivacy.Label(normalizedProcess)}': {volume}%");
                        }
                    }
                    else if (!string.Equals(normalizedProcess, canonicalKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _volumeCacheAliases[normalizedProcess] = canonicalKey;
                    }
                }

                string sanitized = AudioDeviceHelper.SanitizeProcessName(processName);
                string normalizedSanitized = _normalizeForMatching(sanitized);
                if (!string.IsNullOrEmpty(normalizedSanitized) &&
                    !string.Equals(normalizedSanitized, normalizedProcess, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(normalizedSanitized, canonicalKey, StringComparison.OrdinalIgnoreCase) &&
                    canonicalKey != null)
                {
                    _volumeCacheAliases[normalizedSanitized] = canonicalKey;
                }
            }

            TrimCachesIfNeeded();
        }

        public float? TryGetCachedVolume(string normalizedKey)
        {
            long nowTicks = _timestampTicksProvider();

            if (_appVolumeCache.TryGetValue(normalizedKey, out var entry))
            {
                if ((nowTicks - entry.TimestampTicks) < _volumeCacheTtl.Ticks)
                {
                    return entry.Volume;
                }

                _appVolumeCache.TryRemove(normalizedKey, out _);
            }

            if (_volumeCacheAliases.TryGetValue(normalizedKey, out var canonicalKey))
            {
                if (_appVolumeCache.TryGetValue(canonicalKey, out entry))
                {
                    if ((nowTicks - entry.TimestampTicks) < _volumeCacheTtl.Ticks)
                    {
                        return entry.Volume;
                    }

                    _appVolumeCache.TryRemove(canonicalKey, out _);
                }

                _volumeCacheAliases.TryRemove(normalizedKey, out _);
            }

            return null;
        }

        public VolumeCacheCleanupResult CleanupExpiredEntries()
        {
            long nowTicks = _timestampTicksProvider();
            var expiredVolumeKeys = new List<string>();
            var expiredAliases = new List<string>();

            foreach (var kvp in _appVolumeCache)
            {
                if ((nowTicks - kvp.Value.TimestampTicks) > _volumeCacheTtl.Ticks)
                {
                    expiredVolumeKeys.Add(kvp.Key);
                }
            }

            foreach (string key in expiredVolumeKeys)
            {
                _appVolumeCache.TryRemove(key, out _);
            }

            foreach (var kvp in _volumeCacheAliases)
            {
                if (!_appVolumeCache.ContainsKey(kvp.Value))
                {
                    expiredAliases.Add(kvp.Key);
                }
            }

            foreach (string alias in expiredAliases)
            {
                _volumeCacheAliases.TryRemove(alias, out _);
            }

            TrimCachesIfNeeded();
            return new VolumeCacheCleanupResult(expiredVolumeKeys.Count, expiredAliases.Count);
        }

        public void Clear()
        {
            _appVolumeCache.Clear();
            _volumeCacheAliases.Clear();
            _normalizedNameCache.Clear();
        }

        private static long GetMonotonicTimestampTicks() =>
            Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;

        private void TrimCachesIfNeeded()
        {
            int volumeCount = _appVolumeCache.Count;
            if (volumeCount > _maxVolumeCacheEntries)
            {
                int overflow = volumeCount - _maxVolumeCacheEntries;
                var oldestEntries = new PriorityQueue<(string Key, long Timestamp), long>(
                    overflow,
                    Comparer<long>.Create(static (left, right) => right.CompareTo(left)));

                foreach (var kvp in _appVolumeCache)
                {
                    long timestamp = kvp.Value.TimestampTicks;

                    if (oldestEntries.Count < overflow)
                    {
                        oldestEntries.Enqueue((kvp.Key, timestamp), timestamp);
                        continue;
                    }

                    if (!oldestEntries.TryPeek(out _, out long newestTrackedTimestamp))
                    {
                        continue;
                    }

                    if (timestamp < newestTrackedTimestamp)
                    {
                        _ = oldestEntries.Dequeue();
                        oldestEntries.Enqueue((kvp.Key, timestamp), timestamp);
                    }
                }

                var removedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (oldestEntries.TryDequeue(out var entry, out _))
                {
                    if (_appVolumeCache.TryRemove(entry.Key, out _))
                    {
                        removedKeys.Add(entry.Key);
                    }
                }

                if (removedKeys.Count > 0)
                {
                    foreach (var kvp in _volumeCacheAliases)
                    {
                        if (removedKeys.Contains(kvp.Value))
                        {
                            _volumeCacheAliases.TryRemove(kvp.Key, out _);
                        }
                    }
                }
            }

            int aliasCount = _volumeCacheAliases.Count;
            if (aliasCount > _maxVolumeAliasEntries)
            {
                int overflow = aliasCount - _maxVolumeAliasEntries;
                int removed = 0;
                foreach (string alias in _volumeCacheAliases.Keys)
                {
                    if (removed >= overflow)
                    {
                        break;
                    }

                    if (_volumeCacheAliases.TryRemove(alias, out _))
                    {
                        removed++;
                    }
                }
            }

            int normalizedCount = _normalizedNameCache.Count;
            if (normalizedCount > _maxNormalizedNameCacheEntries)
            {
                int overflow = normalizedCount - _maxNormalizedNameCacheEntries;
                int removed = 0;
                foreach (string key in _normalizedNameCache.Keys)
                {
                    if (removed >= overflow)
                    {
                        break;
                    }

                    if (_normalizedNameCache.TryRemove(key, out _))
                    {
                        removed++;
                    }
                }
            }
        }
    }
}
