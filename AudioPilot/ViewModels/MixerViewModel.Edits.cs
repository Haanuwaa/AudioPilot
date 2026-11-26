using System.Diagnostics;
using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class MixerViewModel
    {
        private readonly record struct MixerRowEdit(AudioSessionItem Item, string EndpointId, long Revision, long EndpointMuteRevision = -1);
        internal readonly record struct MixerRowEditState(AudioSessionItem Item, long VolumeRevision, long MuteRevision,
            bool VolumeWasPending = false, bool MuteWasPending = false);
        private Dictionary<string, MixerRowEditState>? _pooledRowEditStates;
        private readonly Lock _groupVolumeLock = new();
        private Dictionary<string, long>? _pendingGroupVolume;
        private List<(string Id, long Revision)>? _groupVolumeScratch, _stagedGroupVolume;
        private List<MixerRowEdit>? _stagedGroupMute;
        private CancellationTokenSource? _groupVolumeLifetime;
        private bool _groupVolumeDraining;
        private long _lastGroupVolumeBatch;
        internal Func<Dictionary<uint, AudioDeviceHelper.SessionVolumeTarget>, Dictionary<uint, AudioDeviceHelper.SessionMutationResult>>?
            GroupVolumeWriter
        {
            get;
            set;
        }

        private void ApplyGroupVolumeEdit(AudioSessionItem anchor, Dictionary<AudioSessionItem, float> originalVolumes, float? newVolume = null)
        {
            bool wasPropagating = _propagatingGroup;
            _propagatingGroup = true;
            _stagedGroupVolume = _groupVolumeScratch ??= [];
            try
            {
                if (newVolume is float value) anchor.Volume = value;
                else StageVolumeEdit(anchor);
                ApplyGroupVolumeOffsets(anchor, originalVolumes);
            }
            finally
            {
                _propagatingGroup = wasPropagating;
                PublishGroupMuteEdits(muted: false);
                PublishGroupVolumeEdits();
            }
        }

        private void PublishGroupVolumeEdits()
        {
            var edits = _stagedGroupVolume!;
            _stagedGroupVolume = null;
            bool startDrain = false;
            CancellationToken token = default;
            lock (_groupVolumeLock)
            {
                if (!_disposed && edits.Count > 0)
                {
                    _pendingGroupVolume ??= [with(StringComparer.OrdinalIgnoreCase)];
                    foreach (var (id, revision) in edits) _pendingGroupVolume[id] = revision;
                    if (!_groupVolumeDraining)
                    {
                        _groupVolumeDraining = startDrain = true;
                        token = (_groupVolumeLifetime ??= new()).Token;
                    }
                }
            }
            edits.Clear();
            if (startDrain) _ = DrainGroupVolumesAsync(token);
        }

        private async Task DrainGroupVolumesAsync(CancellationToken token)
        {
            while (true)
            {
                lock (_groupVolumeLock)
                {
                    if (_pendingGroupVolume == null || _disposed)
                    {
                        _groupVolumeDraining = false;
                        return;
                    }
                }

                if (_lastGroupVolumeBatch != 0)
                {
                    double remainingMs = ThrottleIntervalMs - Stopwatch.GetElapsedTime(_lastGroupVolumeBatch).TotalMilliseconds;
                    if (remainingMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    }
                }

                Dictionary<string, long>? pending;
                lock (_groupVolumeLock)
                {
                    pending = _pendingGroupVolume;
                    _pendingGroupVolume = null;
                }
                if (pending == null || _disposed) continue;
                Task<MixerMutationResult> work = ComThreadingHelper.RunOnCoreAudioThreadAsync(() => ApplyPendingGroupVolumes(pending), token);
                await ObserveVolumeApplyAsync(SettleQueuedGroupVolumeAsync(work, pending), "group-volume").ConfigureAwait(false);
            }
        }

        private async Task<MixerMutationResult> SettleQueuedGroupVolumeAsync(Task<MixerMutationResult> work, Dictionary<string, long> pending)
        {
            try { return await work.ConfigureAwait(false); }
            finally
            {
                if (work.IsFaulted || work.IsCanceled)
                    foreach (var (edit, _) in TakeGroupVolumeEdits(pending)) edit.Item.CompleteVolumeEdit(edit.Revision);
            }
        }

        private MixerMutationResult ApplyPendingGroupVolumes(Dictionary<string, long> pending)
        {
            if (_disposed) return default;
            var edits = TakeGroupVolumeEdits(pending);
            if (edits.Count == 0) return default;
            _lastGroupVolumeBatch = Stopwatch.GetTimestamp();
            try { return ApplyCollectedVolumeEdits(edits); }
            finally { foreach (var (edit, _) in edits) edit.Item.CompleteVolumeEdit(edit.Revision); }
        }

        private List<(MixerRowEdit Edit, float Volume)> TakeGroupVolumeEdits(Dictionary<string, long> pending)
        {
            var edits = new List<(MixerRowEdit Edit, float Volume)>(pending.Count);
            long now = Stopwatch.GetTimestamp();
            foreach ((string id, long revision) in pending)
            {
                if (!_throttleStates.TryGetValue(id, out ThrottleState? state)) continue;
                lock (state.Lock)
                {
                    if (!state.HasPending || state.TrailingCts != null || state.PendingEdit.Revision != revision) continue;
                    edits.Add((state.PendingEdit, state.PendingVolume));
                    state.HasPending = false;
                    state.LastAppliedTimestamp = now;
                }
            }
            return edits;
        }

        private MixerMutationResult ApplyCollectedVolumeEdits(List<(MixerRowEdit Edit, float Volume)> edits)
        {
            if (edits.Count == 1) return ApplyQueuedVolumeEdit(edits[0].Edit, edits[0].Volume);
            MixerMutationResult total = default;
            Dictionary<uint, AudioDeviceHelper.SessionVolumeTarget>? targets = null;
            foreach (var (edit, volume) in edits)
            {
                if (_mixerMode != AudioMixerMode.Output || !edit.Item.IsApplication)
                {
                    Add(ApplyQueuedVolumeEdit(edit, volume));
                    continue;
                }
                if (!IsCurrentRowEdit(edit, mute: false)) continue;
                if (!IsApplicationProcessCurrent(edit.Item))
                {
                    Add(MixerMutationResult.Failure());
                    continue;
                }
                (targets ??= [])[edit.Item.ProcessId!.Value] = new(Math.Clamp(volume / 100f, 0f, 1f),
                    () => IsCurrentRowEdit(edit, mute: false));
            }

            if (targets == null) return total;
            var results = GroupVolumeWriter?.Invoke(targets)
                ?? AudioDeviceHelper.SetPlaybackSessionVolumesByPid(_audio, targets, _pidToProcessName, _logger);
            bool anyWritten = results.Values.Any(static result => result.Succeeded > 0);
            foreach (var (edit, volume) in edits)
            {
                if (edit.Item.ProcessId is not uint pid || !targets.ContainsKey(pid) || !IsCurrentRowEdit(edit, mute: false)) continue;
                var result = results.GetValueOrDefault(pid);
                if (result.IsCompleteSuccess)
                {
                    _pidToProcessName.TryGetValue(pid, out string? processName);
                    _audio.UpdateSessionVolumeCache(edit.Item.DisplayName, processName, volume, _mixerMode, pid);
                    Add(MixerMutationResult.Success(result.Succeeded));
                }
                else
                    Add(new(Math.Max(1, result.Attempted), result.Succeeded, Math.Max(1, result.Failed)));
            }
            if (anyWritten) _audio.InvalidateRecentMixerSnapshotState();
            return total;

            void Add(MixerMutationResult result) =>
                total = new(total.Attempted + result.Attempted, total.Succeeded + result.Succeeded, total.Failed + result.Failed);
        }

        private void StopGroupVolumeBatches()
        {
            lock (_groupVolumeLock)
            {
                _pendingGroupVolume = null;
                AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _groupVolumeLifetime);
            }
        }

        private void SetGroupMute(bool muted, HashSet<AudioSessionItem> selection)
        {
            _stagedGroupMute = [with(selection.Count)];
            try
            {
                foreach (var selected in selection)
                    if (IsCurrentInteractionRow(selected)) selected.IsMuted = muted;
            }
            finally { PublishGroupMuteEdits(muted); }
        }

        private void PublishGroupMuteEdits(bool muted)
        {
            var edits = _stagedGroupMute;
            _stagedGroupMute = null;
            if (edits is not { Count: > 0 }) return;
            if (_disposed)
            {
                foreach (MixerRowEdit edit in edits) edit.Item.CompleteMuteEdit(edit.Revision);
                return;
            }
            CancellationToken token = (_muteApplyCts ??= new()).Token;
            _ = ObserveMuteApplyAsync(ApplyGroupMuteAsync(edits, muted, token), "group-mute", token);
        }

        private async Task<MixerMutationResult> ApplyGroupMuteAsync(List<MixerRowEdit> edits, bool muted, CancellationToken token)
        {
            try
            {
                return await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                {
                    if (edits.Count == 1) return ApplyQueuedMuteEdit(edits[0], muted);
                    Dictionary<uint, Func<bool>> targets = [];
                    int attempted = 0, succeeded = 0, failed = 0;
                    foreach (MixerRowEdit edit in edits)
                    {
                        if (!IsCurrentRowEdit(edit, mute: true)) continue;
                        if (!IsApplicationProcessCurrent(edit.Item)) { attempted++; failed++; continue; }
                        targets[edit.Item.ProcessId!.Value] = () => IsCurrentRowEdit(edit, mute: true);
                    }
                    if (targets.Count == 0) return new MixerMutationResult(attempted, succeeded, failed);
                    var results = AudioDeviceHelper.SetPlaybackSessionMutesByPid(_audio, targets, muted, _pidToProcessName, _logger);
                    foreach ((uint pid, Func<bool> canApply) in targets)
                    {
                        if (!canApply()) continue;
                        var result = results.GetValueOrDefault(pid);
                        attempted += Math.Max(1, result.Attempted);
                        succeeded += result.Succeeded;
                        if (!result.IsCompleteSuccess) failed += Math.Max(1, result.Failed);
                    }
                    if (results.Values.Any(static result => result.Succeeded > 0)) _audio.InvalidateRecentMixerSnapshotState();
                    return new MixerMutationResult(attempted, succeeded, failed);
                }, token).ConfigureAwait(false);
            }
            finally { foreach (MixerRowEdit edit in edits) edit.Item.CompleteMuteEdit(edit.Revision); }
        }

        private Dictionary<string, MixerRowEditState> CaptureRowEditStates()
        {
            var states = Interlocked.Exchange(ref _pooledRowEditStates, null)
                ?? new Dictionary<string, MixerRowEditState>(_sessionsById.Count, StringComparer.OrdinalIgnoreCase);
            foreach ((string id, AudioSessionItem item) in _sessionsById)
                states[id] = new(item, item.VolumeEditRevision, item.MuteEditRevision, item.HasPendingVolumeEdit, item.HasPendingMuteEdit);
            return states;
        }

        private void ReturnRowEditStates(Dictionary<string, MixerRowEditState>? states, int refreshGeneration)
        {
            if (states == null) return;
            states.Clear();
            if (!_disposed && refreshGeneration == GetActiveRefreshGeneration())
            {
                if (Interlocked.CompareExchange(ref _pooledRowEditStates, states, null) == null &&
                    (_disposed || refreshGeneration != GetActiveRefreshGeneration()))
                    Interlocked.CompareExchange(ref _pooledRowEditStates, null, states);
            }
        }

        private bool IsCurrentRowEdit(MixerRowEdit edit, bool mute)
        {
            AudioSessionItem item = edit.Item;
            return !_disposed
                && _sessionsById.TryGetValue(GetSessionIdForItem(item), out AudioSessionItem? current)
                && ReferenceEquals(current, item)
                && edit.Revision == (mute ? item.MuteEditRevision : item.VolumeEditRevision);
        }

        private static async Task<MixerMutationResult> CompleteRowEditAsync(Task<MixerMutationResult> work, MixerRowEdit edit, bool mute)
        {
            try { return await work.ConfigureAwait(false); }
            finally
            {
                if (mute) edit.Item.CompleteMuteEdit(edit.Revision);
                else edit.Item.CompleteVolumeEdit(edit.Revision);
            }
        }

        private MixerMutationResult ApplyQueuedVolumeEdit(MixerRowEdit edit, float volume)
        {
            if (!IsCurrentRowEdit(edit, mute: false)) return default;
            MixerMutationResult result = ApplyVolumeChange(edit.Item, volume, edit.EndpointId);
            if (result.IsSuccess) _audio.InvalidateRecentMixerSnapshotState();
            return result;
        }

        private MixerMutationResult ApplyQueuedMuteEdit(MixerRowEdit edit, bool muted)
        {
            if (!IsCurrentRowEdit(edit, mute: true)) return default;
            MixerMutationResult result = ApplyMuteChange(edit.Item, muted, edit.EndpointId,
                edit.EndpointMuteRevision >= 0 ? edit.EndpointMuteRevision : null);
            if (result.IsSuccess) _audio.InvalidateRecentMixerSnapshotState();
            return result;
        }

        private void ApplyRefreshedRowState(string id, AudioSessionItem item, float volume, bool muted, MixerRowEditState state, bool applyMute = true)
        {
            if (!ReferenceEquals(item, state.Item)) return;
            if (!state.VolumeWasPending && item.VolumeEditRevision == state.VolumeRevision && !HasPendingVolumeChange(id)
                && _sharedSessionBridge?.HasPendingVolumeChange(id) != true)
            {
                item.SetVolumeFromSystem(volume);
                _userSetVolumes[id] = volume;
            }
            if (applyMute && !state.MuteWasPending && item.MuteEditRevision == state.MuteRevision && !item.HasPendingMuteEdit)
                item.SetMuteFromSystem(muted);
        }
    }
}
