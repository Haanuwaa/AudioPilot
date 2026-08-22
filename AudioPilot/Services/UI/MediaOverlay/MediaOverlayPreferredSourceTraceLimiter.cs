using AudioPilot.Constants;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal readonly record struct PreferredSourceSingleCandidateLogDecision(bool ShouldEmit, int SuppressedRepeats);
    internal readonly record struct PreferredSourceMissingSourceLogDecision(bool ShouldEmit, int SuppressedRepeats);

    internal sealed class MediaOverlayPreferredSourceTraceLimiter
    {
        private readonly Lock _preferredSourceTraceLock = new();
        private readonly Dictionary<string, PreferredSourceSingleCandidateTraceState> _lastSingleCandidateTraceBySource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PreferredSourceMissingSourceTraceState> _lastMissingSourceTraceBySource = new(StringComparer.OrdinalIgnoreCase);

        private readonly record struct PreferredSourceSingleCandidateTraceState(
            string Signature,
            DateTimeOffset LastTraceUtc,
            int SuppressedRepeats);

        private readonly record struct PreferredSourceMissingSourceTraceState(
            DateTimeOffset LastTraceUtc,
            int SuppressedRepeats);

        internal PreferredSourceSingleCandidateLogDecision Evaluate(
            string preferredSourceAppUserModelId,
            string signature,
            DateTimeOffset now,
            TimeSpan throttleWindow)
        {
            lock (_preferredSourceTraceLock)
            {
                Trim(now);

                if (!_lastSingleCandidateTraceBySource.TryGetValue(preferredSourceAppUserModelId, out PreferredSourceSingleCandidateTraceState state)
                    || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
                {
                    _lastSingleCandidateTraceBySource[preferredSourceAppUserModelId] = new PreferredSourceSingleCandidateTraceState(signature, now, 0);
                    Trim(now);
                    return new PreferredSourceSingleCandidateLogDecision(true, 0);
                }

                if ((now - state.LastTraceUtc) < throttleWindow)
                {
                    _lastSingleCandidateTraceBySource[preferredSourceAppUserModelId] = state with { SuppressedRepeats = state.SuppressedRepeats + 1 };
                    return new PreferredSourceSingleCandidateLogDecision(false, state.SuppressedRepeats + 1);
                }

                _lastSingleCandidateTraceBySource[preferredSourceAppUserModelId] = new PreferredSourceSingleCandidateTraceState(signature, now, 0);
                return new PreferredSourceSingleCandidateLogDecision(true, state.SuppressedRepeats);
            }
        }

        internal PreferredSourceMissingSourceLogDecision EvaluateMissingSource(
            string preferredSourceAppUserModelId,
            DateTimeOffset now,
            TimeSpan throttleWindow)
        {
            lock (_preferredSourceTraceLock)
            {
                Trim(now);

                if (!_lastMissingSourceTraceBySource.TryGetValue(preferredSourceAppUserModelId, out PreferredSourceMissingSourceTraceState state))
                {
                    _lastMissingSourceTraceBySource[preferredSourceAppUserModelId] = new PreferredSourceMissingSourceTraceState(now, 0);
                    return new PreferredSourceMissingSourceLogDecision(true, 0);
                }

                if ((now - state.LastTraceUtc) < throttleWindow)
                {
                    _lastMissingSourceTraceBySource[preferredSourceAppUserModelId] = state with { SuppressedRepeats = state.SuppressedRepeats + 1 };
                    return new PreferredSourceMissingSourceLogDecision(false, state.SuppressedRepeats + 1);
                }

                _lastMissingSourceTraceBySource[preferredSourceAppUserModelId] = new PreferredSourceMissingSourceTraceState(now, 0);
                return new PreferredSourceMissingSourceLogDecision(true, state.SuppressedRepeats);
            }
        }

        private void Trim(DateTimeOffset now)
        {
            if (_lastSingleCandidateTraceBySource.Count == 0)
            {
                TrimMissing(now);
                return;
            }

            DateTimeOffset cutoffUtc = now.AddSeconds(-AppConstants.Timing.MediaOverlayPreferredSourceSingleCandidateTraceRetentionSeconds);
            List<string>? expiredKeys = null;
            foreach ((string key, PreferredSourceSingleCandidateTraceState value) in _lastSingleCandidateTraceBySource)
            {
                if (value.LastTraceUtc < cutoffUtc)
                {
                    expiredKeys ??= [];
                    expiredKeys.Add(key);
                }
            }

            if (expiredKeys != null)
            {
                foreach (string expiredKey in expiredKeys)
                {
                    _lastSingleCandidateTraceBySource.Remove(expiredKey);
                }
            }

            while (_lastSingleCandidateTraceBySource.Count > AppConstants.Limits.MaxMediaOverlayPreferredSourceSingleCandidateTraceEntries)
            {
                string? oldestKey = null;
                DateTimeOffset oldestTraceUtc = DateTimeOffset.MaxValue;
                foreach ((string key, PreferredSourceSingleCandidateTraceState value) in _lastSingleCandidateTraceBySource)
                {
                    if (value.LastTraceUtc >= oldestTraceUtc)
                    {
                        continue;
                    }

                    oldestKey = key;
                    oldestTraceUtc = value.LastTraceUtc;
                }

                if (oldestKey == null)
                {
                    break;
                }

                _lastSingleCandidateTraceBySource.Remove(oldestKey);
            }

            TrimMissing(now);
        }

        private void TrimMissing(DateTimeOffset now)
        {
            if (_lastMissingSourceTraceBySource.Count == 0)
            {
                return;
            }

            DateTimeOffset cutoffUtc = now.AddSeconds(-AppConstants.Timing.MediaOverlayPreferredSourceSingleCandidateTraceRetentionSeconds);
            List<string>? expiredKeys = null;
            foreach ((string key, PreferredSourceMissingSourceTraceState value) in _lastMissingSourceTraceBySource)
            {
                if (value.LastTraceUtc < cutoffUtc)
                {
                    expiredKeys ??= [];
                    expiredKeys.Add(key);
                }
            }

            if (expiredKeys != null)
            {
                foreach (string expiredKey in expiredKeys)
                {
                    _lastMissingSourceTraceBySource.Remove(expiredKey);
                }
            }

            while (_lastMissingSourceTraceBySource.Count > AppConstants.Limits.MaxMediaOverlayPreferredSourceSingleCandidateTraceEntries)
            {
                string? oldestKey = null;
                DateTimeOffset oldestTraceUtc = DateTimeOffset.MaxValue;
                foreach ((string key, PreferredSourceMissingSourceTraceState value) in _lastMissingSourceTraceBySource)
                {
                    if (value.LastTraceUtc >= oldestTraceUtc)
                    {
                        continue;
                    }

                    oldestKey = key;
                    oldestTraceUtc = value.LastTraceUtc;
                }

                if (oldestKey == null)
                {
                    break;
                }

                _lastMissingSourceTraceBySource.Remove(oldestKey);
            }
        }
    }
}
