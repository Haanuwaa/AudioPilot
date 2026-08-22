namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayTrackStreakStore
    {
        private const int MaxTrackedEntries = 256;

        private readonly Lock _lock = new();
        private readonly Dictionary<string, int> _unchangedStreakByCommandSource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _stagnantPositionStreakByCommandSource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _lastTouchedAtByCommandSource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTimeOffset> _utcNow;

        public MediaOverlayTrackStreakStore()
            : this(static () => DateTimeOffset.UtcNow)
        {
        }

        internal MediaOverlayTrackStreakStore(Func<DateTimeOffset> utcNow)
        {
            _utcNow = utcNow;
        }

        public bool Update(string key, bool unchanged, bool stagnantPosition, out int unchangedStreak, out int stagnantPositionStreak)
        {
            unchangedStreak = 0;
            stagnantPositionStreak = 0;

            lock (_lock)
            {
                if (!unchanged)
                {
                    _unchangedStreakByCommandSource[key] = 0;
                    _stagnantPositionStreakByCommandSource[key] = 0;
                    _lastTouchedAtByCommandSource[key] = _utcNow();
                    return false;
                }

                int nextUnchanged = (_unchangedStreakByCommandSource.TryGetValue(key, out int currentUnchanged) ? currentUnchanged : 0) + 1;
                _unchangedStreakByCommandSource[key] = nextUnchanged;

                int nextStagnant = stagnantPosition
                    ? (_stagnantPositionStreakByCommandSource.TryGetValue(key, out int currentStagnant) ? currentStagnant : 0) + 1
                    : 0;
                _stagnantPositionStreakByCommandSource[key] = nextStagnant;
                _lastTouchedAtByCommandSource[key] = _utcNow();

                unchangedStreak = nextUnchanged;
                stagnantPositionStreak = nextStagnant;
                return nextUnchanged >= 2 && nextStagnant >= 2;
            }
        }

        public void Reset(string key)
        {
            lock (_lock)
            {
                _unchangedStreakByCommandSource[key] = 0;
                _stagnantPositionStreakByCommandSource[key] = 0;
                _lastTouchedAtByCommandSource[key] = _utcNow();
            }
        }

        public void TrimIfNeeded()
        {
            lock (_lock)
            {
                int maxCount = Math.Max(
                    _unchangedStreakByCommandSource.Count,
                    Math.Max(_stagnantPositionStreakByCommandSource.Count, _lastTouchedAtByCommandSource.Count));
                if (maxCount <= MaxTrackedEntries)
                {
                    return;
                }

                int overflow = maxCount - MaxTrackedEntries;
                var oldestEntries = _lastTouchedAtByCommandSource
                    .OrderBy(static kvp => kvp.Value)
                    .Take(overflow)
                    .Select(static kvp => kvp.Key)
                    .ToList();

                foreach (string key in oldestEntries)
                {
                    _unchangedStreakByCommandSource.Remove(key);
                    _stagnantPositionStreakByCommandSource.Remove(key);
                    _lastTouchedAtByCommandSource.Remove(key);
                }
            }
        }
    }
}
