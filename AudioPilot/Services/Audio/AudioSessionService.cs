using System.Collections.ObjectModel;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    public class AudioSessionService : IDisposable
    {
        internal sealed class DeferredWindowPidMap
        {
            private readonly Func<IReadOnlyDictionary<IntPtr, uint>> _factory;
            private IReadOnlyDictionary<IntPtr, uint>? _value;

            public DeferredWindowPidMap()
                : this(AudioDeviceHelper.BuildWindowPidMap)
            {
            }

            internal DeferredWindowPidMap(Func<IReadOnlyDictionary<IntPtr, uint>> factory)
            {
                _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            }

            public IReadOnlyDictionary<IntPtr, uint> GetOrCreate()
            {
                return _value ??= _factory();
            }
        }

        private readonly IAudioDeviceEnumerator _deviceEnumerator;
        private readonly Logger _logger;
        private readonly Func<DeviceCacheHelper?> _deviceCacheAccessor;
        private readonly Lock _snapshotMetricsLock = new();
        private readonly TimeSpan _snapshotDiagnosticsWindow = TimeSpan.FromSeconds(AppConstants.Timing.SessionDiagnosticsSummaryWindowSeconds);
        private readonly AudioSessionProcessCacheCoordinator _processCacheCoordinator;
        private readonly AudioSessionRecentSnapshotCache _recentSnapshotCache;
        private readonly AudioSessionSnapshotCollector _snapshotCollector;
        private readonly AudioSessionInfoCollector _infoCollector;
        private DateTime _snapshotMetricsWindowStartUtc = DateTime.UtcNow;
        private int _snapshotMetricsCount;
        private double _snapshotMetricsTotalMs;
        private double _snapshotMetricsMaxMs;
        private int _consecutiveSlowSnapshots;
        private volatile bool _disposed;

        internal Task? CleanupTaskForTests => _processCacheCoordinator.CleanupTaskForTests;
        internal int ProcessCacheCountForTests => _processCacheCoordinator.ProcessCacheCount;

        internal static DeviceCacheHelper? ResolveDeviceCacheOrNull()
        {
            return DeviceCacheHelper.IsInitialized
                ? DeviceCacheHelper.Instance
                : null;
        }

        public AudioSessionService(
            IAudioDeviceEnumerator deviceEnumerator,
            Func<DeviceCacheHelper?>? deviceCacheAccessor = null,
            Logger? logger = null,
            Func<Role>? getPlaybackDetectionRole = null,
            Func<Role>? getRecordingDetectionRole = null)
        {
            _deviceEnumerator = deviceEnumerator;
            _deviceCacheAccessor = deviceCacheAccessor ?? ResolveDeviceCacheOrNull;
            _logger = logger ?? Logger.Instance;
            _processCacheCoordinator = new AudioSessionProcessCacheCoordinator(
                _logger,
                TimeSpan.FromSeconds(AppConstants.Timing.ProcessCacheTtlSeconds));
            _recentSnapshotCache = new AudioSessionRecentSnapshotCache();
            _snapshotCollector = new AudioSessionSnapshotCollector(
                _deviceEnumerator,
                _logger,
                _deviceCacheAccessor,
                _processCacheCoordinator,
                _recentSnapshotCache,
                GetDefaultPlaybackDevice,
                GetDefaultRecordingDevice,
                getPlaybackDetectionRole ?? (static () => Role.Multimedia),
                getRecordingDetectionRole ?? (static () => Role.Console),
                RecordSnapshotMetric);
            _infoCollector = new AudioSessionInfoCollector(
                _deviceEnumerator,
                _logger,
                _processCacheCoordinator,
                _recentSnapshotCache,
                GetDefaultPlaybackDevice,
                GetDefaultRecordingDevice,
                RecordSnapshotMetric);
            _logger.Info("AudioSessionService", "Service initialized");
        }

        internal (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)? GetCachedProcessInfo(uint pid)
        {
            return _processCacheCoordinator.GetCachedProcessInfo(pid);
        }

        internal bool IsCacheEntryExpired(long timestampTicks) => _processCacheCoordinator.IsCacheEntryExpired(timestampTicks);

        internal bool IsCleanupLoopStarted => _processCacheCoordinator.IsCleanupLoopStarted;

        internal Task StartCleanupTaskAsync()
        {
            return _processCacheCoordinator.StartCleanupTaskAsync(() => _disposed);
        }

        internal Task PauseCleanupTaskAsync()
        {
            return _processCacheCoordinator.PauseCleanupTaskAsync();
        }

        internal Task ResumeCleanupTaskAsync()
        {
            return _processCacheCoordinator.ResumeCleanupTaskAsync(() => _disposed);
        }

        private void EnsureCleanupLoopStarted()
        {
            if (!_processCacheCoordinator.IsCleanupLoopStarted && !_disposed)
            {
                _ = StartCleanupTaskAsync();
            }
        }

        internal void AddProcessCacheEntryForTests(uint pid, string processName, string? displayName, string? mainWindowTitle, long timestampTicks)
        {
            _processCacheCoordinator.AddProcessCacheEntryForTests(pid, processName, displayName, mainWindowTitle, timestampTicks);
        }

        internal void TrimProcessCacheForTests()
        {
            _processCacheCoordinator.TrimProcessCacheForTests();
        }

        internal static bool TryProjectSessionProcessMetadataForTests(
            string processName,
            string? displayName,
            string? mainWindowTitle,
            out (string ProcessName, string DisplayName, string? MainWindowTitle) metadata)
        {
            if (AudioSessionProcessCacheCoordinator.TryProjectSessionProcessMetadata(
                processName,
                displayName,
                mainWindowTitle,
                out var projectedMetadata))
            {
                metadata = (
                    projectedMetadata.ProcessName,
                    projectedMetadata.DisplayName,
                    projectedMetadata.MainWindowTitle);
                return true;
            }

            metadata = default;
            return false;
        }

        internal static bool ShouldSkipSelfSessionForTests(uint processId, int currentProcessId)
        {
            return AudioSessionProcessCacheCoordinator.ShouldSkipSelfSession(processId, currentProcessId);
        }

        internal static bool IsSystemSoundsSessionCandidateForTests(uint? processId, string? displayName)
        {
            return IsSystemSoundsSessionCandidate(processId, displayName);
        }

        public async Task<List<AudioSessionInfo>> GetAllAudioSessionsSnapshotAsync(
            Action<AudioSessionInfo> onVolumeChanged,
            bool includeSessionControls = true,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                _logger.Trace("AudioSessionService",
                    "GetAllAudioSessionsSnapshotAsync called while service is disposed");
                return [];
            }

            if (!_processCacheCoordinator.IsCleanupLoopStarted)
            {
                await StartCleanupTaskAsync();
            }

            if (!includeSessionControls)
            {
                var snapshots = await GetAllAudioSessionSnapshotsAsync(recentSnapshotCacheWindowMs, cancellationToken);
                return AudioSessionInfoCollector.MaterializeSnapshotEntries(snapshots, onVolumeChanged);
            }

            if (TryGetRecentNoControlsSnapshot(onVolumeChanged, recentSnapshotCacheWindowMs, out var cachedSessions))
            {
                return cachedSessions;
            }

            return await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => _infoCollector.Collect(onVolumeChanged, includeSessionControls, cancellationToken),
                cancellationToken);
        }

        public List<AudioSessionInfo> GetAllAudioSessionsSnapshot(
            Action<AudioSessionInfo> onVolumeChanged,
            bool includeSessionControls = true,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs)
        {
            if (_disposed)
            {
                _logger.Trace("AudioSessionService",
                    "GetAllAudioSessionsSnapshot called while service is disposed");
                return [];
            }

            EnsureCleanupLoopStarted();

            if (!includeSessionControls)
            {
                var snapshots = GetAllAudioSessionSnapshots(recentSnapshotCacheWindowMs);
                return AudioSessionInfoCollector.MaterializeSnapshotEntries(snapshots, onVolumeChanged);
            }

            if (TryGetRecentNoControlsSnapshot(onVolumeChanged, recentSnapshotCacheWindowMs, out var cachedSessions))
            {
                return cachedSessions;
            }

            return _infoCollector.Collect(onVolumeChanged, includeSessionControls, CancellationToken.None);
        }

        public async Task<ObservableCollection<AudioSessionInfo>> GetAllAudioSessionsAsync(
            Action<AudioSessionInfo> onVolumeChanged,
            CancellationToken cancellationToken = default)
        {
            var sessions = await GetAllAudioSessionsSnapshotAsync(
                onVolumeChanged,
                includeSessionControls: true,
                cancellationToken: cancellationToken);
            return new ObservableCollection<AudioSessionInfo>(sessions);
        }

        public ObservableCollection<AudioSessionInfo> GetAllAudioSessions(
            Action<AudioSessionInfo> onVolumeChanged)
        {
            var sessions = GetAllAudioSessionsSnapshot(onVolumeChanged);
            return new ObservableCollection<AudioSessionInfo>(sessions);
        }

        internal async Task<IReadOnlyList<AudioSessionSnapshot>> GetAllAudioSessionSnapshotsAsync(
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            CancellationToken cancellationToken = default)
        {
            return await GetAllAudioSessionSnapshotsAsync(AudioMixerMode.Output, recentSnapshotCacheWindowMs, cancellationToken);
        }

        internal async Task<IReadOnlyList<AudioSessionSnapshot>> GetAllAudioSessionSnapshotsAsync(
            AudioMixerMode mixerMode,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                _logger.Trace("AudioSessionService",
                    "GetAllAudioSessionSnapshotsAsync called while service is disposed");
                return [];
            }

            if (!_processCacheCoordinator.IsCleanupLoopStarted)
            {
                await StartCleanupTaskAsync();
            }

            if (_recentSnapshotCache.TryGetRecentNoControlsSnapshotData(mixerMode, recentSnapshotCacheWindowMs, out var cachedSessions))
            {
                return cachedSessions;
            }

            return await ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => _snapshotCollector.Collect(mixerMode, cancellationToken),
                cancellationToken);
        }

        internal IReadOnlyList<AudioSessionSnapshot> GetAllAudioSessionSnapshots(
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs)
        {
            return GetAllAudioSessionSnapshots(AudioMixerMode.Output, recentSnapshotCacheWindowMs);
        }

        internal IReadOnlyList<AudioSessionSnapshot> GetAllAudioSessionSnapshots(
            AudioMixerMode mixerMode,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs)
        {
            if (_disposed)
            {
                _logger.Trace("AudioSessionService",
                    "GetAllAudioSessionSnapshots called while service is disposed");
                return [];
            }

            EnsureCleanupLoopStarted();

            if (_recentSnapshotCache.TryGetRecentNoControlsSnapshotData(mixerMode, recentSnapshotCacheWindowMs, out var cachedSessions))
            {
                return cachedSessions;
            }

            return _snapshotCollector.Collect(mixerMode, CancellationToken.None);
        }

        internal void InvalidateRecentMixerSnapshotState()
        {
            _recentSnapshotCache.InvalidateRecentMixerSnapshotState();
        }

        internal bool RecordEndpointVolumeNotification(AudioMixerMode mixerMode, string endpointId, float volumePercent, bool isMuted)
        {
            return _recentSnapshotCache.RecordEndpointVolumeNotification(mixerMode, endpointId, volumePercent, isMuted);
        }

        internal void SeedRecentSnapshotForTests(
            AudioMixerMode mixerMode,
            AudioSessionSnapshot[]? snapshot,
            DateTime capturedUtc)
        {
            _recentSnapshotCache.SeedRecentSnapshotForTests(mixerMode, snapshot, capturedUtc);
        }

        internal void SeedEndpointSnapshotForTests(
            AudioMixerMode mixerMode,
            AudioSessionRecentSnapshotCache.EndpointSnapshotEntry? snapshot)
        {
            _recentSnapshotCache.SeedEndpointSnapshotForTests(mixerMode, snapshot);
        }

        internal void SetOutputScanStateForTests(
            string playbackFingerprint,
            HashSet<string>? sessionBearingPlaybackDeviceIds,
            int selectivePlaybackScanStreak)
        {
            _recentSnapshotCache.SetOutputScanStateForTests(
                playbackFingerprint,
                sessionBearingPlaybackDeviceIds,
                selectivePlaybackScanStreak);
        }

        internal (AudioSessionSnapshot[]? Snapshot, DateTime CapturedUtc) GetRecentSnapshotDataForTests(AudioMixerMode mixerMode)
        {
            return _recentSnapshotCache.GetRecentSnapshotData(mixerMode);
        }

        internal AudioSessionRecentSnapshotCache.EndpointSnapshotEntry? GetEndpointSnapshotForTests(AudioMixerMode mixerMode)
        {
            return _recentSnapshotCache.GetEndpointSnapshot(mixerMode);
        }

        internal AudioSessionRecentSnapshotCache.OutputSnapshotScanState GetOutputScanStateForTests()
        {
            return _recentSnapshotCache.GetOutputScanState();
        }

        internal IReadOnlyList<AudioSessionSnapshot> GetAllAudioSessionSnapshotsForTests(bool includeSessionControls = true)
        {
            return GetAllAudioSessionSnapshots(AudioMixerMode.Output, includeSessionControls ? 0 : AppConstants.Timing.SessionSnapshotFastPathCacheMs);
        }

        private MMDevice? GetDefaultPlaybackDevice(string reason)
        {
            if (_deviceEnumerator is AudioDeviceService audioDeviceService)
            {
                return audioDeviceService.GetDefaultPlaybackDevice(reason);
            }

            return _deviceEnumerator.GetDefaultPlaybackDevice();
        }

        private MMDevice? GetDefaultRecordingDevice(string reason)
        {
            if (_deviceEnumerator is AudioDeviceService audioDeviceService)
            {
                return audioDeviceService.GetDefaultRecordingDevice(reason);
            }

            return _deviceEnumerator.GetDefaultRecordingDevice();
        }

        private bool TryGetRecentNoControlsSnapshot(
            Action<AudioSessionInfo> onVolumeChanged,
            int recentSnapshotCacheWindowMs,
            out List<AudioSessionInfo> sessions)
        {
            sessions = [];

            if (!_recentSnapshotCache.TryGetRecentNoControlsSnapshotData(
                AudioMixerMode.Output,
                recentSnapshotCacheWindowMs,
                out var snapshotEntries))
            {
                return false;
            }

            sessions = AudioSessionInfoCollector.MaterializeSnapshotEntries(snapshotEntries, onVolumeChanged);
            return true;
        }

        internal static bool ShouldUseSelectivePlaybackDeviceScan(
            bool includeSessionControls,
            string currentPlaybackFingerprint,
            string previousPlaybackFingerprint,
            int candidateDeviceCount,
            int selectiveScanStreak,
            int selectiveScanLimit)
        {
            if (includeSessionControls)
            {
                return false;
            }

            if (candidateDeviceCount <= 0 || selectiveScanLimit <= 0)
            {
                return false;
            }

            if (selectiveScanStreak >= selectiveScanLimit)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(currentPlaybackFingerprint) &&
                string.Equals(currentPlaybackFingerprint, previousPlaybackFingerprint, StringComparison.Ordinal);
        }

        internal static bool ShouldUseRecentSnapshotCache(
            DateTime nowUtc,
            DateTime lastSnapshotUtc,
            int cacheWindowMs,
            bool includeSessionControls)
        {
            if (includeSessionControls)
            {
                return false;
            }

            if (cacheWindowMs <= 0)
            {
                return false;
            }

            if (lastSnapshotUtc == DateTime.MinValue)
            {
                return false;
            }

            return (nowUtc - lastSnapshotUtc).TotalMilliseconds <= cacheWindowMs;
        }

        internal static bool IsSystemSoundsSessionCandidate(uint? processId, string? displayName)
        {
            return processId == 0
                || string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase)
                || (displayName?.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase) == true);
        }

        private void RecordSnapshotMetric(double elapsedMs, int sessionCount)
        {
            lock (_snapshotMetricsLock)
            {
                _snapshotMetricsCount++;
                _snapshotMetricsTotalMs += elapsedMs;
                _snapshotMetricsMaxMs = Math.Max(_snapshotMetricsMaxMs, elapsedMs);

                if (elapsedMs >= AppConstants.Timing.SessionSlowSnapshotWarningMs)
                {
                    _consecutiveSlowSnapshots++;
                    if (_consecutiveSlowSnapshots >= AppConstants.Timing.SessionSlowSnapshotConsecutiveCount &&
                        _logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Warning(
                            "AudioSessionService",
                            $"{AppConstants.Audio.LogEvents.Diagnostics.SessionSnapshotSlow} | elapsedMs={elapsedMs:F1} consecutive={_consecutiveSlowSnapshots} sessionCount={sessionCount}");
                    }
                }
                else
                {
                    _consecutiveSlowSnapshots = 0;
                }

                var now = DateTime.UtcNow;
                var windowElapsed = now - _snapshotMetricsWindowStartUtc;
                if (windowElapsed < _snapshotDiagnosticsWindow)
                {
                    return;
                }

                if (_snapshotMetricsCount > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    double avg = _snapshotMetricsTotalMs / _snapshotMetricsCount;
                    _logger.Debug(
                        "AudioSessionService",
                        $"{AppConstants.Audio.LogEvents.Diagnostics.SessionSnapshotDiagnostics} | count={_snapshotMetricsCount} avgMs={avg:F1} maxMs={_snapshotMetricsMaxMs:F1} windowSeconds={windowElapsed.TotalSeconds:F0}");
                }

                _snapshotMetricsWindowStartUtc = now;
                _snapshotMetricsCount = 0;
                _snapshotMetricsTotalMs = 0;
                _snapshotMetricsMaxMs = 0;
            }
        }

        internal static bool IsSharedMixerSnapshot(string displayName)
        {
            return displayName.StartsWith("Master Volume", StringComparison.OrdinalIgnoreCase)
                || displayName.StartsWith("Microphone Volume", StringComparison.OrdinalIgnoreCase)
                || string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase);
        }

        internal void ClearCaches()
        {
            _processCacheCoordinator.Clear();
            _recentSnapshotCache.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("AudioSessionService", "Disposing audio session service");
            }

            _processCacheCoordinator.Dispose();
            ClearCaches();
            GC.SuppressFinalize(this);
        }
    }
}
