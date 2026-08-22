using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class MixerViewModel
    {
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
            bool collectPhaseTiming = traceEnabled;
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

            IReadOnlyList<AudioSessionSnapshot>? serviceSessions = null;
            HashSet<string>? incomingIds = null;
            List<(string Id, AudioSessionItem Item)>? toAdd = null;
            List<(string Id, float Volume, bool IsMuted)>? toUpdate = null;
            List<string>? idsToRemove = null;
            Dictionary<string, string>? sessionRowIdsByInstance = null;
            Dictionary<string, MixerRowEditState>? editStates = null;
            try
            {
                CancellationTokenSource nextRefreshCts = BeginRefreshCycle();
                CancellationToken refreshToken;
                try { refreshToken = nextRefreshCts.Token; }
                catch (ObjectDisposedException) { return; }
                refreshToken.ThrowIfCancellationRequested();
                editStates = CaptureRowEditStates();
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
                        bool knownPid = _pidToProcessName.TryGetValue(mappedPid, out string? cachedProcessName);
                        if (!knownPid || !string.Equals(cachedProcessName, session.ProcessName, StringComparison.Ordinal))
                        {
                            _pidToProcessName[mappedPid] = session.ProcessName;
                        }
                        if (!knownPid)
                        {
                            TrimPidToProcessMapIfNeeded();
                        }
                    }

                    if (!_sessionsById.TryGetValue(id, out var existingItem))
                    {
                        uint? pid = resolvedPid;
                        var item = CreateSessionItem(session, pid);

                        toAdd ??= RentToAddList();
                        toAdd.Add((id, item));
                        continue;
                    }

                    if (existingItem.ProcessStartedTicks.HasValue && !string.Equals(existingItem.SessionInstanceId, session.SessionInstanceId, StringComparison.Ordinal)
                        && !IsApplicationProcessCurrent(existingItem))
                    {
                        idsToRemove ??= RentIdsToRemoveList();
                        idsToRemove.Add(id);
                        toAdd ??= RentToAddList();
                        toAdd.Add((id, CreateSessionItem(session, resolvedPid)));
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    bool weJustSetIt = !RequiresActivationRefresh && _lastVolumeSetByUs.TryGetValue(id, out var lastSetTime) &&
                        (now - lastSetTime) < _ourVolumeSetTtl;

                    float currentVolume = existingItem.Volume;
                    float systemVolume = session.Volume;
                    bool muteChanged = existingItem.IsMuted != session.IsMuted;
                    if ((!weJustSetIt && Math.Abs(currentVolume - systemVolume) > 0.1f) || muteChanged)
                    {
                        toUpdate ??= RentToUpdateList();
                        toUpdate.Add((id, weJustSetIt ? currentVolume : systemVolume, session.IsMuted));
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

                await ApplyRefreshResultsAsync(refreshGeneration, idsToRemove, toAdd, toUpdate, refreshToken,
                    serviceSessions, sessionRowIdsByInstance, editStates);

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

                if (debugEnabled)
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
                ReturnRowEditStates(editStates, refreshGeneration);
                LeaveRefreshSettlementCycle(refreshSettlementSource);

            }
        }

        internal static bool ShouldCollectRefreshElapsedTiming(bool traceEnabled, bool debugEnabled, bool warningEnabled)
        {
            return traceEnabled || debugEnabled || warningEnabled;
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
            CancellationToken cancellationToken,
            IReadOnlyList<AudioSessionSnapshot>? snapshot = null,
            Dictionary<string, string>? sessionInstanceMappings = null,
            Dictionary<string, MixerRowEditState>? editStates = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanApplyRefreshResults(refreshGeneration))
            {
                LogDroppedStaleRefresh(refreshGeneration);
                return false;
            }

            if (snapshot == null && idsToRemove == null && (toAdd?.Count ?? 0) == 0 && (toUpdate?.Count ?? 0) == 0)
            {
                return true;
            }

            if (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return false;
            }

            bool ownsEditStates = editStates == null;
            editStates ??= CaptureRowEditStates();
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
                                RemoveInteractionRow(existingItem);
                                DetachVolumeChangedHandlerIfNeeded(id, existingItem);
                                Sessions.Remove(existingItem);

                                _userSetVolumes.TryRemove(id, out _);
                                _lastVolumeSetByUs.TryRemove(id, out _);

                                if (_throttleStates.TryRemove(id, out var state))
                                {
                                    lock (state.Lock)
                                    {
                                        state.PendingEdit.Item?.CompleteVolumeEdit(state.PendingEdit.Revision);
                                        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref state.TrailingCts);
                                    }
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
                            _userSetVolumes[id] = resolvedItem.Volume;
                            itemsToInsert ??= [];
                            itemsToInsert.Add(resolvedItem);
                        }

                        if (itemsToInsert != null)
                        {
                            Sessions.InsertSortedRange(itemsToInsert, CompareSessionItems);
                        }
                    }

                    if (snapshot != null)
                    {
                        int renamedCount = 0;
                        foreach (AudioSessionSnapshot session in snapshot)
                        {
                            string id = GetSessionIdentity(session, out _);
                            if (_sessionsById.TryGetValue(id, out AudioSessionItem? item))
                            {
                                item.UpdateRoutingMetadataFromSystem(session.ProcessName, session.SessionInstanceId, session.EndpointId, session.SessionCount);
                                if (!string.Equals(item.DisplayName, session.DisplayName, StringComparison.Ordinal))
                                {
                                    item.SetDisplayNameFromSystem(session.DisplayName);
                                    renamedCount++;
                                }
                            }
                        }

                        if (renamedCount > 0 && _logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.Debug("MixerViewModel", () => $"mixer-names-refreshed | mode={_mixerMode} renamed={renamedCount}");
                        }
                    }

                    if (toUpdate != null)
                    {
                        foreach (var (id, volume, isMuted) in toUpdate)
                        {
                            if (_sessionsById.TryGetValue(id, out var item) && editStates.TryGetValue(id, out MixerRowEditState state))
                                ApplyRefreshedRowState(id, item, volume, isMuted, state);
                        }
                    }
                    if (snapshot != null)
                    {
                        ReplaceSessionInstanceMappings(sessionInstanceMappings);
                        _lastProcessedSnapshotEntries = snapshot;
                        ClearActivationRefreshRequirement();
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
            finally
            {
                if (ownsEditStates) ReturnRowEditStates(editStates, refreshGeneration);
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

        private bool CanApplyRefreshResults(int refreshGeneration)
        {
            return !_disposed && ShouldApplyRefreshResults(refreshGeneration, GetActiveRefreshGeneration());
        }

        private void ClearCollectionPools()
        {
            Interlocked.Exchange(ref _pooledRowEditStates, null);
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
    }
}
