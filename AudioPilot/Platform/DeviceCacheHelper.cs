using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Platform
{
    internal readonly record struct DeviceCacheSnapshotData(string Id, string Name, DeviceState State);

    internal readonly record struct DeviceCacheRefreshCapture(
        IReadOnlyList<DeviceCacheSnapshotData?> Playback,
        IReadOnlyList<DeviceCacheSnapshotData?> Recording);

    internal sealed class DeviceCacheHelperRuntime
    {
        public required Func<Task<DeviceCacheRefreshCapture>> CaptureSnapshotsAsync { get; init; }
        public required Func<string, MMDevice?> MaterializeDeviceById { get; init; }
        public required Func<NRole, string, MMDevice?> GetFallbackPlaybackDevice { get; init; }
        public required Func<NRole, string, MMDevice?> GetFallbackRecordingDevice { get; init; }
        public required Func<Logger, MMDevice, string, bool?> ProbeMuteState { get; init; }
        public required Func<Func<Task>, Task> RunRefreshWorkAsync { get; init; }
    }

    public class DeviceCacheHelper : IDisposable
    {
        private static DeviceCacheHelper? _instance;
        private static readonly Lock _initLock = new();
        private static readonly NRole[] AllRoles =
        [
            NRole.Console,
            NRole.Multimedia,
            NRole.Communications
        ];

        private readonly AudioDeviceService _audio;
        private readonly Logger _logger;
        private readonly DeviceCacheHelperRuntime _runtime;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly Lock _stateLock = new();
        private readonly Lock _completionLock = new();
        private readonly Lock _accessDiagnosticsLock = new();

        private readonly record struct RefreshSnapshotResult(
            DeviceCacheSnapshotData?[] Playback,
            DeviceCacheSnapshotData?[] Recording,
            int PlaybackCount,
            int RecordingCount);

        private const string DefaultProbeContext = "unspecified";

        private volatile bool _disposed;
        private volatile bool _cacheInvalidated;
        private volatile bool _refreshInProgress;
        private int _isRefreshing;
        private long _deviceCacheExpiryTicks;
        private long _lastAccessDiagnosticsLogTicks;
        private long _lastInvalidateTicks;
        private string _lastTopologyFingerprint = string.Empty;
        private TaskCompletionSource<bool>? _refreshCompletionSource;
        private readonly Dictionary<string, int> _pendingAccessDiagnostics = new(StringComparer.OrdinalIgnoreCase);

        private readonly DeviceCacheSnapshotData?[] _cachedPlaybackSnapshots = new DeviceCacheSnapshotData?[AllRoles.Length];
        private readonly DeviceCacheSnapshotData?[] _cachedRecordingSnapshots = new DeviceCacheSnapshotData?[AllRoles.Length];

        internal bool CacheInvalidatedForTests => _cacheInvalidated;
        internal bool RefreshInProgressForTests => _refreshInProgress;
        internal long LastInvalidateTicksForTests => Interlocked.Read(ref _lastInvalidateTicks);
        internal long DeviceCacheExpiryTicksForTests => Interlocked.Read(ref _deviceCacheExpiryTicks);
        internal string LastTopologyFingerprintForTests => _lastTopologyFingerprint;
        internal Task WaitForRefreshCompletionForTestsAsync()
        {
            lock (_completionLock)
            {
                return _refreshCompletionSource?.Task ?? Task.CompletedTask;
            }
        }

        private bool TryBeginRefreshOperation(out Task<bool> refreshTask)
        {
            lock (_completionLock)
            {
                if (_refreshCompletionSource != null)
                {
                    refreshTask = _refreshCompletionSource.Task;
                    return false;
                }

                _refreshCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                refreshTask = _refreshCompletionSource.Task;
                _refreshInProgress = true;
                Interlocked.Exchange(ref _isRefreshing, 1);
                return true;
            }
        }

        private void CompleteRefreshOperation()
        {
            TaskCompletionSource<bool>? refreshCompletionSource;

            _refreshInProgress = false;
            Interlocked.Exchange(ref _isRefreshing, 0);

            lock (_completionLock)
            {
                refreshCompletionSource = _refreshCompletionSource;
                _refreshCompletionSource = null;
            }

            refreshCompletionSource?.TrySetResult(true);
        }

        private void ResetCacheState()
        {
            DisposeCachedDevices();
            _cacheInvalidated = false;
            _refreshInProgress = false;
            Interlocked.Exchange(ref _isRefreshing, 0);
            _lastTopologyFingerprint = string.Empty;
            Interlocked.Exchange(ref _lastInvalidateTicks, 0);
            Interlocked.Exchange(ref _deviceCacheExpiryTicks, 0);

            TaskCompletionSource<bool>? refreshCompletionSource;
            lock (_completionLock)
            {
                refreshCompletionSource = _refreshCompletionSource;
                _refreshCompletionSource = null;
            }

            refreshCompletionSource?.TrySetResult(true);
        }

        private async Task RunRefreshUnderLockAsync()
        {
            await _runtime.RunRefreshWorkAsync(async () =>
            {
                try
                {
                    await _refreshLock.WaitAsync();
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    return;
                }

                try
                {
                    if (!_disposed)
                    {
                        await RefreshCacheInternalAsync();
                    }
                }
                finally
                {
                    if (!_disposed)
                    {
                        _refreshLock.Release();
                    }
                }
            });
        }

        public static bool IsInitialized => _instance != null;

        public static DeviceCacheHelper Instance
        {
            get
            {
                if (_instance == null)
                    throw new InvalidOperationException("DeviceCacheHelper not initialized. Call Initialize first.");
                return _instance;
            }
        }

        private DeviceCacheHelper(AudioDeviceService audio, DeviceCacheHelperRuntime? runtime = null, Logger? logger = null)
        {
            _audio = audio;
            _logger = logger ?? Logger.Instance;
            _runtime = runtime ?? new DeviceCacheHelperRuntime
            {
                CaptureSnapshotsAsync = CaptureSnapshotsOnCoreAudioThreadAsync,
                MaterializeDeviceById = MaterializeDeviceOnCoreAudioThread,
                GetFallbackPlaybackDevice = GetFallbackPlaybackDeviceFromAudioService,
                GetFallbackRecordingDevice = GetFallbackRecordingDeviceFromAudioService,
                ProbeMuteState = ProbeMuteStateWithEndpointVolume,
                RunRefreshWorkAsync = static refreshWork => refreshWork(),
            };
            _lastAccessDiagnosticsLogTicks = Environment.TickCount64;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("DeviceCacheHelper", () => $"{AppConstants.Audio.LogEvents.DeviceCache.CacheInit} | roleCount={AllRoles.Length}");
            }
        }

        public static void Initialize(AudioDeviceService audio)
        {
            lock (_initLock)
            {
                if (_instance != null)
                    throw new InvalidOperationException("DeviceCacheHelper already initialized");
                _instance = new DeviceCacheHelper(audio);
            }
        }

        internal static void InitializeForTests(AudioDeviceService audio, DeviceCacheHelperRuntime runtime, Logger? logger = null)
        {
            lock (_initLock)
            {
                if (_instance != null)
                    throw new InvalidOperationException("DeviceCacheHelper already initialized");
                _instance = new DeviceCacheHelper(audio, runtime, logger);
            }
        }

        public static void DisposeSingleton()
        {
            DeviceCacheHelper? instance;
            lock (_initLock)
            {
                instance = _instance;
                _instance = null;
            }

            if (instance == null)
                return;

            lock (instance._stateLock)
            {
                if (instance._disposed)
                    return;

                instance.ResetCacheState();
                instance._disposed = true;
            }
            instance._refreshLock.Dispose();
        }

        private void TriggerBackgroundRefreshIfNeeded()
        {
            long currentTicks = Interlocked.Read(ref _deviceCacheExpiryTicks);
            if (currentTicks > Environment.TickCount64 && !_cacheInvalidated)
                return;

            if (!TryBeginRefreshOperation(out _))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_disposed)
                        return;

                    await RunRefreshUnderLockAsync();
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                }
                catch (Exception ex)
                {
                    _logger.Error("DeviceCacheHelper", () => $"{AppConstants.Audio.LogEvents.DeviceCache.BackgroundRefreshFailed} | reason={ex.GetType().Name}");
                }
                finally
                {
                    CompleteRefreshOperation();
                }
            });
        }

        private static int GetRoleIndex(NRole role) => role switch
        {
            NRole.Console => 0,
            NRole.Multimedia => 1,
            NRole.Communications => 2,
            _ => -1
        };

        /// <summary>
        /// Returns whether repeated invalidation requests should be collapsed into one background refresh window.
        /// </summary>
        internal static bool ShouldThrottleInvalidation(long currentTicks, long lastInvalidateTicks, long minIntervalMs = 100)
        {
            return currentTicks - lastInvalidateTicks < minIntervalMs;
        }

        internal static string BuildTopologyFingerprintForTests(
            (string Id, string Name, DeviceState State)?[] playbackSnapshots,
            (string Id, string Name, DeviceState State)?[] recordingSnapshots)
        {
            return BuildTopologyFingerprint(
                [.. playbackSnapshots.Select(static snapshot => snapshot is { } value
                    ? new DeviceCacheSnapshotData(value.Id, value.Name, value.State)
                    : (DeviceCacheSnapshotData?)null)],
                [.. recordingSnapshots.Select(static snapshot => snapshot is { } value
                    ? new DeviceCacheSnapshotData(value.Id, value.Name, value.State)
                    : (DeviceCacheSnapshotData?)null)]);
        }

        internal static bool UsePlaybackFallbackForRole(NRole role) => role == NRole.Multimedia;
        internal static bool UseRecordingFallbackForRole(NRole role) => role == NRole.Console;
        internal static bool ShouldFlushAccessDiagnostics(long currentTicks, long lastFlushTicks, long minIntervalMs = 30000)
        {
            return lastFlushTicks > 0 && currentTicks - lastFlushTicks >= minIntervalMs;
        }

        internal void FlushAccessDiagnosticsForTests()
        {
            FlushAccessDiagnostics(Environment.TickCount64);
        }

        internal void TrimDiagnosticsForHiddenMode()
        {
            lock (_accessDiagnosticsLock)
            {
                _pendingAccessDiagnostics.Clear();
            }
        }

        private bool TryCreateSnapshot(MMDevice device, out DeviceCacheSnapshotData snapshot)
        {
            snapshot = default;
            try
            {
                string id = device.ID;
                string name = device.FriendlyName;
                DeviceState state = device.State;
                snapshot = new DeviceCacheSnapshotData(id, name, state);
                return true;
            }
            catch (COMException ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("DeviceCacheHelper", () => $"Device COM exception during validation: {ex.HResult:X8}");
                }
                return false;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("DeviceCacheHelper", () => $"Device validation exception: {ex.GetType().Name}");
                }
                return false;
            }
        }

        private MMDevice? MaterializeDevice(DeviceCacheSnapshotData? snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Value.Id))
                return null;

            try
            {
                return _runtime.MaterializeDeviceById(snapshot.Value.Id);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("DeviceCacheHelper", () => $"{AppConstants.Audio.LogEvents.DeviceCache.MaterializeDeviceFailed} | id={LogPrivacy.Id(snapshot.Value.Id)} reason={ex.GetType().Name}");
                }
                return null;
            }
        }

        private static DeviceCacheSnapshotData?[] GetSnapshotArray(DeviceCacheSnapshotData?[] snapshots)
        {
            return [.. snapshots];
        }

        private static int CaptureSnapshots(
            IReadOnlyList<DeviceCacheSnapshotData?> snapshots,
            DeviceCacheSnapshotData?[] destination)
        {
            int captureCount = 0;
            int limit = Math.Min(snapshots.Count, AllRoles.Length);
            for (int index = 0; index < limit; index++)
            {
                DeviceCacheSnapshotData? snapshot = snapshots[index];
                if (snapshot == null)
                {
                    continue;
                }

                destination[index] = snapshot;
                captureCount++;
            }

            return captureCount;
        }

        private static RefreshSnapshotResult CreateRefreshSnapshotResult(
            DeviceCacheRefreshCapture capture)
        {
            var newPlayback = new DeviceCacheSnapshotData?[AllRoles.Length];
            var newRecording = new DeviceCacheSnapshotData?[AllRoles.Length];

            int playbackCount = CaptureSnapshots(
                capture.Playback,
                newPlayback);

            int recordingCount = CaptureSnapshots(
                capture.Recording,
                newRecording);

            return new RefreshSnapshotResult(newPlayback, newRecording, playbackCount, recordingCount);
        }

        private void ApplyRefreshResult(RefreshSnapshotResult result, Stopwatch refreshStopwatch)
        {
            string currentTopologyFingerprint = BuildTopologyFingerprint(result.Playback, result.Recording);

            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                bool unchangedTopology =
                    string.Equals(_lastTopologyFingerprint, currentTopologyFingerprint, StringComparison.Ordinal);

                if (!unchangedTopology)
                {
                    for (int index = 0; index < AllRoles.Length; index++)
                    {
                        _cachedPlaybackSnapshots[index] = result.Playback[index];
                        _cachedRecordingSnapshots[index] = result.Recording[index];
                    }

                    _lastTopologyFingerprint = currentTopologyFingerprint;
                }

                Interlocked.Exchange(ref _deviceCacheExpiryTicks, Environment.TickCount64 + AppConstants.Timing.DeviceCacheDurationMs);
                _cacheInvalidated = false;
                refreshStopwatch.Stop();

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    string eventName = unchangedTopology
                        ? AppConstants.Audio.LogEvents.DeviceCache.CacheRefreshSkippedUnchanged
                        : AppConstants.Audio.LogEvents.DeviceCache.CacheRefreshComplete;
                    _logger.Debug("DeviceCacheHelper", () => $"{eventName} | playbackCount={result.PlaybackCount} recordingCount={result.RecordingCount} elapsedMs={refreshStopwatch.Elapsed.TotalMilliseconds:F1} ttlMs={AppConstants.Timing.DeviceCacheDurationMs}");
                }
            }
        }

        private MMDevice? GetDeviceCore(
            NRole role,
            bool triggerBackgroundRefresh,
            DeviceCacheSnapshotData?[] snapshots,
            Func<NRole, string, MMDevice?> getFallbackDevice,
            string disposedMessage,
            string fallbackEventName,
            string cacheHitEventName,
            string fallbackContext)
        {
            DeviceCacheSnapshotData? snapshot;

            lock (_stateLock)
            {
                if (_disposed)
                {
                    _logger.Trace("DeviceCacheHelper", disposedMessage);
                    return null;
                }

                if (_refreshInProgress)
                {
                    RecordAccessDiagnostic(fallbackEventName, role, "refresh-in-progress");

                    return getFallbackDevice(role, fallbackContext);
                }

                if (triggerBackgroundRefresh)
                {
                    TriggerBackgroundRefreshIfNeeded();
                }

                int index = GetRoleIndex(role);
                if (index < 0)
                    return null;

                snapshot = snapshots[index];
                if (snapshot == null)
                {
                    RecordAccessDiagnostic(fallbackEventName, role, "invalid-cache-entry");

                    return getFallbackDevice(role, fallbackContext);
                }
            }

            MMDevice? materialized = MaterializeDevice(snapshot);
            if (materialized != null)
            {
                RecordAccessDiagnostic(cacheHitEventName, role, "cache-hit");

                return materialized;
            }

            RecordAccessDiagnostic(fallbackEventName, role, "materialize-failed");

            InvalidateCache();
            return getFallbackDevice(role, fallbackContext);
        }

        private void RecordAccessDiagnostic(string eventName, NRole role, string reason)
        {
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            long now = Environment.TickCount64;
            bool shouldFlush;
            lock (_accessDiagnosticsLock)
            {
                string key = $"event={eventName} role={role} reason={reason}";
                if (_pendingAccessDiagnostics.TryGetValue(key, out int count))
                {
                    _pendingAccessDiagnostics[key] = count + 1;
                }
                else
                {
                    _pendingAccessDiagnostics[key] = 1;
                }

                shouldFlush = ShouldFlushAccessDiagnostics(now, _lastAccessDiagnosticsLogTicks);
            }

            if (shouldFlush)
            {
                FlushAccessDiagnostics(now);
            }
        }

        private void FlushAccessDiagnostics(long now)
        {
            KeyValuePair<string, int>[] entries;
            long windowMs;

            lock (_accessDiagnosticsLock)
            {
                if (_pendingAccessDiagnostics.Count == 0)
                {
                    _lastAccessDiagnosticsLogTicks = now;
                    return;
                }

                entries = [.. _pendingAccessDiagnostics.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)];
                _pendingAccessDiagnostics.Clear();
                windowMs = Math.Max(0, now - _lastAccessDiagnosticsLogTicks);
                _lastAccessDiagnosticsLogTicks = now;
            }

            _logger.Debug(
                "DeviceCacheHelper",
                () => $"{AppConstants.Audio.LogEvents.DeviceCache.AccessDiagnostics} | windowMs={windowMs} summary={string.Join("; ", entries.Select(static entry => $"{entry.Key} count={entry.Value}"))}");
        }

        private MMDevice?[] GetAllDevicesCore(
            DeviceCacheSnapshotData?[] snapshots,
            string disposedMessage,
            Func<int, MMDevice?> getFallbackDevice)
        {
            DeviceCacheSnapshotData?[] snapshotCopy;

            lock (_stateLock)
            {
                if (_disposed)
                {
                    _logger.Trace("DeviceCacheHelper", disposedMessage);
                    return [];
                }

                if (!_refreshInProgress)
                {
                    TriggerBackgroundRefreshIfNeeded();
                }

                snapshotCopy = GetSnapshotArray(snapshots);
            }

            var result = new MMDevice?[AllRoles.Length];
            for (int index = 0; index < AllRoles.Length; index++)
            {
                result[index] = MaterializeDevice(snapshotCopy[index]) ?? getFallbackDevice(index);
            }

            return result;
        }

        private string? GetSnapshotPropertyCore(
            NRole role,
            bool triggerBackgroundRefresh,
            DeviceCacheSnapshotData?[] snapshots,
            string disposedMessage,
            Func<DeviceCacheSnapshotData, string> selector)
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    _logger.Trace("DeviceCacheHelper", disposedMessage);
                    return null;
                }

                if (triggerBackgroundRefresh)
                {
                    TriggerBackgroundRefreshIfNeeded();
                }

                int index = GetRoleIndex(role);
                if (index < 0)
                    return null;

                return snapshots[index] is DeviceCacheSnapshotData snapshot ? selector(snapshot) : null;
            }
        }

        private bool TryGetPrimaryDeviceMuteState(
            Func<string, MMDevice?> getPrimaryDevice,
            string probeContext,
            string missingDeviceMessage,
            string errorPrefix,
            out bool muted)
        {
            muted = false;
            using MMDevice? primaryDevice = getPrimaryDevice(probeContext);
            if (primaryDevice == null)
            {
                _logger.Trace("DeviceCacheHelper", () => $"{missingDeviceMessage} | context={probeContext}");
                return false;
            }

            try
            {
                bool? muteState = _runtime.ProbeMuteState(_logger, primaryDevice, probeContext);
                if (muteState.HasValue)
                {
                    muted = muteState.Value;
                    return true;
                }
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80010108))
            {
                _logger.Trace("DeviceCacheHelper", () => $"Device COM object invalid, triggering cache refresh | context={probeContext}");
                InvalidateCache();
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("DeviceCacheHelper", () => $"{errorPrefix}: {ex.GetType().Name} | context={probeContext}");
                }
            }

            return false;
        }

        private MMDevice? GetFallbackPlaybackDevice(NRole role, string fallbackContext)
        {
            try
            {
                return UsePlaybackFallbackForRole(role)
                    ? _runtime.GetFallbackPlaybackDevice(role, fallbackContext)
                    : null;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("DeviceCacheHelper", () => $"fallback-playback-device-failed | role={role} context={fallbackContext} reason={ex.GetType().Name}");
                }

                return null;
            }
        }

        private MMDevice? GetFallbackRecordingDevice(NRole role, string fallbackContext)
        {
            try
            {
                return UseRecordingFallbackForRole(role)
                    ? _runtime.GetFallbackRecordingDevice(role, fallbackContext)
                    : null;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("DeviceCacheHelper", () => $"fallback-recording-device-failed | role={role} context={fallbackContext} reason={ex.GetType().Name}");
                }

                return null;
            }
        }

        public MMDevice? GetPlaybackDevice(NRole role)
        {
            return GetPlaybackDeviceCore(role, triggerBackgroundRefresh: true);
        }

        public MMDevice? GetPlaybackDeviceWithoutRefresh(NRole role)
        {
            return GetPlaybackDeviceCore(role, triggerBackgroundRefresh: false);
        }

        private MMDevice? GetPlaybackDeviceCore(NRole role, bool triggerBackgroundRefresh)
        {
            return GetDeviceCore(
                role,
                triggerBackgroundRefresh,
                _cachedPlaybackSnapshots,
                GetFallbackPlaybackDevice,
                disposedMessage: "GetPlaybackDevice called on disposed cache",
                fallbackEventName: AppConstants.Audio.LogEvents.DeviceCache.GetPlaybackFallback,
                cacheHitEventName: AppConstants.Audio.LogEvents.DeviceCache.GetPlaybackCacheHit,
                fallbackContext: DefaultProbeContext);
        }

        public MMDevice? GetRecordingDevice(NRole role)
        {
            return GetRecordingDeviceCore(role, triggerBackgroundRefresh: true);
        }

        public MMDevice? GetRecordingDeviceWithoutRefresh(NRole role)
        {
            return GetRecordingDeviceCore(role, triggerBackgroundRefresh: false);
        }

        private MMDevice? GetRecordingDeviceCore(NRole role, bool triggerBackgroundRefresh)
        {
            return GetDeviceCore(
                role,
                triggerBackgroundRefresh,
                _cachedRecordingSnapshots,
                GetFallbackRecordingDevice,
                disposedMessage: "GetRecordingDevice called on disposed cache",
                fallbackEventName: AppConstants.Audio.LogEvents.DeviceCache.GetRecordingFallback,
                cacheHitEventName: AppConstants.Audio.LogEvents.DeviceCache.GetRecordingCacheHit,
                fallbackContext: DefaultProbeContext);
        }

        public MMDevice? GetPrimaryPlaybackDevice(string? fallbackContext = null)
        {
            return GetDeviceCore(
                NRole.Multimedia,
                triggerBackgroundRefresh: true,
                _cachedPlaybackSnapshots,
                GetFallbackPlaybackDevice,
                disposedMessage: "GetPlaybackDevice called on disposed cache",
                fallbackEventName: AppConstants.Audio.LogEvents.DeviceCache.GetPlaybackFallback,
                cacheHitEventName: AppConstants.Audio.LogEvents.DeviceCache.GetPlaybackCacheHit,
                fallbackContext: string.IsNullOrWhiteSpace(fallbackContext) ? DefaultProbeContext : fallbackContext);
        }

        public MMDevice? GetPrimaryRecordingDevice(string? fallbackContext = null)
        {
            return GetDeviceCore(
                NRole.Console,
                triggerBackgroundRefresh: true,
                _cachedRecordingSnapshots,
                GetFallbackRecordingDevice,
                disposedMessage: "GetRecordingDevice called on disposed cache",
                fallbackEventName: AppConstants.Audio.LogEvents.DeviceCache.GetRecordingFallback,
                cacheHitEventName: AppConstants.Audio.LogEvents.DeviceCache.GetRecordingCacheHit,
                fallbackContext: string.IsNullOrWhiteSpace(fallbackContext) ? DefaultProbeContext : fallbackContext);
        }

        public IReadOnlyList<MMDevice?> GetAllPlaybackDevices()
        {
            return GetAllDevicesCore(
                _cachedPlaybackSnapshots,
                disposedMessage: "GetAllPlaybackDevices called on disposed cache",
                getFallbackDevice: index => GetFallbackPlaybackDevice(AllRoles[index], DefaultProbeContext));
        }

        public IReadOnlyList<MMDevice?> GetAllRecordingDevices()
        {
            return GetAllDevicesCore(
                _cachedRecordingSnapshots,
                disposedMessage: "GetAllRecordingDevices called on disposed cache",
                getFallbackDevice: index => GetFallbackRecordingDevice(AllRoles[index], DefaultProbeContext));
        }

        public string? GetPlaybackDeviceName(NRole role)
        {
            return GetPlaybackDeviceNameCore(role, triggerBackgroundRefresh: true);
        }

        public string? GetPlaybackDeviceIdWithoutRefresh(NRole role)
        {
            return GetPlaybackDeviceIdCore(role, triggerBackgroundRefresh: false);
        }

        private string? GetPlaybackDeviceIdCore(NRole role, bool triggerBackgroundRefresh)
        {
            return GetSnapshotPropertyCore(
                role,
                triggerBackgroundRefresh,
                _cachedPlaybackSnapshots,
                disposedMessage: "GetPlaybackDeviceId called on disposed cache",
                selector: static snapshot => snapshot.Id);
        }

        public string? GetPlaybackDeviceNameWithoutRefresh(NRole role)
        {
            return GetPlaybackDeviceNameCore(role, triggerBackgroundRefresh: false);
        }

        private string? GetPlaybackDeviceNameCore(NRole role, bool triggerBackgroundRefresh)
        {
            return GetSnapshotPropertyCore(
                role,
                triggerBackgroundRefresh,
                _cachedPlaybackSnapshots,
                disposedMessage: "GetPlaybackDeviceName called on disposed cache",
                selector: static snapshot => snapshot.Name);
        }

        public string? GetRecordingDeviceName(NRole role)
        {
            return GetRecordingDeviceNameCore(role, triggerBackgroundRefresh: true);
        }

        public string? GetRecordingDeviceIdWithoutRefresh(NRole role)
        {
            return GetRecordingDeviceIdCore(role, triggerBackgroundRefresh: false);
        }

        private string? GetRecordingDeviceIdCore(NRole role, bool triggerBackgroundRefresh)
        {
            return GetSnapshotPropertyCore(
                role,
                triggerBackgroundRefresh,
                _cachedRecordingSnapshots,
                disposedMessage: "GetRecordingDeviceId called on disposed cache",
                selector: static snapshot => snapshot.Id);
        }

        public string? GetRecordingDeviceNameWithoutRefresh(NRole role)
        {
            return GetRecordingDeviceNameCore(role, triggerBackgroundRefresh: false);
        }

        private string? GetRecordingDeviceNameCore(NRole role, bool triggerBackgroundRefresh)
        {
            return GetSnapshotPropertyCore(
                role,
                triggerBackgroundRefresh,
                _cachedRecordingSnapshots,
                disposedMessage: "GetRecordingDeviceName called on disposed cache",
                selector: static snapshot => snapshot.Name);
        }

        public bool IsPlaybackMuted(string? probeContext = null)
        {
            return TryGetPlaybackMuteState(out bool muted, probeContext) && muted;
        }

        public bool TryGetPlaybackMuteState(out bool muted, string? probeContext = null)
        {
            return TryGetPrimaryDeviceMuteState(
                GetPrimaryPlaybackDevice,
                string.IsNullOrWhiteSpace(probeContext) ? DefaultProbeContext : probeContext,
                missingDeviceMessage: "Primary playback device is null during mute check",
                errorPrefix: "Error checking playback mute",
                out muted);
        }

        public bool IsRecordingMuted(string? probeContext = null)
        {
            return TryGetRecordingMuteState(out bool muted, probeContext) && muted;
        }

        public bool TryGetRecordingMuteState(out bool muted, string? probeContext = null)
        {
            return TryGetPrimaryDeviceMuteState(
                GetPrimaryRecordingDevice,
                string.IsNullOrWhiteSpace(probeContext) ? DefaultProbeContext : probeContext,
                missingDeviceMessage: "Primary recording device is null during mute check",
                errorPrefix: "Error checking recording mute",
                out muted);
        }

        private async Task RefreshCacheInternalAsync()
        {
            var refreshStopwatch = Stopwatch.StartNew();

            RefreshSnapshotResult result;

            try
            {
                DeviceCacheRefreshCapture capture = await _runtime.CaptureSnapshotsAsync();
                result = CreateRefreshSnapshotResult(capture);
            }
            catch (Exception ex)
            {
                _logger.Error("DeviceCacheHelper", () => $"{AppConstants.Audio.LogEvents.DeviceCache.CacheRefreshFetchFailed} | reason={ex.GetType().Name}");
                Interlocked.Exchange(ref _deviceCacheExpiryTicks, 0);
                return;
            }

            ApplyRefreshResult(result, refreshStopwatch);
        }

        private static void DisposeUniqueDevices(IReadOnlyList<MMDevice?> devices)
        {
            var unique = new HashSet<MMDevice>(ReferenceEqualityComparer.Instance);
            foreach (var device in devices)
            {
                if (device == null || !unique.Add(device))
                {
                    continue;
                }

                try { device.Dispose(); }
                catch (Exception ex)
                {
                    var logger = Logger.Instance;
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("DeviceCacheHelper", () => $"{AppConstants.Audio.LogEvents.DeviceCache.DisposeArrayEntryFailed} | reason={ex.GetType().Name}");
                    }
                }
            }
        }

        /// <summary>
        /// Marks the cache stale and schedules the next consumer-facing access to observe a background refresh instead
        /// of performing redundant invalidations for bursty device-notification storms.
        /// </summary>
        public void InvalidateCache()
        {
            long currentTicks = Environment.TickCount64;
            long lastInvalidate = Interlocked.Read(ref _lastInvalidateTicks);

            if (ShouldThrottleInvalidation(currentTicks, lastInvalidate))
                return;

            if (Interlocked.CompareExchange(ref _lastInvalidateTicks, currentTicks, lastInvalidate) != lastInvalidate)
                return;

            lock (_stateLock)
            {
                if (_disposed)
                {
                    _logger.Trace("DeviceCacheHelper", "InvalidateCache called on disposed cache");
                    return;
                }

                _cacheInvalidated = true;
                _logger.Debug("DeviceCacheHelper", () => $"{AppConstants.Audio.LogEvents.DeviceCache.CacheInvalidated} | nextAccess=background-refresh");
            }
        }

        /// <summary>
        /// Forces a cache refresh and coalesces concurrent callers onto the same in-flight refresh task.
        /// </summary>
        /// <remarks>
        /// When a refresh is already running, later callers await that shared completion instead of starting a second
        /// refresh pass, which keeps COM enumeration churn bounded during hotplug bursts.
        /// </remarks>
        public async Task RefreshAsync()
        {
            if (_disposed)
            {
                _logger.Trace("DeviceCacheHelper", "RefreshAsync called on disposed cache");
                return;
            }

            if (!TryBeginRefreshOperation(out Task<bool> refreshTask))
            {
                _logger.Debug("DeviceCacheHelper", AppConstants.Audio.LogEvents.DeviceCache.CacheRefreshAwaitExisting);
                await refreshTask;
                return;
            }

            try
            {
                await RunRefreshUnderLockAsync();
            }
            finally
            {
                CompleteRefreshOperation();
            }
        }

        /// <summary>
        /// Starts a fire-and-forget refresh that routes faults through the helper logger rather than surfacing them to
        /// callers that only need eventual cache freshness.
        /// </summary>
        public void Refresh()
        {
            _ = RefreshAndObserveAsync();
        }

        private async Task RefreshAndObserveAsync()
        {
            try
            {
                await RefreshAsync();
            }
            catch (ObjectDisposedException) when (_disposed)
            {
            }
            catch (Exception ex)
            {
                _logger.Error("DeviceCacheHelper", () => $"{AppConstants.Audio.LogEvents.DeviceCache.CacheRefreshTaskFaulted} | reason={ex.GetType().Name}");
            }
        }

        private void DisposeCachedDevices()
        {
            for (int i = 0; i < AllRoles.Length; i++)
            {
                _cachedPlaybackSnapshots[i] = null;
                _cachedRecordingSnapshots[i] = null;
            }
        }

        private static string BuildTopologyFingerprint(
            DeviceCacheSnapshotData?[] playbackSnapshots,
            DeviceCacheSnapshotData?[] recordingSnapshots)
        {
            var builder = new StringBuilder();

            builder.Append("P|");
            AppendSnapshots(builder, playbackSnapshots);
            builder.Append("R|");
            AppendSnapshots(builder, recordingSnapshots);

            return builder.ToString();
        }

        private static void AppendSnapshots(StringBuilder builder, IEnumerable<DeviceCacheSnapshotData?> snapshots)
        {
            foreach (var snapshot in snapshots)
            {
                if (snapshot is not DeviceCacheSnapshotData value)
                {
                    builder.Append("null|");
                    continue;
                }

                builder.Append(value.Id);
                builder.Append('=');
                builder.Append(value.Name);
                builder.Append('=');
                builder.Append((int)value.State);
                builder.Append('|');
            }
        }

        private async Task<DeviceCacheRefreshCapture> CaptureSnapshotsOnCoreAudioThreadAsync()
        {
            return await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                var playbackDevices = _audio.GetAllDefaultPlaybackDevices();
                var recordingDevices = _audio.GetAllDefaultRecordingDevices();

                try
                {
                    return new DeviceCacheRefreshCapture(
                        CaptureSnapshotData(playbackDevices),
                        CaptureSnapshotData(recordingDevices));
                }
                finally
                {
                    DisposeUniqueDevices(playbackDevices);
                    DisposeUniqueDevices(recordingDevices);
                }
            });
        }

        private DeviceCacheSnapshotData?[] CaptureSnapshotData(List<MMDevice?> devices)
        {
            var snapshots = new DeviceCacheSnapshotData?[AllRoles.Length];
            int limit = Math.Min(devices.Count, AllRoles.Length);
            for (int index = 0; index < limit; index++)
            {
                MMDevice? device = devices[index];
                if (device != null && TryCreateSnapshot(device, out DeviceCacheSnapshotData snapshot))
                {
                    snapshots[index] = snapshot;
                }
            }

            return snapshots;
        }

        private static MMDevice? MaterializeDeviceOnCoreAudioThread(string deviceId)
        {
            return ComThreadingHelper.RunOnCoreAudioThread(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                return device;
            });
        }

        private MMDevice? GetFallbackPlaybackDeviceFromAudioService(NRole role, string fallbackContext)
        {
            return _audio.GetDefaultPlaybackDevice(fallbackContext);
        }

        private MMDevice? GetFallbackRecordingDeviceFromAudioService(NRole role, string fallbackContext)
        {
            return _audio.GetDefaultRecordingDevice(fallbackContext);
        }

        private static bool? ProbeMuteStateWithEndpointVolume(Logger logger, MMDevice device, string probeContext)
        {
            if (AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var volume, probeContext))
            {
                return volume.Mute;
            }

            return null;
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _logger.Debug("DeviceCacheHelper", "dispose-start");
                ResetCacheState();
                _disposed = true;
            }
            _refreshLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
