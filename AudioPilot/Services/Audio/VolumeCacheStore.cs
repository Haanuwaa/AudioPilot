using System.Collections.Concurrent;
using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class VolumeCacheStore(
        Logger logger,
        ConcurrentDictionary<string, string> normalizedNameCache,
        Func<string, string> normalizeForMatching,
        TimeSpan volumeCacheTtl,
        int maxVolumeCacheEntries,
        int maxNormalizedNameCacheEntries,
        Func<long>? timestampTicksProvider = null)
    {
        private readonly record struct VolumeCacheEntry(float Volume, long TimestampTicks);

        private readonly Logger _logger = logger;
        private readonly ConcurrentDictionary<string, string> _normalizedNameCache = normalizedNameCache;
        private readonly Func<string, string> _normalizeForMatching = normalizeForMatching;
        private readonly TimeSpan _volumeCacheTtl = volumeCacheTtl;
        private readonly int _maxVolumeCacheEntries = maxVolumeCacheEntries;
        private readonly int _maxNormalizedNameCacheEntries = maxNormalizedNameCacheEntries;
        private readonly Func<long> _timestampTicksProvider = timestampTicksProvider ?? GetMonotonicTimestampTicks;
        private readonly ConcurrentDictionary<string, VolumeCacheEntry> _appVolumeCache = new(StringComparer.OrdinalIgnoreCase);

        public void UpdateCache(string displayName, string? processName, float volume)
        {
            if (!float.IsFinite(volume)) return;
            string processKey = NormalizeProcessIdentity(processName);
            string displayKey = processKey.Length == 0 ? _normalizeForMatching(displayName) : string.Empty;
            if (processKey.Length == 0 && (displayKey.Length == 0 ||
                displayName is "Master Volume" or "Microphone Volume" or "System Sounds")) return;
            string key = processKey.Length != 0 ? "process:" + processKey : "display:" + displayKey;
            var entry = new VolumeCacheEntry(Math.Clamp(volume, 0f, 100f), _timestampTicksProvider());
            _appVolumeCache.AddOrUpdate(key, static (_, newer) => newer,
                static (_, current, newer) => current.TimestampTicks > newer.TimestampTicks ? current : newer, entry);
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.Trace("VolumeControlService", () => $"Cached playback volume | process={LogPrivacy.Process(processName ?? string.Empty)} volume={volume}%");
            TrimCachesIfNeeded();
        }

        internal static string NormalizeProcessIdentity(string? name)
        {
            string value = name?.Trim() ?? string.Empty;
            return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
        }

        public float? TryGetApplicationVolume(string? processName, string? displayName, out long updatedAt)
        {
            string processKey = NormalizeProcessIdentity(processName);
            string key = processKey.Length != 0 ? "process:" + processKey : "display:" + _normalizeForMatching(displayName ?? string.Empty);
            VolumeCacheEntry? entry = TryGetEntry(key);
            updatedAt = entry?.TimestampTicks ?? 0;
            return entry?.Volume;
        }

        private VolumeCacheEntry? TryGetEntry(string key)
        {
            if (_appVolumeCache.TryGetValue(key, out var entry))
            {
                if ((_timestampTicksProvider() - entry.TimestampTicks) < _volumeCacheTtl.Ticks) return entry;
                _appVolumeCache.TryRemove(new KeyValuePair<string, VolumeCacheEntry>(key, entry));
            }
            return null;
        }

        public int CleanupExpiredEntries()
        {
            long nowTicks = _timestampTicksProvider();
            int removed = 0;
            foreach (var entry in _appVolumeCache)
            {
                if (nowTicks - entry.Value.TimestampTicks >= _volumeCacheTtl.Ticks && _appVolumeCache.TryRemove(entry)) removed++;
            }
            TrimCachesIfNeeded();
            return removed;
        }

        public void Clear()
        {
            _appVolumeCache.Clear();
            _normalizedNameCache.Clear();
        }

        internal static long GetMonotonicTimestampTicks() =>
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

                while (oldestEntries.TryDequeue(out var entry, out _))
                {
                    if (_appVolumeCache.TryGetValue(entry.Key, out var current) && current.TimestampTicks == entry.Timestamp)
                        _appVolumeCache.TryRemove(new KeyValuePair<string, VolumeCacheEntry>(entry.Key, current));
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
