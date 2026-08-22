namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayStateStore
    {
        private readonly MediaOverlayCommandSequenceStore _commandSequence = new();
        private readonly MediaOverlayTrackStreakStore _trackStreaks = new();
        private readonly MediaOverlaySourceMemoryStore _sourceMemory = new();
        private readonly MediaOverlayTelemetryStore _telemetry = new();
        private int _trimStateCallCount;

        internal int StickySourceCountForTests
        {
            get
            {
                return _sourceMemory.StickySourceCount;
            }
        }

        internal int TrustedSourceCountForTests
        {
            get
            {
                return _sourceMemory.TrustedSourceCount;
            }
        }

        internal int TrimStateCallCountForTests => _trimStateCallCount;

        public long GetNextCommandSequence()
        {
            return _commandSequence.GetNextCommandSequence();
        }

        public long GetNextReadOnlyCommandSequence()
        {
            return _commandSequence.GetNextReadOnlyCommandSequence();
        }

        public bool IsCommandSequenceCurrent(long commandSequence)
        {
            return _commandSequence.IsCommandSequenceCurrent(commandSequence);
        }

        public bool UpdateTrackStreak(string key, bool unchanged, bool stagnantPosition, out int unchangedStreak, out int stagnantPositionStreak)
        {
            return _trackStreaks.Update(key, unchanged, stagnantPosition, out unchangedStreak, out stagnantPositionStreak);
        }

        public void ResetTrackStreak(string key)
        {
            _trackStreaks.Reset(key);
        }

        public string? TryGetStickySource(string commandGroupKey, int stickySourceTtlSeconds)
        {
            return _sourceMemory.TryGetStickySource(commandGroupKey, stickySourceTtlSeconds);
        }

        public void UpdateStickySource(string commandGroupKey, string sourceAppUserModelId)
        {
            _sourceMemory.UpdateStickySource(commandGroupKey, sourceAppUserModelId);
        }

        public void ClearStickySource(string commandGroupKey)
        {
            _sourceMemory.ClearStickySource(commandGroupKey);
        }

        public string? TryGetRecoveredSource(string commandGroupKey, int recoveredSourceTtlSeconds)
        {
            return _sourceMemory.TryGetRecoveredSource(commandGroupKey, recoveredSourceTtlSeconds);
        }

        public void UpdateRecoveredSource(string commandGroupKey, string sourceAppUserModelId)
        {
            _sourceMemory.UpdateRecoveredSource(commandGroupKey, sourceAppUserModelId);
        }

        public void ClearRecoveredSource(string commandGroupKey)
        {
            _sourceMemory.ClearRecoveredSource(commandGroupKey);
        }

        public void MarkRecentlySignaledSource(string sourceAppUserModelId)
        {
            _sourceMemory.MarkRecentlySignaledSource(sourceAppUserModelId);
        }

        public bool HasRecentSignal(string sourceAppUserModelId, int recentSignalTtlMs)
        {
            return _sourceMemory.HasRecentSignal(sourceAppUserModelId, recentSignalTtlMs);
        }

        public void MarkTrustedTrackNavigationSource(string sourceAppUserModelId)
        {
            _sourceMemory.MarkTrustedTrackNavigationSource(sourceAppUserModelId);
        }

        public bool HasTrustedTrackNavigationSource(string sourceAppUserModelId, int trustedSourceTtlMs)
        {
            return _sourceMemory.HasTrustedTrackNavigationSource(sourceAppUserModelId, trustedSourceTtlMs);
        }

        public bool IsInFirstCommandGraceWindow(string commandGroupKey, string currentSourceAppUserModelId, int firstCommandGraceWindowMs)
        {
            return _sourceMemory.IsInFirstCommandGraceWindow(commandGroupKey, currentSourceAppUserModelId, firstCommandGraceWindowMs);
        }

        public void TrimStateIfNeeded()
        {
            _trimStateCallCount++;
            _trackStreaks.TrimIfNeeded();
            _sourceMemory.TrimIfNeeded();
        }

        public bool TryRecordOverlayTelemetry(
            MediaOverlayTelemetryEvent telemetryEvent,
            MediaOverlayTelemetryOutcomeClass outcomeClass,
            TrackNavigationChangeKind? trackChangeKind,
            int flushEveryEvents,
            int flushIntervalSeconds,
            out MediaOverlayTelemetrySnapshot snapshot)
        {
            return _telemetry.TryRecord(telemetryEvent, outcomeClass, trackChangeKind, flushEveryEvents, flushIntervalSeconds, out snapshot);
        }
    }
}
