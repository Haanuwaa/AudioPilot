using AudioPilot.Constants;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlaySourceMemoryStore
    {
        private const int MaxStickySourceEntries = 64;
        private const int MaxRecoveredSourceEntries = 64;
        private const int MaxContextEntries = 64;
        private const int MaxRecentSignalEntries = 64;
        private const int MaxTrustedSourceEntries = 64;

        private readonly record struct StickySourceState(
            string SourceAppUserModelId,
            DateTimeOffset LastUpdatedUtc);

        private readonly Lock _stickySourceLock = new();
        private readonly Dictionary<string, StickySourceState> _stickySourceByCommandGroup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _recoveredSourceLock = new();
        private readonly Dictionary<string, StickySourceState> _recoveredSourceByCommandGroup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _recentSignalLock = new();
        private readonly Dictionary<string, DateTimeOffset> _recentSignalAtBySource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _trustedSourceLock = new();
        private readonly Dictionary<string, DateTimeOffset> _trustedTrackNavigationAtBySource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _contextShiftLock = new();
        private readonly Dictionary<string, string> _lastBaselineSourceByCommandGroup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _sourceShiftAtByCommandGroup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _contextTouchedAtByCommandGroup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTimeOffset> _utcNow;

        public MediaOverlaySourceMemoryStore()
            : this(static () => DateTimeOffset.UtcNow)
        {
        }

        internal MediaOverlaySourceMemoryStore(Func<DateTimeOffset> utcNow)
        {
            _utcNow = utcNow;
        }

        public int StickySourceCount
        {
            get
            {
                lock (_stickySourceLock)
                {
                    return _stickySourceByCommandGroup.Count;
                }
            }
        }

        public int TrustedSourceCount
        {
            get
            {
                lock (_trustedSourceLock)
                {
                    return _trustedTrackNavigationAtBySource.Count;
                }
            }
        }

        public string? TryGetStickySource(string commandGroupKey, int stickySourceTtlSeconds)
        {
            lock (_stickySourceLock)
            {
                if (!_stickySourceByCommandGroup.TryGetValue(commandGroupKey, out StickySourceState state))
                {
                    return null;
                }

                if ((_utcNow() - state.LastUpdatedUtc).TotalSeconds > stickySourceTtlSeconds)
                {
                    _stickySourceByCommandGroup.Remove(commandGroupKey);
                    return null;
                }

                return state.SourceAppUserModelId;
            }
        }

        public void UpdateStickySource(string commandGroupKey, string sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(commandGroupKey) || string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return;
            }

            lock (_stickySourceLock)
            {
                _stickySourceByCommandGroup[commandGroupKey] = new StickySourceState(sourceAppUserModelId, _utcNow());
            }
        }

        public void ClearStickySource(string commandGroupKey)
        {
            lock (_stickySourceLock)
            {
                _stickySourceByCommandGroup.Remove(commandGroupKey);
            }
        }

        public string? TryGetRecoveredSource(string commandGroupKey, int recoveredSourceTtlSeconds)
        {
            lock (_recoveredSourceLock)
            {
                if (!_recoveredSourceByCommandGroup.TryGetValue(commandGroupKey, out StickySourceState state))
                {
                    return null;
                }

                if ((_utcNow() - state.LastUpdatedUtc).TotalSeconds > recoveredSourceTtlSeconds)
                {
                    _recoveredSourceByCommandGroup.Remove(commandGroupKey);
                    return null;
                }

                return state.SourceAppUserModelId;
            }
        }

        public void UpdateRecoveredSource(string commandGroupKey, string sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(commandGroupKey) || string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return;
            }

            lock (_recoveredSourceLock)
            {
                _recoveredSourceByCommandGroup[commandGroupKey] = new StickySourceState(sourceAppUserModelId, _utcNow());
            }
        }

        public void ClearRecoveredSource(string commandGroupKey)
        {
            lock (_recoveredSourceLock)
            {
                _recoveredSourceByCommandGroup.Remove(commandGroupKey);
            }
        }

        public void MarkRecentlySignaledSource(string sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return;
            }

            lock (_recentSignalLock)
            {
                _recentSignalAtBySource[sourceAppUserModelId] = _utcNow();
            }
        }

        public bool HasRecentSignal(string sourceAppUserModelId, int recentSignalTtlMs)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId) || recentSignalTtlMs <= 0)
            {
                return false;
            }

            lock (_recentSignalLock)
            {
                if (!_recentSignalAtBySource.TryGetValue(sourceAppUserModelId, out DateTimeOffset signaledAtUtc))
                {
                    return false;
                }

                if ((_utcNow() - signaledAtUtc).TotalMilliseconds > recentSignalTtlMs)
                {
                    _recentSignalAtBySource.Remove(sourceAppUserModelId);
                    return false;
                }

                return true;
            }
        }

        public void MarkTrustedTrackNavigationSource(string sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return;
            }

            lock (_trustedSourceLock)
            {
                _trustedTrackNavigationAtBySource[sourceAppUserModelId] = _utcNow();
            }
        }

        public bool HasTrustedTrackNavigationSource(string sourceAppUserModelId, int trustedSourceTtlMs)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId) || trustedSourceTtlMs <= 0)
            {
                return false;
            }

            lock (_trustedSourceLock)
            {
                if (!_trustedTrackNavigationAtBySource.TryGetValue(sourceAppUserModelId, out DateTimeOffset trustedAtUtc))
                {
                    return false;
                }

                if ((_utcNow() - trustedAtUtc).TotalMilliseconds > trustedSourceTtlMs)
                {
                    _trustedTrackNavigationAtBySource.Remove(sourceAppUserModelId);
                    return false;
                }

                return true;
            }
        }

        public bool IsInFirstCommandGraceWindow(string commandGroupKey, string currentSourceAppUserModelId, int firstCommandGraceWindowMs)
        {
            if (string.IsNullOrWhiteSpace(commandGroupKey) || string.IsNullOrWhiteSpace(currentSourceAppUserModelId))
            {
                return false;
            }

            DateTimeOffset now = _utcNow();
            bool sourceShifted;
            lock (_contextShiftLock)
            {
                if (!_lastBaselineSourceByCommandGroup.TryGetValue(commandGroupKey, out string? previousSource))
                {
                    _lastBaselineSourceByCommandGroup[commandGroupKey] = currentSourceAppUserModelId;
                    _sourceShiftAtByCommandGroup[commandGroupKey] = now;
                    _contextTouchedAtByCommandGroup[commandGroupKey] = now;
                    sourceShifted = true;
                }
                else if (!string.Equals(previousSource, currentSourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
                {
                    _lastBaselineSourceByCommandGroup[commandGroupKey] = currentSourceAppUserModelId;
                    _sourceShiftAtByCommandGroup[commandGroupKey] = now;
                    _contextTouchedAtByCommandGroup[commandGroupKey] = now;
                    sourceShifted = true;
                }
                else if (!_sourceShiftAtByCommandGroup.TryGetValue(commandGroupKey, out DateTimeOffset shiftedAt))
                {
                    _sourceShiftAtByCommandGroup[commandGroupKey] = now;
                    _contextTouchedAtByCommandGroup[commandGroupKey] = now;
                    sourceShifted = true;
                }
                else
                {
                    _contextTouchedAtByCommandGroup[commandGroupKey] = now;
                    return (now - shiftedAt).TotalMilliseconds <= firstCommandGraceWindowMs;
                }
            }

            if (sourceShifted)
            {
                ClearStickySource(commandGroupKey);
            }

            return true;
        }

        public void TrimIfNeeded()
        {
            lock (_contextShiftLock)
            {
                int maxCount = Math.Max(
                    _lastBaselineSourceByCommandGroup.Count,
                    Math.Max(_sourceShiftAtByCommandGroup.Count, _contextTouchedAtByCommandGroup.Count));
                if (maxCount > MaxContextEntries)
                {
                    var entriesByAge = new List<KeyValuePair<string, DateTimeOffset>>(_contextTouchedAtByCommandGroup);
                    entriesByAge.Sort(static (left, right) => left.Value.CompareTo(right.Value));

                    int overflow = maxCount - MaxContextEntries;
                    for (int index = 0; index < overflow && index < entriesByAge.Count; index++)
                    {
                        string key = entriesByAge[index].Key;
                        _lastBaselineSourceByCommandGroup.Remove(key);
                        _sourceShiftAtByCommandGroup.Remove(key);
                        _contextTouchedAtByCommandGroup.Remove(key);
                    }
                }
            }

            lock (_stickySourceLock)
            {
                if (_stickySourceByCommandGroup.Count > 0)
                {
                    DateTimeOffset cutoffUtc = _utcNow().AddSeconds(-AppConstants.MediaOverlay.StickySourceTtlSeconds);
                    List<string>? expiredKeys = null;
                    foreach (var kvp in _stickySourceByCommandGroup)
                    {
                        if (kvp.Value.LastUpdatedUtc < cutoffUtc)
                        {
                            expiredKeys ??= [];
                            expiredKeys.Add(kvp.Key);
                        }
                    }

                    if (expiredKeys != null)
                    {
                        for (int index = 0; index < expiredKeys.Count; index++)
                        {
                            _stickySourceByCommandGroup.Remove(expiredKeys[index]);
                        }
                    }

                    if (_stickySourceByCommandGroup.Count > MaxStickySourceEntries)
                    {
                        var entriesByAge = new List<KeyValuePair<string, StickySourceState>>(_stickySourceByCommandGroup);
                        entriesByAge.Sort(static (left, right) => left.Value.LastUpdatedUtc.CompareTo(right.Value.LastUpdatedUtc));

                        int overflow = _stickySourceByCommandGroup.Count - MaxStickySourceEntries;
                        for (int index = 0; index < overflow; index++)
                        {
                            _stickySourceByCommandGroup.Remove(entriesByAge[index].Key);
                        }
                    }
                }
            }

            lock (_recoveredSourceLock)
            {
                if (_recoveredSourceByCommandGroup.Count > 0)
                {
                    DateTimeOffset cutoffUtc = _utcNow().AddSeconds(-AppConstants.MediaOverlay.StickySourceTtlSeconds);
                    List<string>? expiredKeys = null;
                    foreach (var kvp in _recoveredSourceByCommandGroup)
                    {
                        if (kvp.Value.LastUpdatedUtc < cutoffUtc)
                        {
                            expiredKeys ??= [];
                            expiredKeys.Add(kvp.Key);
                        }
                    }

                    if (expiredKeys != null)
                    {
                        for (int index = 0; index < expiredKeys.Count; index++)
                        {
                            _recoveredSourceByCommandGroup.Remove(expiredKeys[index]);
                        }
                    }

                    if (_recoveredSourceByCommandGroup.Count > MaxRecoveredSourceEntries)
                    {
                        var entriesByAge = new List<KeyValuePair<string, StickySourceState>>(_recoveredSourceByCommandGroup);
                        entriesByAge.Sort(static (left, right) => left.Value.LastUpdatedUtc.CompareTo(right.Value.LastUpdatedUtc));

                        int overflow = _recoveredSourceByCommandGroup.Count - MaxRecoveredSourceEntries;
                        for (int index = 0; index < overflow; index++)
                        {
                            _recoveredSourceByCommandGroup.Remove(entriesByAge[index].Key);
                        }
                    }
                }
            }

            lock (_recentSignalLock)
            {
                if (_recentSignalAtBySource.Count > 0)
                {
                    DateTimeOffset cutoffUtc = _utcNow().AddMilliseconds(-AppConstants.MediaOverlay.RecentSignalTtlMs);
                    List<string>? expiredKeys = null;
                    foreach (var kvp in _recentSignalAtBySource)
                    {
                        if (kvp.Value < cutoffUtc)
                        {
                            expiredKeys ??= [];
                            expiredKeys.Add(kvp.Key);
                        }
                    }

                    if (expiredKeys != null)
                    {
                        for (int index = 0; index < expiredKeys.Count; index++)
                        {
                            _recentSignalAtBySource.Remove(expiredKeys[index]);
                        }
                    }

                    if (_recentSignalAtBySource.Count > MaxRecentSignalEntries)
                    {
                        var entriesByAge = new List<KeyValuePair<string, DateTimeOffset>>(_recentSignalAtBySource);
                        entriesByAge.Sort(static (left, right) => left.Value.CompareTo(right.Value));

                        int overflow = _recentSignalAtBySource.Count - MaxRecentSignalEntries;
                        for (int index = 0; index < overflow; index++)
                        {
                            _recentSignalAtBySource.Remove(entriesByAge[index].Key);
                        }
                    }
                }
            }

            lock (_trustedSourceLock)
            {
                if (_trustedTrackNavigationAtBySource.Count == 0)
                {
                    return;
                }

                DateTimeOffset cutoffUtc = _utcNow().AddMilliseconds(-AppConstants.MediaOverlay.RecentTrustedSourceTtlMs);
                List<string>? expiredKeys = null;
                foreach (var kvp in _trustedTrackNavigationAtBySource)
                {
                    if (kvp.Value < cutoffUtc)
                    {
                        expiredKeys ??= [];
                        expiredKeys.Add(kvp.Key);
                    }
                }

                if (expiredKeys != null)
                {
                    for (int index = 0; index < expiredKeys.Count; index++)
                    {
                        _trustedTrackNavigationAtBySource.Remove(expiredKeys[index]);
                    }
                }

                if (_trustedTrackNavigationAtBySource.Count <= MaxTrustedSourceEntries)
                {
                    return;
                }

                var entriesByAge = new List<KeyValuePair<string, DateTimeOffset>>(_trustedTrackNavigationAtBySource);
                entriesByAge.Sort(static (left, right) => left.Value.CompareTo(right.Value));

                int overflow = _trustedTrackNavigationAtBySource.Count - MaxTrustedSourceEntries;
                for (int index = 0; index < overflow; index++)
                {
                    _trustedTrackNavigationAtBySource.Remove(entriesByAge[index].Key);
                }
            }
        }
    }
}
