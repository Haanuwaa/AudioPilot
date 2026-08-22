using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels
{
    public partial class MixerViewModel(
        AudioDeviceService audio,
        Dispatcher dispatcher,
        AudioMixerMode mixerMode = AudioMixerMode.Output,
        Logger? logger = null,
        DeviceCacheHelper? deviceCache = null)
    {
        private readonly AudioDeviceService _audio = audio;
        private readonly Logger _logger = logger ?? Logger.Instance;
        private readonly Dispatcher _dispatcher = dispatcher;
        private readonly AudioMixerMode _mixerMode = mixerMode;
        private readonly DeviceCacheHelper _deviceCache = deviceCache ?? DeviceCacheHelper.Instance;
        private CancellationTokenSource? _refreshCts;
        private CancellationTokenSource? _muteApplyCts;
        private int _activeRefreshCount;
        private int _refreshGeneration;
        private readonly Lock _refreshSettlementLock = new();
        private TaskCompletionSource<object?> _refreshSettlementTcs = CreateCompletedRefreshSettlementSource();
        private readonly ConcurrentDictionary<string, AudioSessionItem> _sessionsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<uint, string> _pidToProcessName = new();
        private readonly ConcurrentDictionary<string, float> _userSetVolumes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastVolumeSetByUs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ThrottleState> _throttleStates = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<AudioSessionSnapshot>? _lastProcessedSnapshotEntries;
        private readonly ConcurrentBag<HashSet<string>> _incomingIdsPool = [];
        private readonly ConcurrentBag<List<(string Id, AudioSessionItem Item)>> _toAddPool = [];
        private readonly ConcurrentBag<List<(string Id, float Volume, bool IsMuted)>> _toUpdatePool = [];
        private readonly ConcurrentBag<List<string>> _idsToRemovePool = [];
        private const int MaxPidProcessMapEntries = AppConstants.Limits.MaxPidProcessMapEntries;
        private const int MaxPooledCollectionCapacity = AppConstants.Limits.MaxPidProcessMapEntries;

        private readonly TimeSpan _ourVolumeSetTtl = TimeSpan.FromSeconds(AppConstants.Timing.OurVolumeSetTtlSeconds);
        private static readonly int ThrottleIntervalMs = AppConstants.Timing.VolumeThrottleIntervalMs;
        private readonly Lock _refreshMetricsLock = new();
        private DateTime _refreshMetricsWindowStartUtc = DateTime.UtcNow;
        private int _refreshMetricsCount;
        private double _refreshMetricsTotalMs;
        private double _refreshMetricsMaxMs;
        private MixerSharedSessionCoordinator? _sharedSessionBridge;
        private readonly ConcurrentDictionary<string, byte> _subscribedSessionIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly double SlowRefreshWarningMs = AppConstants.Timing.MixerSlowRefreshWarningMs;
        private const int SlowRefreshWarningConsecutiveCount = AppConstants.Timing.MixerSlowRefreshConsecutiveCount;
        private bool _hasCompletedFirstRefresh;
        private int _consecutiveSlowRefreshes;
        private int _cacheWindowDiagnosticsRefreshCount;
        private int _interactiveRefreshCount;
        private int _backgroundRefreshCount;
        private int _requiresActivationRefresh;
        private volatile bool _disposed;
        private RelayCommand? _toggleMuteCommand;

        internal readonly record struct MixerMutationResult(int Attempted, int Succeeded, int Failed)
        {
            public bool IsSuccess => Attempted > 0 && Succeeded > 0 && Failed == 0;
            public static MixerMutationResult Success(int succeeded = 1) => new(Math.Max(succeeded, 1), succeeded, 0);
            public static MixerMutationResult Failure(int attempted = 1) => new(Math.Max(attempted, 1), 0, Math.Max(attempted, 1));
        }

        private sealed class ThrottleState
        {
            public DateTime LastApplied;
            public float PendingVolume;
            public bool HasPending;
            public CancellationTokenSource? TrailingCts;
            public readonly Lock Lock = new();
        }

        private async Task ObserveMuteApplyAsync(Task<MixerMutationResult> task, string phase, Action? onSuccess = null, Action? onFailure = null)
        {
            try
            {
                MixerMutationResult result = await task;
                if (result.IsSuccess) onSuccess?.Invoke();
                else HandleMutationFailure(phase, result, onFailure);
            }
            catch (OperationCanceledException)
            {
                onFailure?.Invoke();
            }
            catch (Exception ex)
            {
                onFailure?.Invoke();
                _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-task-failed | phase={phase}", nameof(ObserveMuteApplyAsync), ex);
            }
        }

        private async Task ObserveMuteApplyAsync(Task<MixerMutationResult> task, string phase, CancellationToken cancellationToken, Action? onSuccess = null, Action? onFailure = null)
        {
            try
            {
                MixerMutationResult result = await task.WaitAsync(cancellationToken);
                if (result.IsSuccess) onSuccess?.Invoke();
                else HandleMutationFailure(phase, result, onFailure);
            }
            catch (OperationCanceledException)
            {
                onFailure?.Invoke();
            }
            catch (Exception ex)
            {
                onFailure?.Invoke();
                _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-task-failed | phase={phase}", nameof(ObserveMuteApplyAsync), ex);
            }
        }

        private void HandleMutationFailure(string phase, MixerMutationResult result, Action? onFailure)
        {
            onFailure?.Invoke();
            _logger.Warning("MixerViewModel", () => $"mixer-mutation-not-applied | phase={phase} attempted={result.Attempted} succeeded={result.Succeeded} failed={result.Failed}");
            MarkActivationRefreshStale($"mutation-failed:{phase}");
            _ = RefreshAsync(interactive: true);
        }

        public RelayCommand ToggleMuteCommand => _toggleMuteCommand ??= new RelayCommand(parameter =>
        {
            if (parameter is AudioSessionItem item)
            {
                ToggleMute(item);
            }
        });

        public ObservableCollection<AudioSessionItem> Sessions { get; } = [];

        internal AudioMixerMode MixerMode => _mixerMode;
        internal bool IsDisposed => _disposed;

        internal static void ConnectSharedSessionPair(MixerViewModel outputMixer, MixerViewModel inputMixer)
        {
            ArgumentNullException.ThrowIfNull(outputMixer);
            ArgumentNullException.ThrowIfNull(inputMixer);

            var bridge = new MixerSharedSessionCoordinator(AudioMixerMode.Output);
            outputMixer._sharedSessionBridge = bridge;
            inputMixer._sharedSessionBridge = bridge;
        }

        internal bool IsRefreshInProgress => Interlocked.CompareExchange(ref _activeRefreshCount, 0, 0) > 0;
        internal bool RequiresActivationRefresh => Interlocked.CompareExchange(ref _requiresActivationRefresh, 0, 0) != 0;

        internal async Task WaitForRefreshSettlementAsync(CancellationToken cancellationToken)
        {
            Task settlementTask;
            lock (_refreshSettlementLock)
            {
                if (Interlocked.CompareExchange(ref _activeRefreshCount, 0, 0) == 0)
                {
                    return;
                }

                settlementTask = _refreshSettlementTcs.Task;
            }

            await settlementTask.WaitAsync(cancellationToken);
        }

        private string GetSessionIdentity(AudioSessionInfo session, out uint? processId)
        {
            processId = null;
            string? displayName = session.DisplayName;

            if (displayName?.StartsWith("Master Volume", StringComparison.OrdinalIgnoreCase) == true)
                return "master:primary";

            if (displayName?.StartsWith("Microphone Volume", StringComparison.OrdinalIgnoreCase) == true)
                return "mic:primary";

            if (string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase))
                return "system:sounds";

            if (session.ProcessId.HasValue && session.ProcessId.Value != 0)
            {
                processId = session.ProcessId.Value;
                return $"pid:{session.ProcessId.Value}";
            }

            if (session.AudioSessionControl != null)
            {
                try
                {
                    uint pid = session.AudioSessionControl.GetProcessID;
                    if (pid != 0)
                    {
                        processId = pid;
                        return $"pid:{pid}";
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("MixerViewModel", () => $"mixer-session-process-id-read-failed | session={LogPrivacy.Session(displayName)} error={ex.GetType().Name}");
                    }
                }
            }

            return $"name:{displayName ?? "unknown"}";
        }

        private static string GetSessionIdentity(AudioSessionSnapshot session, out uint? processId)
        {
            processId = null;
            string displayName = session.DisplayName;

            if (displayName.StartsWith("Master Volume", StringComparison.OrdinalIgnoreCase))
                return "master:primary";

            if (displayName.StartsWith("Microphone Volume", StringComparison.OrdinalIgnoreCase))
                return "mic:primary";

            if (string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase))
                return "system:sounds";

            if (session.ProcessId.HasValue && session.ProcessId.Value != 0)
            {
                processId = session.ProcessId.Value;
                return $"pid:{session.ProcessId.Value}";
            }

            return $"name:{displayName}";
        }

        private static string GetSessionIdForItem(AudioSessionItem item)
        {
            if (item.IsMaster)
                return "master:primary";

            if (item.IsMic)
                return "mic:primary";

            if (item.IsSystemSounds)
                return "system:sounds";

            if (item.ProcessId.HasValue)
                return $"pid:{item.ProcessId.Value}";

            return $"name:{item.DisplayName}";
        }

        internal static string FormatSessionIdForLog(string sessionId) => LogPrivacy.Session(sessionId);

        internal static string FormatProcessIdForLog(uint processId) =>
            LogPrivacy.Id(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private static string GetUserVolumeKey(string sessionId) => $"user:{sessionId}";

        internal static bool IsSharedSessionId(string sessionId)
        {
            return string.Equals(sessionId, "master:primary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sessionId, "mic:primary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sessionId, "system:sounds", StringComparison.OrdinalIgnoreCase);
        }

        private void AttachVolumeChangedHandlerIfNeeded(string sessionId, AudioSessionItem item)
        {
            if (IsSharedSessionId(sessionId) && _sharedSessionBridge is MixerSharedSessionCoordinator bridge)
            {
                bridge.AttachVolumeChangedHandler(sessionId, item, this);
                return;
            }

            AttachOwnedVolumeChangedHandler(sessionId, item);
        }

        private void DetachVolumeChangedHandlerIfNeeded(string sessionId, AudioSessionItem item)
        {
            if (IsSharedSessionId(sessionId) && _sharedSessionBridge is MixerSharedSessionCoordinator bridge)
            {
                bridge.DetachVolumeChangedHandler(sessionId, item, this);
                return;
            }

            DetachOwnedVolumeChangedHandler(sessionId, item);
        }

        internal void AttachOwnedVolumeChangedHandler(string sessionId, AudioSessionItem item)
        {
            if (_subscribedSessionIds.TryAdd(sessionId, 0))
            {
                item.VolumeChanged += OnItemVolumeChanged;
                item.MuteChanged += OnItemMuteChanged;
            }
        }

        internal void DetachOwnedVolumeChangedHandler(string sessionId, AudioSessionItem item)
        {
            if (_subscribedSessionIds.TryRemove(sessionId, out _))
            {
                item.VolumeChanged -= OnItemVolumeChanged;
                item.MuteChanged -= OnItemMuteChanged;
            }
        }

        private AudioSessionItem CreateSessionItem(string sessionId, AudioSessionSnapshot session, uint? processId)
        {
            string displayName = session.DisplayName ?? "Unknown";
            bool isMaster = displayName.StartsWith("Master Volume", StringComparison.OrdinalIgnoreCase);
            bool isMic = displayName.StartsWith("Microphone Volume", StringComparison.OrdinalIgnoreCase);
            bool isSystemSounds = string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase);

            if (IsSharedSessionId(sessionId) && _sharedSessionBridge is MixerSharedSessionCoordinator bridge)
            {
                return bridge.GetOrAdd(
                    sessionId,
                    () => new AudioSessionItem(displayName, session.Volume, isMaster, isMic, isSystemSounds, processId, session.IsMuted, session.ProcessName, session.SessionInstanceId, session.EndpointId));
            }

            return new AudioSessionItem(displayName, session.Volume, isMaster, isMic, isSystemSounds, processId, session.IsMuted, session.ProcessName, session.SessionInstanceId, session.EndpointId);
        }

        private AudioSessionItem ResolveSharedSessionItemForAdd(string sessionId, AudioSessionItem item)
        {
            if (IsSharedSessionId(sessionId) && _sharedSessionBridge is MixerSharedSessionCoordinator bridge)
            {
                return bridge.GetOrAdd(sessionId, () => item);
            }

            return item;
        }

        private static uint? GetProcessIdForItem(AudioSessionItem item)
        {
            if (item.IsMaster || item.IsMic || item.IsSystemSounds)
                return null;

            return item.ProcessId;
        }

        private HashSet<string> RentIncomingIdsSet(int expectedCount)
        {
            if (_incomingIdsPool.TryTake(out var set))
            {
                set.Clear();
                return set;
            }

            int capacity = expectedCount > 0 ? expectedCount : 16;
            return new HashSet<string>(capacity, StringComparer.OrdinalIgnoreCase);
        }

        private void ReturnIncomingIdsSet(HashSet<string>? set)
        {
            if (set == null)
            {
                return;
            }

            int capacity = set.EnsureCapacity(0);
            set.Clear();
            if (capacity > MaxPooledCollectionCapacity)
            {
                return;
            }

            _incomingIdsPool.Add(set);
        }

        private List<(string Id, AudioSessionItem Item)> RentToAddList()
        {
            if (_toAddPool.TryTake(out var list))
            {
                list.Clear();
                return list;
            }

            return [];
        }

        private void ReturnToAddList(List<(string Id, AudioSessionItem Item)>? list)
        {
            if (list == null)
            {
                return;
            }

            int capacity = list.Capacity;
            list.Clear();
            if (capacity > MaxPooledCollectionCapacity)
            {
                return;
            }

            _toAddPool.Add(list);
        }

        private List<(string Id, float Volume, bool IsMuted)> RentToUpdateList()
        {
            if (_toUpdatePool.TryTake(out var list))
            {
                list.Clear();
                return list;
            }

            return [];
        }

        private void ReturnToUpdateList(List<(string Id, float Volume, bool IsMuted)>? list)
        {
            if (list == null)
            {
                return;
            }

            int capacity = list.Capacity;
            list.Clear();
            if (capacity > MaxPooledCollectionCapacity)
            {
                return;
            }

            _toUpdatePool.Add(list);
        }

        private List<string> RentIdsToRemoveList()
        {
            if (_idsToRemovePool.TryTake(out var list))
            {
                list.Clear();
                return list;
            }

            return [];
        }

        private void ReturnIdsToRemoveList(List<string>? list)
        {
            if (list == null)
            {
                return;
            }

            int capacity = list.Capacity;
            list.Clear();
            if (capacity > MaxPooledCollectionCapacity)
            {
                return;
            }

            _idsToRemovePool.Add(list);
        }

        private static TaskCompletionSource<object?> CreateCompletedRefreshSettlementSource()
        {
            var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            completionSource.TrySetResult(null);
            return completionSource;
        }

        private TaskCompletionSource<object?> EnterRefreshSettlementCycle()
        {
            lock (_refreshSettlementLock)
            {
                if (Interlocked.Increment(ref _activeRefreshCount) == 1)
                {
                    _refreshSettlementTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return _refreshSettlementTcs;
            }
        }

        private void LeaveRefreshSettlementCycle(TaskCompletionSource<object?> settlementSource)
        {
            if (Interlocked.Decrement(ref _activeRefreshCount) == 0)
            {
                CompleteRefreshSettlementCycle(settlementSource);
            }
        }

        /// <summary>
        /// Refreshes mixer rows from the current audio-session snapshot across active playback devices.
        /// </summary>
        /// <remarks>
        /// Refreshes are cancel-coalesced so only the newest cycle updates UI state. Existing items are diffed by
        /// stable session identity to preserve user interactions while pruning stale sessions.
        /// </remarks>
        public async Task RefreshAsync(bool interactive = true)
        {
            if (_disposed)
            {
                return;
            }

            TaskCompletionSource<object?> refreshSettlementSource = EnterRefreshSettlementCycle();

            bool traceEnabled = _logger.IsEnabled(LogLevel.Trace);
            bool debugEnabled = _logger.IsEnabled(LogLevel.Debug);
            bool warningEnabled = _logger.IsEnabled(LogLevel.Warning);
            bool collectPhaseTiming = ShouldCollectRefreshPhaseTiming(traceEnabled);
            bool collectElapsedTiming = ShouldCollectRefreshElapsedTiming(traceEnabled, debugEnabled, warningEnabled);
            long refreshStartTimestamp = 0;
            long phaseStartTimestamp = 0;
            double snapshotMs = 0;
            double diffMs = 0;
            double uiApplyMs = 0;

            if (collectElapsedTiming)
            {
                refreshStartTimestamp = Stopwatch.GetTimestamp();
            }

            if (collectPhaseTiming)
            {
                phaseStartTimestamp = Stopwatch.GetTimestamp();
            }

            int recentSnapshotCacheWindowMs = ResolveSnapshotCacheWindowMs(
                interactive,
                _hasCompletedFirstRefresh,
                RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                AppConstants.Timing.SessionSnapshotPrewarmReuseMs);
            int refreshGeneration = BeginRefreshGeneration();
            CancellationTokenSource nextRefreshCts = BeginRefreshCycle();
            var refreshToken = nextRefreshCts.Token;

            IReadOnlyList<AudioSessionSnapshot>? serviceSessions = null;
            HashSet<string>? incomingIds = null;
            List<(string Id, AudioSessionItem Item)>? toAdd = null;
            List<(string Id, float Volume, bool IsMuted)>? toUpdate = null;
            List<string>? idsToRemove = null;
            Dictionary<string, string>? sessionRowIdsByInstance = null;
            try
            {
                serviceSessions = await _audio.GetAllAudioSessionSnapshotsAsync(
                    _mixerMode,
                    recentSnapshotCacheWindowMs: recentSnapshotCacheWindowMs,
                    cancellationToken: refreshToken);
                if (collectPhaseTiming)
                {
                    snapshotMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                    phaseStartTimestamp = Stopwatch.GetTimestamp();
                }

                if (!RequiresActivationRefresh && ShouldSkipRefreshForRepeatedSnapshotReference(serviceSessions, _lastProcessedSnapshotEntries))
                {
                    return;
                }

                incomingIds = RentIncomingIdsSet(serviceSessions.Count);

                foreach (var session in serviceSessions)
                {
                    refreshToken.ThrowIfCancellationRequested();

                    string id = GetSessionIdentity(session, out var resolvedPid);
                    incomingIds.Add(id);
                    if (!string.IsNullOrWhiteSpace(session.SessionInstanceId))
                    {
                        sessionRowIdsByInstance ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        sessionRowIdsByInstance[session.SessionInstanceId] = id;
                    }

                    if (resolvedPid is uint mappedPid && mappedPid != 0 && !string.IsNullOrWhiteSpace(session.ProcessName))
                    {
                        _pidToProcessName[mappedPid] = session.ProcessName;
                        TrimPidToProcessMapIfNeeded();
                    }

                    if (!_sessionsById.TryGetValue(id, out var existingItem))
                    {
                        uint? pid = resolvedPid;
                        var item = CreateSessionItem(id, session, pid);

                        toAdd ??= RentToAddList();
                        toAdd.Add((id, item));
                        _userSetVolumes[GetUserVolumeKey(id)] = item.Volume;
                        continue;
                    }


                    existingItem.UpdateRoutingMetadataFromSystem(session.ProcessName, session.SessionInstanceId, session.EndpointId);

                    if (_throttleStates.TryGetValue(id, out var updateState))
                    {
                        lock (updateState.Lock)
                        {
                            if (updateState.HasPending)
                            {
                                continue;
                            }
                        }
                    }

                    var now = DateTime.UtcNow;
                    bool weJustSetIt = _lastVolumeSetByUs.TryGetValue(id, out var lastSetTime) &&
                        (now - lastSetTime) < _ourVolumeSetTtl;

                    float currentVolume = existingItem.Volume;
                    float systemVolume = session.Volume;
                    bool muteChanged = existingItem.IsMuted != session.IsMuted;
                    if ((!weJustSetIt && Math.Abs(currentVolume - systemVolume) > 0.1f) || muteChanged)
                    {
                        toUpdate ??= RentToUpdateList();
                        toUpdate.Add((id, systemVolume, session.IsMuted));
                        _userSetVolumes[GetUserVolumeKey(id)] = systemVolume;
                    }
                }

                refreshToken.ThrowIfCancellationRequested();

                int addedCount = toAdd?.Count ?? 0;
                bool shouldScanForRemovals = ShouldScanForRemovedSessions(_sessionsById.Count, incomingIds.Count, addedCount);
                if (shouldScanForRemovals)
                {
                    foreach (var existingId in _sessionsById.Keys)
                    {
                        if (!incomingIds.Contains(existingId))
                        {
                            idsToRemove ??= RentIdsToRemoveList();
                            idsToRemove.Add(existingId);
                        }
                    }
                }

                refreshToken.ThrowIfCancellationRequested();

                if (collectPhaseTiming)
                {
                    diffMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                    phaseStartTimestamp = Stopwatch.GetTimestamp();
                }

                bool refreshApplied = await ApplyRefreshResultsAsync(refreshGeneration, idsToRemove, toAdd, toUpdate, refreshToken);
                if (refreshApplied)
                {
                    ReplaceSessionInstanceMappings(sessionRowIdsByInstance);
                    _lastProcessedSnapshotEntries = serviceSessions;
                    ClearActivationRefreshRequirement();
                }

                if (collectPhaseTiming)
                {
                    uiApplyMs = Stopwatch.GetElapsedTime(phaseStartTimestamp).TotalMilliseconds;
                }

            }
            catch (OperationCanceledException)
            {
                _logger.Trace("MixerViewModel", AppConstants.Audio.LogEvents.ViewModel.Mixer.RefreshCancelled);
            }
            catch (Exception ex)
            {
                _logger.Error("MixerViewModel", () => $"mixer-refresh-failed | error={ex.GetType().Name}", nameof(RefreshAsync), ex);
            }
            finally
            {
                double elapsedMs = collectElapsedTiming
                    ? Stopwatch.GetElapsedTime(refreshStartTimestamp).TotalMilliseconds
                    : 0;

                if (traceEnabled)
                {
                    _logger.Trace("MixerViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.Mixer.RefreshComplete} | totalMs={elapsedMs:F1} sessionCount={Sessions.Count} snapshotMs={snapshotMs:F1} diffMs={diffMs:F1} uiMs={uiApplyMs:F1}");
                }

                if (ShouldRecordRefreshMetrics(debugEnabled))
                {
                    RecordRefreshMetric(elapsedMs);
                }

                if (elapsedMs >= SlowRefreshWarningMs)
                {
                    _consecutiveSlowRefreshes++;
                    if (_hasCompletedFirstRefresh &&
                        _consecutiveSlowRefreshes >= SlowRefreshWarningConsecutiveCount &&
                        warningEnabled)
                    {
                        _logger.Warning("MixerViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.Mixer.RefreshSlow} | elapsedMs={elapsedMs:F1} consecutive={_consecutiveSlowRefreshes}");
                    }
                }
                else
                {
                    _consecutiveSlowRefreshes = 0;
                }

                _hasCompletedFirstRefresh = true;

                RecordCacheWindowDiagnostics(interactive, recentSnapshotCacheWindowMs);


                ReturnIncomingIdsSet(incomingIds);
                ReturnToAddList(toAdd);
                ReturnToUpdateList(toUpdate);
                ReturnIdsToRemoveList(idsToRemove);
                LeaveRefreshSettlementCycle(refreshSettlementSource);

            }
        }

        internal static bool ShouldCollectRefreshPhaseTiming(bool traceEnabled)
        {
            return traceEnabled;
        }

        internal static bool ShouldCollectRefreshElapsedTiming(bool traceEnabled, bool debugEnabled, bool warningEnabled)
        {
            return traceEnabled || debugEnabled || warningEnabled;
        }

        internal static bool ShouldRecordRefreshMetrics(bool debugEnabled)
        {
            return debugEnabled;
        }

        internal static bool ShouldSkipRefreshForRepeatedSnapshotReference(
            IReadOnlyList<AudioSessionSnapshot>? currentSnapshot,
            IReadOnlyList<AudioSessionSnapshot>? previousSnapshot)
        {
            return currentSnapshot != null && previousSnapshot != null && ReferenceEquals(currentSnapshot, previousSnapshot);
        }

        internal async Task<bool> ApplyRefreshResultsAsync(
            int refreshGeneration,
            List<string>? idsToRemove,
            List<(string Id, AudioSessionItem Item)>? toAdd,
            List<(string Id, float Volume, bool IsMuted)>? toUpdate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanApplyRefreshResults(refreshGeneration))
            {
                LogDroppedStaleRefresh(refreshGeneration);
                return false;
            }

            if (idsToRemove == null && (toAdd?.Count ?? 0) == 0 && (toUpdate?.Count ?? 0) == 0)
            {
                return true;
            }

            if (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return false;
            }

            try
            {
                bool applied = await _dispatcher.InvokeAsync(() =>
                {
                    if (!CanApplyRefreshResults(refreshGeneration))
                    {
                        LogDroppedStaleRefresh(refreshGeneration);
                        return false;
                    }

                    if (idsToRemove != null)
                    {
                        foreach (var id in idsToRemove)
                        {
                            if (_sessionsById.TryRemove(id, out var existingItem))
                            {
                                DetachVolumeChangedHandlerIfNeeded(id, existingItem);
                                Sessions.Remove(existingItem);

                                _userSetVolumes.TryRemove(GetUserVolumeKey(id), out _);
                                _lastVolumeSetByUs.TryRemove(id, out _);

                                if (_throttleStates.TryRemove(id, out var state))
                                {
                                    AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);
                                }

                                if (existingItem.ProcessId.HasValue)
                                {
                                    _pidToProcessName.TryRemove(existingItem.ProcessId.Value, out _);
                                }
                            }
                        }
                    }

                    if (toAdd != null)
                    {
                        List<AudioSessionItem>? itemsToInsert = null;

                        foreach (var (id, item) in toAdd)
                        {
                            AudioSessionItem resolvedItem = ResolveSharedSessionItemForAdd(id, item);
                            AttachVolumeChangedHandlerIfNeeded(id, resolvedItem);
                            _sessionsById[id] = resolvedItem;
                            itemsToInsert ??= [];
                            itemsToInsert.Add(resolvedItem);
                        }

                        if (itemsToInsert != null)
                        {
                            Sessions.InsertSortedRange(itemsToInsert, CompareSessionItems);
                        }
                    }

                    if (toUpdate != null)
                    {
                        foreach (var (id, volume, isMuted) in toUpdate)
                        {
                            if (_sessionsById.TryGetValue(id, out var item))
                            {
                                item.SetStateFromSystem(volume, isMuted);
                            }
                        }
                    }
                    return true;
                });
                return applied;
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("MixerViewModel", "Skipping refresh result application because dispatcher shutdown is in progress", nameof(ApplyRefreshResultsAsync), ex);
                return false;
            }

        }

        private static void CompleteRefreshSettlementCycle(TaskCompletionSource<object?> settlementSource)
        {
            settlementSource.TrySetResult(null);
        }

        private void RecordCacheWindowDiagnostics(bool interactive, int cacheWindowMs)
        {
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            int refreshCount = Interlocked.Increment(ref _cacheWindowDiagnosticsRefreshCount);
            if (interactive)
            {
                Interlocked.Increment(ref _interactiveRefreshCount);
            }
            else
            {
                Interlocked.Increment(ref _backgroundRefreshCount);
            }

            int logInterval = RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes;
            if (refreshCount != 1 && (refreshCount % logInterval) != 0)
            {
                return;
            }

            _logger.Debug(
                "MixerViewModel",
                () => $"{AppConstants.Audio.LogEvents.ViewModel.Mixer.CacheWindowDiagnostics} | refreshCount={refreshCount} interactiveRefreshes={Interlocked.CompareExchange(ref _interactiveRefreshCount, 0, 0)} backgroundRefreshes={Interlocked.CompareExchange(ref _backgroundRefreshCount, 0, 0)} activeWindowMs={cacheWindowMs}");
        }

        private void TrimPidToProcessMapIfNeeded()
        {
            int count = _pidToProcessName.Count;
            if (count <= MaxPidProcessMapEntries)
            {
                return;
            }

            int overflow = count - MaxPidProcessMapEntries;
            if (overflow <= 0)
            {
                return;
            }

            int removed = 0;
            foreach (var kvp in _pidToProcessName)
            {
                if (removed >= overflow)
                {
                    break;
                }

                if (_pidToProcessName.TryRemove(kvp.Key, out _))
                {
                    removed++;
                }
            }

            if (removed > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("MixerViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.Mixer.PidNameCacheTrim} | removed={removed} remaining={_pidToProcessName.Count}");
            }
        }

        private void RecordRefreshMetric(double elapsedMs)
        {
            lock (_refreshMetricsLock)
            {
                _refreshMetricsCount++;
                _refreshMetricsTotalMs += elapsedMs;
                _refreshMetricsMaxMs = Math.Max(_refreshMetricsMaxMs, elapsedMs);

                var now = DateTime.UtcNow;
                var windowElapsed = now - _refreshMetricsWindowStartUtc;
                TimeSpan refreshDiagnosticsWindow = TimeSpan.FromSeconds(RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds);
                if (windowElapsed < refreshDiagnosticsWindow)
                {
                    return;
                }

                if (_refreshMetricsCount > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    double avg = _refreshMetricsTotalMs / _refreshMetricsCount;
                    _logger.Debug("MixerViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.Mixer.RefreshDiagnostics} | count={_refreshMetricsCount} avgMs={avg:F1} maxMs={_refreshMetricsMaxMs:F1} windowSeconds={windowElapsed.TotalSeconds:F0}");
                }

                _refreshMetricsWindowStartUtc = now;
                _refreshMetricsCount = 0;
                _refreshMetricsTotalMs = 0;
                _refreshMetricsMaxMs = 0;
            }
        }

        private static int CompareSessionItems(AudioSessionItem a, AudioSessionItem b)
        {
            static int GetCategory(AudioSessionItem item)
            {
                if (item.IsMaster) return 0;
                if (item.IsMic) return 1;
                if (item.IsSystemSounds) return 2;
                return 3;
            }

            int categoryA = GetCategory(a);
            int categoryB = GetCategory(b);

            if (categoryA != categoryB)
                return categoryA.CompareTo(categoryB);

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        public void Refresh() => _ = RefreshAsync();

        internal void MarkActivationRefreshStale(string context)
        {
            if (_disposed || Sessions.Count == 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref _requiresActivationRefresh, 1) == 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("MixerViewModel", () => $"mixer-activation-refresh-stale | mode={_mixerMode} context={context} sessionCount={Sessions.Count}");
            }
        }

        internal void ClearActivationRefreshRequirement()
        {
            Interlocked.Exchange(ref _requiresActivationRefresh, 0);
        }

        public void TrimIdleState()
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _refreshGeneration);
            _lastProcessedSnapshotEntries = null;
            ClearActivationRefreshRequirement();

            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _refreshCts);

            foreach (var state in _throttleStates.Values)
            {
                lock (state.Lock)
                {
                    AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);
                    state.HasPending = false;
                }
            }

            _throttleStates.Clear();
            _pidToProcessName.Clear();
            _userSetVolumes.Clear();
            _lastVolumeSetByUs.Clear();

            foreach (var pair in _sessionsById)
            {
                DetachVolumeChangedHandlerIfNeeded(pair.Key, pair.Value);
            }

            _sessionsById.Clear();
            _sessionRowIdByInstanceId?.Clear();
            Sessions.Clear();

            ClearCollectionPools();
        }

        private bool CanApplyRefreshResults(int refreshGeneration)
        {
            return !_disposed && ShouldApplyRefreshResults(refreshGeneration, GetActiveRefreshGeneration());
        }

        private void ClearCollectionPools()
        {
            if (_incomingIdsPool == null || _toAddPool == null || _toUpdatePool == null || _idsToRemovePool == null)
            {
                return;
            }

            while (_incomingIdsPool.TryTake(out HashSet<string>? incomingIds))
            {
                incomingIds.Clear();
            }

            while (_toAddPool.TryTake(out List<(string Id, AudioSessionItem Item)>? toAdd))
            {
                toAdd.Clear();
            }

            while (_toUpdatePool.TryTake(out List<(string Id, float Volume, bool IsMuted)>? toUpdate))
            {
                toUpdate.Clear();
            }

            while (_idsToRemovePool.TryTake(out List<string>? idsToRemove))
            {
                idsToRemove.Clear();
            }
        }

        internal int BeginRefreshGeneration()
        {
            return Interlocked.Increment(ref _refreshGeneration);
        }

        internal int GetActiveRefreshGeneration()
        {
            return Volatile.Read(ref _refreshGeneration);
        }

        internal CancellationTokenSource BeginRefreshCycle()
        {
            return AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                nextRefreshCts => SwapRefreshTokenSource(ref _refreshCts, nextRefreshCts));
        }

        internal CancellationTokenSource? BeginRefreshCycle(CancellationTokenSource nextRefreshCts)
        {
            return SwapRefreshTokenSource(ref _refreshCts, nextRefreshCts);
        }

        internal static CancellationTokenSource? SwapRefreshTokenSource(ref CancellationTokenSource? current, CancellationTokenSource next)
        {
            return Interlocked.Exchange(ref current, next);
        }

        internal static int ResolveSnapshotCacheWindowMs(
            bool interactive,
            bool hasCompletedFirstRefresh,
            int interactiveCacheWindowMs,
            int backgroundCacheWindowMs,
            int prewarmReuseWindowMs)
        {
            int baseWindowMs = interactive ? interactiveCacheWindowMs : backgroundCacheWindowMs;
            if (!interactive || hasCompletedFirstRefresh)
            {
                return baseWindowMs;
            }

            return Math.Max(baseWindowMs, prewarmReuseWindowMs);
        }

        internal static bool ShouldApplyRefreshResults(int refreshGeneration, int activeRefreshGeneration)
        {
            return refreshGeneration == activeRefreshGeneration;
        }

        /// <summary>
        /// Determines whether a slider change should be applied immediately or deferred to trailing-edge throttle.
        /// </summary>
        internal static bool ShouldApplyVolumeImmediately(DateTime now, DateTime lastApplied, int throttleIntervalMs)
        {
            return (now - lastApplied).TotalMilliseconds >= throttleIntervalMs;
        }

        internal static bool ShouldUseTrailingEdgeOnly(AudioSessionItem item)
        {
            return item.IsMaster || item.IsMic;
        }

        internal static int ResolveTrailingApplyDelay(double elapsedMs, int throttleIntervalMs, bool trailingEdgeOnly)
        {
            if (trailingEdgeOnly)
            {
                return throttleIntervalMs;
            }

            return Math.Max(0, throttleIntervalMs - (int)elapsedMs + 5);
        }

        internal static bool ShouldAutoUnmuteOnVolumeDrag(bool isMuted, float previousVolume, float currentVolume)
        {
            return isMuted
                && previousVolume <= 0.01f
                && currentVolume > 0.01f;
        }

        internal static bool ShouldScanForRemovedSessions(int existingSessionCount, int incomingSessionCount, int addedSessionCount)
        {
            if (addedSessionCount > 0)
            {
                return true;
            }

            return existingSessionCount != incomingSessionCount;
        }

        private void OnItemVolumeChanged(AudioSessionItem item)
        {
            string id = GetSessionIdForItem(item);
            var now = DateTime.UtcNow;
            bool trailingEdgeOnly = ShouldUseTrailingEdgeOnly(item);
            string userVolumeKey = GetUserVolumeKey(id);
            float previousVolume = _userSetVolumes.TryGetValue(userVolumeKey, out float lastKnownUserVolume)
                ? lastKnownUserVolume
                : item.Volume;

            if (ShouldAutoUnmuteOnVolumeDrag(item.IsMuted, previousVolume, item.Volume))
            {
                item.SetMuteFromSystem(false);
                var cts = _muteApplyCts ??= new CancellationTokenSource();
                _ = ObserveMuteApplyAsync(
                    ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyMuteChange(item, isMuted: false)),
                    phase: "auto-unmute",
                    cancellationToken: cts.Token,
                    onSuccess: null,
                    onFailure: null);
            }

            float previousStoredVolume = _userSetVolumes.TryGetValue(userVolumeKey, out float storedVolume)
                ? storedVolume
                : item.Volume;

            _lastVolumeSetByUs[id] = now;
            _userSetVolumes[userVolumeKey] = item.Volume;

            var state = _throttleStates.GetOrAdd(id, _ => new ThrottleState());

            lock (state.Lock)
            {
                var elapsed = (now - state.LastApplied).TotalMilliseconds;

                if (!trailingEdgeOnly && ShouldApplyVolumeImmediately(now, state.LastApplied, ThrottleIntervalMs))
                {
                    state.LastApplied = now;
                    state.HasPending = false;
                    AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);

                    _ = ObserveVolumeApplyAsync(
                        ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyVolumeChange(item)),
                        phase: "leading-edge",
                        onSuccess: null,
                        onFailure: () => _userSetVolumes[userVolumeKey] = previousStoredVolume);
                }
                else
                {
                    state.PendingVolume = item.Volume;
                    state.HasPending = true;

                    if (trailingEdgeOnly)
                    {
                        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);
                    }

                    if (state.TrailingCts == null)
                    {
                        state.TrailingCts = new CancellationTokenSource();
                        CancellationTokenSource cts = state.TrailingCts;
                        var capturedItem = item;
                        int delay = ResolveTrailingApplyDelay(elapsed, ThrottleIntervalMs, trailingEdgeOnly);

                        _ = ObserveVolumeApplyAsync(
                            RunTrailingVolumeApplyAsync(id, capturedItem, delay, state, cts),
                            phase: "trailing-edge",
                            onSuccess: null,
                            onFailure: () => _userSetVolumes[userVolumeKey] = previousStoredVolume);
                    }
                }
            }
        }

        private void OnItemMuteChanged(AudioSessionItem item)
        {
            var cts = _muteApplyCts ??= new CancellationTokenSource();
            _ = ObserveMuteApplyAsync(
                ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyMuteChange(item, item.IsMuted)),
                phase: "item-mute-change",
                cancellationToken: cts.Token,
                onSuccess: null,
                onFailure: null);
        }

        private async Task<MixerMutationResult> RunTrailingVolumeApplyAsync(
            string id,
            AudioSessionItem item,
            int delayMs,
            ThrottleState state,
            CancellationTokenSource ownedDebounceCts)
        {
            MixerMutationResult result = MixerMutationResult.Failure();
            await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                ownedDebounceCts,
                current => ReleaseTrailingEdgeDebounce(state, current),
                async linkedToken =>
                {
                    await Task.Delay(delayMs, linkedToken);
                    result = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyTrailingEdge(id, item), linkedToken);
                },
                CancellationToken.None);
            return result;
        }

        private void LogDroppedStaleRefresh(int refreshGeneration)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("MixerViewModel", () => $"mixer-refresh-drop-stale | refreshGeneration={refreshGeneration} activeGeneration={GetActiveRefreshGeneration()}");
            }
        }

        private async Task ObserveVolumeApplyAsync(Task<MixerMutationResult> task, string phase, Action? onSuccess = null, Action? onFailure = null)
        {
            try
            {
                MixerMutationResult result = await task;
                if (result.IsSuccess) onSuccess?.Invoke();
                else HandleMutationFailure(phase, result, onFailure);
            }
            catch (OperationCanceledException)
            {
                onFailure?.Invoke();
            }
            catch (Exception ex)
            {
                onFailure?.Invoke();
                _logger.Warning("MixerViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.Mixer.VolumeApplyTaskFailed} | phase={phase}", nameof(ObserveVolumeApplyAsync), ex);
            }
        }

        /// <summary>
        /// Applies the most recent pending slider value after the throttle window elapses.
        /// </summary>
        private MixerMutationResult ApplyTrailingEdge(string id, AudioSessionItem item)
        {
            if (!_throttleStates.TryGetValue(id, out var state))
                return MixerMutationResult.Failure();

            float volumeToApply;
            lock (state.Lock)
            {
                if (!state.HasPending)
                    return MixerMutationResult.Failure();

                volumeToApply = state.PendingVolume;
                state.HasPending = false;
                state.LastApplied = DateTime.UtcNow;
            }

            var snapshot = new AudioSessionItem(
                item.DisplayName,
                volumeToApply,
                item.IsMaster,
                item.IsMic,
                item.IsSystemSounds,
                item.ProcessId,
                item.IsMuted,
                item.ProcessName,
                item.SessionInstanceId,
                item.EndpointId);

            return ApplyVolumeChange(snapshot);
        }

        public void ToggleMute(AudioSessionItem? item)
        {
            if (_disposed || item == null)
            {
                return;
            }

            bool nextMuteState = !item.IsMuted;
            bool originalMuteState = item.IsMuted;

            var cts = _muteApplyCts ??= new CancellationTokenSource();
            _ = ObserveMuteApplyAsync(
                ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                {
                    return ApplyMuteChange(item, nextMuteState);
                }),
                phase: "toggle",
                cancellationToken: cts.Token,
                onSuccess: () => item.SetMuteFromSystem(nextMuteState),
                onFailure: () => item.SetMuteFromSystem(originalMuteState));
        }

        /// <summary>
        /// Applies a volume update to master, microphone, system sounds, or process sessions.
        /// </summary>
        /// <remarks>
        /// COM failures fall back to device-cache refresh so stale session handles recover on subsequent cycles.
        /// </remarks>
        private MixerMutationResult ApplyVolumeChange(AudioSessionItem item)
        {
            try
            {
                float scalar = Math.Clamp(item.Volume / 100f, 0f, 1f);
                string sessionId = FormatSessionIdForLog(GetSessionIdForItem(item));

                if (item.IsMaster)
                {
                    using var dev = _audio.GetDefaultPlaybackDevice("mixer-apply-volume:master");
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=master reason=no-default-playback-device");
                        return MixerMutationResult.Failure();
                    }

                    if (!AudioDeviceHelper.TryGetEndpointVolume(_logger, dev, out var volume, "mixer-apply-volume:master"))
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=master reason=endpoint-volume-unavailable");
                        return MixerMutationResult.Failure();
                    }

                    volume.MasterVolumeLevelScalar = scalar;
                    return MixerMutationResult.Success();
                }

                if (item.IsMic)
                {
                    using var dev = _audio.GetDefaultRecordingDevice("mixer-apply-volume:recording");
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=microphone reason=no-default-recording-device");
                        return MixerMutationResult.Failure();
                    }

                    if (!AudioDeviceHelper.TryGetEndpointVolume(_logger, dev, out var volume, "mixer-apply-volume:recording"))
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=microphone reason=endpoint-volume-unavailable");
                        return MixerMutationResult.Failure();
                    }

                    volume.MasterVolumeLevelScalar = scalar;
                    return MixerMutationResult.Success();
                }

                if (item.IsSystemSounds)
                {
                    int updatedSystemSessions = AudioDeviceHelper.SetVolumeForSystemSounds(
                        _audio,
                        scalar,
                        _logger);

                    if (updatedSystemSessions <= 0)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=system-sounds reason=no-system-sound-session-updated");
                        _deviceCache.Refresh();
                        return MixerMutationResult.Failure();
                    }

                    return MixerMutationResult.Success(updatedSystemSessions);
                }

                uint? pid = GetProcessIdForItem(item);
                if (!pid.HasValue)
                {
                    _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=process reason=missing-pid");
                    return MixerMutationResult.Failure();
                }

                AudioDeviceHelper.SessionMutationResult mutation = AudioDeviceHelper.SetVolumeForSessionsByPid(
                    _audio,
                    pid.Value,
                    scalar,
                    _pidToProcessName,
                    _logger,
                    GetProcessSessionFlow(_mixerMode),
                    item.EndpointId,
                    item.SessionInstanceId);

                if (!mutation.IsCompleteSuccess)
                {
                    _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-incomplete | sessionId={sessionId} target=process pid={FormatProcessIdForLog(pid.Value)} attempted={mutation.Attempted} succeeded={mutation.Succeeded} failed={mutation.Failed} mode={_mixerMode}");
                    _deviceCache.Refresh();
                    return new MixerMutationResult(mutation.Attempted, mutation.Succeeded, mutation.Failed == 0 ? 1 : mutation.Failed);
                }

                if (_pidToProcessName.TryGetValue(pid.Value, out var processName))
                {
                    _audio.UpdateSessionVolumeCache(item.DisplayName, processName, item.Volume);
                }
                else
                {
                    _audio.UpdateSessionVolumeCache(item.DisplayName, null, item.Volume);
                }
                return new MixerMutationResult(mutation.Attempted, mutation.Succeeded, mutation.Failed);
            }
            catch (COMException)
            {
                _deviceCache.Refresh();
                return MixerMutationResult.Failure();
            }
            catch (Exception ex)
            {
                _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-failed | error={ex.GetType().Name}", nameof(ApplyVolumeChange), ex);
                return MixerMutationResult.Failure();
            }
        }

        private MixerMutationResult ApplyMuteChange(AudioSessionItem item, bool isMuted)
        {
            if (_disposed)
            {
                return MixerMutationResult.Failure();
            }

            string sessionId = FormatSessionIdForLog(GetSessionIdForItem(item));

            try
            {

                if (item.IsMaster)
                {
                    using var dev = _audio.GetDefaultPlaybackDevice("mixer-apply-mute:master");
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=master reason=no-default-playback-device");
                        return MixerMutationResult.Failure();
                    }

                    if (!AudioDeviceHelper.TryGetEndpointVolume(_logger, dev, out var volume, "mixer-apply-mute:master"))
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=master reason=endpoint-volume-unavailable");
                        return MixerMutationResult.Failure();
                    }

                    volume.Mute = isMuted;
                    return MixerMutationResult.Success();
                }

                if (item.IsMic)
                {
                    using var dev = _audio.GetDefaultRecordingDevice("mixer-apply-mute:recording");
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=microphone reason=no-default-recording-device");
                        return MixerMutationResult.Failure();
                    }

                    if (!AudioDeviceHelper.TryGetEndpointVolume(_logger, dev, out var volume, "mixer-apply-mute:recording"))
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=microphone reason=endpoint-volume-unavailable");
                        return MixerMutationResult.Failure();
                    }

                    volume.Mute = isMuted;
                    return MixerMutationResult.Success();
                }

                if (item.IsSystemSounds)
                {
                    int updatedSystemSessions = AudioDeviceHelper.SetMuteForSystemSounds(
                        _audio,
                        isMuted,
                        _logger);

                    if (updatedSystemSessions <= 0)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=system-sounds reason=no-session-updated");
                        _deviceCache.Refresh();
                        return MixerMutationResult.Failure();
                    }

                    return MixerMutationResult.Success(updatedSystemSessions);
                }

                uint? pid = GetProcessIdForItem(item);
                if (!pid.HasValue)
                {
                    _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=process reason=missing-pid");
                    return MixerMutationResult.Failure();
                }

                AudioDeviceHelper.SessionMutationResult mutation = AudioDeviceHelper.SetMuteForSessionsByPid(
                    _audio,
                    pid.Value,
                    isMuted,
                    _pidToProcessName,
                    _logger,
                    GetProcessSessionFlow(_mixerMode),
                    item.EndpointId,
                    item.SessionInstanceId);

                if (!mutation.IsCompleteSuccess)
                {
                    _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-incomplete | sessionId={sessionId} target=process pid={FormatProcessIdForLog(pid.Value)} attempted={mutation.Attempted} succeeded={mutation.Succeeded} failed={mutation.Failed} mode={_mixerMode}");
                    _deviceCache.Refresh();
                    return new MixerMutationResult(mutation.Attempted, mutation.Succeeded, mutation.Failed == 0 ? 1 : mutation.Failed);
                }
                return new MixerMutationResult(mutation.Attempted, mutation.Succeeded, mutation.Failed);
            }
            catch (COMException ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("MixerViewModel", () => $"mixer-mute-apply-com-failed | sessionId={sessionId} hresult=0x{ex.HResult:X8}");
                }
                _deviceCache.Refresh();
                return MixerMutationResult.Failure();
            }
            catch (Exception ex)
            {
                _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-failed | error={ex.GetType().Name}", nameof(ApplyMuteChange), ex);
                return MixerMutationResult.Failure();
            }
        }

        public void Cleanup()
        {
            _disposed = true;
            Interlocked.Increment(ref _refreshGeneration);
            _lastProcessedSnapshotEntries = null;
            ClearActivationRefreshRequirement();

            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _refreshCts);
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _muteApplyCts);

            if (_throttleStates != null)
            {
                foreach (var state in _throttleStates.Values)
                {
                    lock (state.Lock)
                    {
                        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);
                    }
                }

                _throttleStates.Clear();
            }

            _pidToProcessName?.Clear();
            _userSetVolumes?.Clear();
            _lastVolumeSetByUs?.Clear();

            if (_sessionsById != null)
            {
                foreach (var pair in _sessionsById)
                {
                    DetachVolumeChangedHandlerIfNeeded(pair.Key, pair.Value);
                }

                _sessionsById.Clear();
            }

            _sessionRowIdByInstanceId?.Clear();

            Sessions?.Clear();
            ClearCollectionPools();
            _sharedSessionBridge = null;
        }

        internal static DataFlow GetProcessSessionFlow(AudioMixerMode mixerMode)
        {
            return mixerMode == AudioMixerMode.Input ? DataFlow.Capture : DataFlow.Render;
        }

        private static void ReleaseTrailingEdgeDebounce(ThrottleState state, CancellationTokenSource ownedDebounceCts)
        {
            lock (state.Lock)
            {
                AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref state.TrailingCts, ownedDebounceCts);
            }
        }
    }
}
