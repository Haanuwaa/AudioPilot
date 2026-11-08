using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        DeviceCacheHelper? deviceCache = null) : INotifyPropertyChanged
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
        private int _idleTrimGeneration;
        private volatile bool _idleTrimRequested;
        private Task<MixerMutationResult>? _idleVolumeFlushTask;
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
            public long LastAppliedTimestamp;
            public float PendingVolume;
            public MixerRowEdit PendingEdit;
            public bool HasPending;
            public CancellationTokenSource? TrailingCts;
            public readonly Lock Lock = new();
        }

        private async Task ObserveMuteApplyAsync(Task<MixerMutationResult> task, string phase, CancellationToken cancellationToken)
        {
            try
            {
                MixerMutationResult result = await task.WaitAsync(cancellationToken);
                if (_disposed) return;
                if (!result.IsSuccess && (result.Attempted > 0 || result.Failed > 0)) HandleMutationFailure(phase, result);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-task-failed | phase={phase}", nameof(ObserveMuteApplyAsync), ex);
                HandleMutationFailure(phase, MixerMutationResult.Failure());
            }
        }

        private void HandleMutationFailure(string phase, MixerMutationResult result)
        {
            if (_disposed) return;
            _logger.Warning("MixerViewModel", () => $"mixer-mutation-not-applied | phase={phase} attempted={result.Attempted} succeeded={result.Succeeded} failed={result.Failed}");
            MarkActivationRefreshStale($"mutation-failed:{phase}");
            _audio?.InvalidateRecentMixerSnapshotState();
            if (!_idleTrimRequested) _ = RefreshAsync(interactive: true);
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

            if (outputMixer._sharedSessionBridge != null
                && ReferenceEquals(outputMixer._sharedSessionBridge, inputMixer._sharedSessionBridge))
            {
                return;
            }

            var bridge = new MixerSharedSessionCoordinator(AudioMixerMode.Output);
            outputMixer.AdoptSharedSessionCoordinator(bridge);
            inputMixer.AdoptSharedSessionCoordinator(bridge);
        }

        /// <summary>Transfers existing rows and their subscriptions when the other mixer is initialized later.</summary>
        private void AdoptSharedSessionCoordinator(MixerSharedSessionCoordinator bridge)
        {
            KeyValuePair<string, AudioSessionItem>[] existingRows = [.. _sessionsById.Where(static pair => IsSharedSessionId(pair.Key))];
            foreach (var (id, item) in existingRows)
            {
                DetachVolumeChangedHandlerIfNeeded(id, item);
            }

            _sharedSessionBridge = bridge;
            foreach (var (id, item) in existingRows)
            {
                AudioSessionItem sharedItem = ResolveSharedSessionItemForAdd(id, item);
                _sessionsById[id] = sharedItem;
                if (!ReferenceEquals(item, sharedItem))
                {
                    int index = Sessions.IndexOf(item);
                    if (index >= 0) { Sessions[index] = sharedItem; }
                }
                AttachVolumeChangedHandlerIfNeeded(id, sharedItem);
            }
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

        private static string GetSessionIdentity(AudioSessionSnapshot session, out uint? processId)
        {
            processId = null;
            string displayName = session.DisplayName;

            if (session.ProcessId.HasValue && session.ProcessId.Value != 0)
            {
                processId = session.ProcessId.Value;
                return $"pid:{session.ProcessId.Value}";
            }

            if (displayName.StartsWith("Master Volume", StringComparison.OrdinalIgnoreCase))
                return "master:primary";

            if (displayName.StartsWith("Microphone Volume", StringComparison.OrdinalIgnoreCase))
                return "mic:primary";

            if (string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase))
                return "system:sounds";

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

        private static AudioSessionItem CreateSessionItem(AudioSessionSnapshot session, uint? processId)
        {
            string displayName = session.DisplayName ?? "Unknown";
            bool isMaster = processId.GetValueOrDefault() == 0 && displayName.StartsWith("Master Volume", StringComparison.OrdinalIgnoreCase);
            bool isMic = processId.GetValueOrDefault() == 0 && displayName.StartsWith("Microphone Volume", StringComparison.OrdinalIgnoreCase);
            bool isSystemSounds = processId.GetValueOrDefault() == 0 && string.Equals(displayName, "System Sounds", StringComparison.OrdinalIgnoreCase);

            var item = new AudioSessionItem(displayName, session.Volume, isMaster, isMic, isSystemSounds, processId, session.IsMuted, session.ProcessName, session.SessionInstanceId, session.EndpointId, session.SessionCount);
            if (item.IsApplication)
            {
                try { using var process = Process.GetProcessById(checked((int)processId!.Value)); item.ProcessStartedTicks = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
            }
            return item;
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
            ResetMixerInteractions(shutdown: false);
            if (_disposed) return;
            int trimGeneration = Interlocked.Increment(ref _idleTrimGeneration);
            _idleTrimRequested = true;
            Interlocked.Increment(ref _refreshGeneration);
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _refreshCts);
            List<(MixerRowEdit Edit, float Volume)>? edits = null;
            foreach (var pair in _throttleStates)
            {
                ThrottleState state = pair.Value;
                lock (state.Lock)
                {
                    if (state.HasPending && IsCurrentRowEdit(state.PendingEdit, mute: false))
                    {
                        (edits ??= []).Add((state.PendingEdit, state.PendingVolume));
                        state.HasPending = false;
                        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);
                    }
                }
            }
            if (edits == null && !_sessionsById.Any(static pair => pair.Value.HasPendingVolumeEdit || pair.Value.HasPendingMuteEdit)
                && Volatile.Read(ref _idleVolumeFlushTask) is not { IsCompleted: false })
            {
                TrimIdleStateCore();
                return;
            }
            Task<MixerMutationResult> flush = ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                MixerMutationResult total = default;
                if (edits != null)
                {
                    foreach ((MixerRowEdit edit, float volume) in edits)
                    {
                        MixerMutationResult result = ApplyQueuedVolumeEdit(edit, volume);
                        total = new(total.Attempted + result.Attempted, total.Succeeded + result.Succeeded, total.Failed + result.Failed);
                    }
                }
                return total;
            });
            _ = Interlocked.Exchange(ref _idleVolumeFlushTask, flush);
            _ = CompleteIdleVolumeFlushAsync(flush, edits, trimGeneration);
        }

        internal void CancelIdleTrim()
        {
            _idleTrimRequested = false;
            Interlocked.Increment(ref _idleTrimGeneration);
        }

        private async Task CompleteIdleVolumeFlushAsync(Task<MixerMutationResult> flush,
            List<(MixerRowEdit Edit, float Volume)>? edits, int trimGeneration)
        {
            MixerMutationResult result = default;
            try { result = await flush.ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    _logger.Warning("MixerViewModel", "mixer-idle-volume-flush-failed", nameof(CompleteIdleVolumeFlushAsync), ex);
                    result = MixerMutationResult.Failure();
                }
            }
            finally
            {
                if (edits != null)
                    foreach ((MixerRowEdit edit, _) in edits) edit.Item.CompleteVolumeEdit(edit.Revision);
                _ = Interlocked.CompareExchange(ref _idleVolumeFlushTask, null, flush);
            }
            if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_disposed) return;
                    bool stillIdle = trimGeneration == Volatile.Read(ref _idleTrimGeneration);
                    if (!result.IsSuccess && (result.Attempted > 0 || result.Failed > 0))
                    {
                        if (!stillIdle && !_idleTrimRequested) HandleMutationFailure("idle-flush", result);
                        else _logger.Warning("MixerViewModel", () => $"mixer-idle-volume-not-applied | attempted={result.Attempted} succeeded={result.Succeeded} failed={result.Failed}");
                    }
                    if (stillIdle) TrimIdleStateCore();
                }).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) { }
            catch (Exception ex)
            {
                if (!_disposed) _logger.Warning("MixerViewModel", "mixer-idle-trim-failed", nameof(CompleteIdleVolumeFlushAsync), ex);
            }
        }

        private void TrimIdleStateCore()
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _refreshGeneration);
            _lastProcessedSnapshotEntries = null;
            ClearActivationRefreshRequirement();

            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _refreshCts);
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _muteApplyCts);

            foreach (var state in _throttleStates.Values)
            {
                lock (state.Lock)
                {
                    state.PendingEdit.Item?.CompleteVolumeEdit(state.PendingEdit.Revision);
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

        /// <summary>
        /// Determines whether a slider change should be applied immediately or deferred to trailing-edge throttle.
        /// </summary>
        internal static bool ShouldApplyVolumeImmediately(double elapsedMs, int throttleIntervalMs)
        {
            return elapsedMs >= throttleIntervalMs;
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
            if (_disposed) return;
            PropagateGroupVolume(item);
            CancelIdleTrim();
            string id = GetSessionIdForItem(item);
            long now = Stopwatch.GetTimestamp();
            bool trailingEdgeOnly = ShouldUseTrailingEdgeOnly(item);
            float previousVolume = _userSetVolumes.TryGetValue(id, out float lastKnownUserVolume)
                ? lastKnownUserVolume
                : item.Volume;

            if (ShouldAutoUnmuteOnVolumeDrag(item.IsMuted, previousVolume, item.Volume))
            {
                item.IsMuted = false;
            }

            float requestedVolume = item.Volume;
            var edit = new MixerRowEdit(item, item.EndpointId, item.VolumeEditRevision);
            item.BeginVolumeEdit(edit.Revision);
            _lastVolumeSetByUs[id] = DateTime.UtcNow;
            _userSetVolumes[id] = item.Volume;

            var state = _throttleStates.GetOrAdd(id, _ => new ThrottleState());

            lock (state.Lock)
            {
                double elapsed = state.LastAppliedTimestamp == 0 ? double.PositiveInfinity
                    : Stopwatch.GetElapsedTime(state.LastAppliedTimestamp, now).TotalMilliseconds;

                if (!trailingEdgeOnly && ShouldApplyVolumeImmediately(elapsed, ThrottleIntervalMs))
                {
                    state.LastAppliedTimestamp = now;
                    state.HasPending = false;
                    state.PendingEdit.Item?.CompleteVolumeEdit(state.PendingEdit.Revision);
                    AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);

                    _ = ObserveVolumeApplyAsync(
                        CompleteRowEditAsync(ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyQueuedVolumeEdit(edit, requestedVolume)), edit, mute: false),
                        phase: "leading-edge");
                }
                else
                {
                    state.PendingVolume = item.Volume;
                    state.PendingEdit = edit;
                    state.HasPending = true;

                    if (state.TrailingCts == null)
                    {
                        state.TrailingCts = new CancellationTokenSource();
                        CancellationTokenSource cts = state.TrailingCts;
                        int delay = ResolveTrailingApplyDelay(elapsed, ThrottleIntervalMs, trailingEdgeOnly);

                        _ = ObserveVolumeApplyAsync(
                            RunTrailingVolumeApplyAsync(id, delay, state, cts),
                            phase: "trailing-edge");
                    }
                }
            }
        }

        private void OnItemMuteChanged(AudioSessionItem item)
        {
            if (_disposed) return;
            CancelIdleTrim();
            var cts = _muteApplyCts ??= new CancellationTokenSource();
            bool requestedMute = item.IsMuted;
            long endpointMuteRevision = item.IsMaster || item.IsMic ? _audio?.RecordEndpointMuteIntent(item.IsMaster) ?? -1 : -1;
            var edit = new MixerRowEdit(item, item.EndpointId, item.MuteEditRevision, endpointMuteRevision);
            item.BeginMuteEdit(edit.Revision);
            _ = ObserveMuteApplyAsync(
                CompleteRowEditAsync(ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyQueuedMuteEdit(edit, requestedMute), cts.Token), edit, mute: true),
                phase: "item-mute-change",
                cancellationToken: cts.Token);
        }

        private async Task<MixerMutationResult> RunTrailingVolumeApplyAsync(
            string id,
            int delayMs,
            ThrottleState state,
            CancellationTokenSource ownedDebounceCts)
        {
            MixerMutationResult result = default;
            await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                ownedDebounceCts,
                current => ReleaseTrailingEdgeDebounce(state, current),
                async linkedToken =>
                {
                    await Task.Delay(delayMs, linkedToken);
                    result = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyTrailingEdge(id, ownedDebounceCts), linkedToken);
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

        private async Task ObserveVolumeApplyAsync(Task<MixerMutationResult> task, string phase)
        {
            try
            {
                MixerMutationResult result = await task;
                if (_disposed) return;
                if (!result.IsSuccess && (result.Attempted > 0 || result.Failed > 0)) HandleMutationFailure(phase, result);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                _logger.Warning("MixerViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.Mixer.VolumeApplyTaskFailed} | phase={phase}", nameof(ObserveVolumeApplyAsync), ex);
                HandleMutationFailure(phase, MixerMutationResult.Failure());
            }
        }

        /// <summary>
        /// Applies the most recent pending slider value after the throttle window elapses.
        /// </summary>
        private MixerMutationResult ApplyTrailingEdge(string id, CancellationTokenSource ownedDebounceCts)
        {
            if (!_throttleStates.TryGetValue(id, out var state))
                return default;

            float volumeToApply;
            MixerRowEdit edit;
            lock (state.Lock)
            {
                if (!ReferenceEquals(state.TrailingCts, ownedDebounceCts))
                    return default;

                state.TrailingCts = null;
                if (!state.HasPending)
                    return default;

                volumeToApply = state.PendingVolume;
                edit = state.PendingEdit;
                state.HasPending = false;
                state.LastAppliedTimestamp = Stopwatch.GetTimestamp();
            }

            try { return ApplyQueuedVolumeEdit(edit, volumeToApply); }
            finally { edit.Item?.CompleteVolumeEdit(edit.Revision); }
        }

        public void ToggleMute(AudioSessionItem? item)
        {
            if (_disposed || item == null)
            {
                return;
            }

            bool muted = !item.IsMuted;
            if (CanGroupApplications && item.IsGroupSelected && IsCurrentInteractionRow(item) && SelectedApplicationCount > 1)
            {
                foreach (var selected in _selectedApplications!)
                    if (IsCurrentInteractionRow(selected)) selected.IsMuted = muted;
            }
            else item.IsMuted = muted;
        }

        /// <summary>
        /// Applies a volume update to master, microphone, system sounds, or process sessions.
        /// </summary>
        /// <remarks>
        /// COM failures fall back to device-cache refresh so stale session handles recover on subsequent cycles.
        /// </remarks>
        private MixerMutationResult ApplyVolumeChange(AudioSessionItem item, float requestedVolume, string endpointId)
        {
            if (_disposed) return default;

            try
            {
                if (!IsApplicationProcessCurrent(item)) return MixerMutationResult.Failure();
                float scalar = Math.Clamp(requestedVolume / 100f, 0f, 1f);
                string sessionId = FormatSessionIdForLog(GetSessionIdForItem(item));

                if (item.IsMaster)
                {
                    using var dev = _audio.GetDefaultPlaybackDevice();
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=master reason=no-default-playback-device");
                        return MixerMutationResult.Failure();
                    }

                    if (string.IsNullOrWhiteSpace(endpointId) || !string.Equals(dev.ID, endpointId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                        return MixerMutationResult.Failure();

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
                    using var dev = _audio.GetDefaultRecordingDevice();
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-skipped | sessionId={sessionId} target=microphone reason=no-default-recording-device");
                        return MixerMutationResult.Failure();
                    }

                    if (string.IsNullOrWhiteSpace(endpointId) || !string.Equals(dev.ID, endpointId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                        return MixerMutationResult.Failure();

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
                    GetProcessSessionFlow(_mixerMode));

                if (!mutation.IsCompleteSuccess)
                {
                    _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-incomplete | sessionId={sessionId} target=process pid={FormatProcessIdForLog(pid.Value)} attempted={mutation.Attempted} succeeded={mutation.Succeeded} failed={mutation.Failed} mode={_mixerMode}");
                    _deviceCache.Refresh();
                    return new MixerMutationResult(mutation.Attempted, mutation.Succeeded, mutation.Failed == 0 ? 1 : mutation.Failed);
                }

                _logger.Trace("MixerViewModel", () => $"mixer-process-volume-applied | pid={FormatProcessIdForLog(pid.Value)} mode={_mixerMode} rowEndpoint={LogPrivacy.Id(item.EndpointId)} rowInstance={LogPrivacy.Id(item.SessionInstanceId)} sessions={mutation.Succeeded} requested={scalar}");

                if (_pidToProcessName.TryGetValue(pid.Value, out var processName))
                {
                    _audio.UpdateSessionVolumeCache(item.DisplayName, processName, scalar * 100f, _mixerMode, pid.Value);
                }
                else
                {
                    _audio.UpdateSessionVolumeCache(item.DisplayName, null, scalar * 100f, _mixerMode, pid.Value);
                }
                return new MixerMutationResult(mutation.Attempted, mutation.Succeeded, mutation.Failed);
            }
            catch (COMException ex)
            {
                _logger.Trace("MixerViewModel", () => $"mixer-volume-apply-com-failed | sessionId={FormatSessionIdForLog(GetSessionIdForItem(item))} hresult=0x{ex.HResult:X8}");
                _deviceCache.Refresh();
                return MixerMutationResult.Failure();
            }
            catch (Exception ex)
            {
                _logger.Warning("MixerViewModel", () => $"mixer-volume-apply-failed | error={ex.GetType().Name}", nameof(ApplyVolumeChange), ex);
                return MixerMutationResult.Failure();
            }
        }

        private MixerMutationResult ApplyMuteChange(AudioSessionItem item, bool isMuted, string endpointId, long? intentRevision)
        {
            if (_disposed)
            {
                return default;
            }

            string sessionId = FormatSessionIdForLog(GetSessionIdForItem(item));

            try
            {
                if (!IsApplicationProcessCurrent(item)) return MixerMutationResult.Failure();
                if (item.IsMaster)
                {
                    using var dev = _audio.GetDefaultPlaybackDevice();
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=master reason=no-default-playback-device");
                        return MixerMutationResult.Failure();
                    }

                    if (string.IsNullOrWhiteSpace(endpointId) || !string.Equals(dev.ID, endpointId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                        return MixerMutationResult.Failure();

                    if (!AudioDeviceHelper.TryGetEndpointVolume(_logger, dev, out var volume, "mixer-apply-mute:master"))
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=master reason=endpoint-volume-unavailable");
                        return MixerMutationResult.Failure();
                    }

                    if (intentRevision.HasValue)
                    {
                        if (intentRevision.Value != _audio.GetEndpointMuteIntentRevision(item.IsMaster)) return MixerMutationResult.Failure();
                    }
                    else _audio.RecordEndpointMuteIntent(item.IsMaster);
                    volume.Mute = isMuted;
                    return MixerMutationResult.Success();
                }

                if (item.IsMic)
                {
                    using var dev = _audio.GetDefaultRecordingDevice();
                    if (dev == null)
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=microphone reason=no-default-recording-device");
                        return MixerMutationResult.Failure();
                    }

                    if (string.IsNullOrWhiteSpace(endpointId) || !string.Equals(dev.ID, endpointId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                        return MixerMutationResult.Failure();

                    if (!AudioDeviceHelper.TryGetEndpointVolume(_logger, dev, out var volume, "mixer-apply-mute:recording"))
                    {
                        _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-skipped | sessionId={sessionId} target=microphone reason=endpoint-volume-unavailable");
                        return MixerMutationResult.Failure();
                    }

                    if (intentRevision.HasValue)
                    {
                        if (intentRevision.Value != _audio.GetEndpointMuteIntentRevision(item.IsMaster)) return MixerMutationResult.Failure();
                    }
                    else _audio.RecordEndpointMuteIntent(item.IsMaster);
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
                    GetProcessSessionFlow(_mixerMode));

                if (!mutation.IsCompleteSuccess)
                {
                    _logger.Warning("MixerViewModel", () => $"mixer-mute-apply-incomplete | sessionId={sessionId} target=process pid={FormatProcessIdForLog(pid.Value)} attempted={mutation.Attempted} succeeded={mutation.Succeeded} failed={mutation.Failed} mode={_mixerMode}");
                    _deviceCache.Refresh();
                    return new MixerMutationResult(mutation.Attempted, mutation.Succeeded, mutation.Failed == 0 ? 1 : mutation.Failed);
                }

                _logger.Trace("MixerViewModel", () => $"mixer-process-mute-applied | pid={FormatProcessIdForLog(pid.Value)} mode={_mixerMode} rowEndpoint={LogPrivacy.Id(item.EndpointId)} rowInstance={LogPrivacy.Id(item.SessionInstanceId)} sessions={mutation.Succeeded} requested={isMuted}");
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
            ResetMixerInteractions(shutdown: true);
            _disposed = true;
            CancelIdleTrim();
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
                        state.PendingEdit.Item?.CompleteVolumeEdit(state.PendingEdit.Revision);
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
                if (ReferenceEquals(state.TrailingCts, ownedDebounceCts))
                {
                    state.HasPending = false;
                    state.PendingEdit.Item?.CompleteVolumeEdit(state.PendingEdit.Revision);
                }
                AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref state.TrailingCts, ownedDebounceCts);
            }
        }
    }
}
