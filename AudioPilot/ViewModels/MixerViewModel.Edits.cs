using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class MixerViewModel
    {
        private readonly record struct MixerRowEdit(AudioSessionItem Item, string EndpointId, long Revision, long EndpointMuteRevision = -1);
        internal readonly record struct MixerRowEditState(AudioSessionItem Item, long VolumeRevision, long MuteRevision,
            bool VolumeWasPending = false, bool MuteWasPending = false);
        private Dictionary<string, MixerRowEditState>? _pooledRowEditStates;

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
