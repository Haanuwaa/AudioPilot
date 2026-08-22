using System.Collections.Concurrent;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class MixerViewModel
    {
        private readonly ConcurrentDictionary<string, string> _sessionRowIdByInstanceId = new(StringComparer.OrdinalIgnoreCase);

        internal AudioSessionItem? GetEndpointRow(AudioMixerMode mode)
        {
            string id = mode == AudioMixerMode.Input ? "mic:primary" : "master:primary";
            if (_sessionsById?.TryGetValue(id, out AudioSessionItem? row) == true) return row;
            return null;
        }

        internal MixerRowEditState? CaptureEndpointEditState(AudioMixerMode mode)
            => GetEndpointRow(mode) is AudioSessionItem item ? new(item, item.VolumeEditRevision, item.MuteEditRevision) : null;

        internal void ApplyEndpointMuteStateFromSystem(bool playbackMuted, bool microphoneMuted)
        {
            if (_disposed || Sessions is not { Count: > 0 } sessions)
            {
                return;
            }

            foreach (AudioSessionItem item in sessions)
            {
                if (item.IsMaster)
                {
                    if (!item.HasPendingMuteEdit) item.SetMuteFromSystem(playbackMuted);
                }
                else if (item.IsMic)
                {
                    if (!item.HasPendingMuteEdit) item.SetMuteFromSystem(microphoneMuted);
                }
            }
        }

        internal void ApplyEndpointStateFromSystem(AudioMixerMode mode, float volumePercent, bool isMuted, string? endpointId = null,
            MixerRowEditState? editState = null, bool applyMute = true)
        {
            if (_disposed || Sessions is not { Count: > 0 } sessions)
            {
                return;
            }

            float normalizedVolume = Math.Clamp(volumePercent, 0f, 100f);
            foreach (AudioSessionItem item in sessions)
            {
                if ((mode == AudioMixerMode.Output && item.IsMaster)
                    || (mode == AudioMixerMode.Input && item.IsMic))
                {
                    if (endpointId != null && !string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)) continue;
                    string rowId = GetSessionIdForItem(item);
                    if (editState.HasValue)
                    {
                        ApplyRefreshedRowState(rowId, item, normalizedVolume, isMuted, editState.Value, applyMute);
                        continue;
                    }
                    if (!HasPendingVolumeChange(rowId) && _sharedSessionBridge?.HasPendingVolumeChange(rowId) != true)
                    {
                        item.SetVolumeFromSystem(normalizedVolume);
                        _userSetVolumes[rowId] = normalizedVolume;
                    }
                    if (applyMute && !item.HasPendingMuteEdit) item.SetMuteFromSystem(isMuted);
                }
            }
        }

        internal bool ApplySessionStateFromSystem(string sessionInstanceId, float volumePercent, bool isMuted)
        {
            if (_disposed
                || string.IsNullOrWhiteSpace(sessionInstanceId)
                || !_sessionRowIdByInstanceId.TryGetValue(sessionInstanceId, out string? rowId)
                || !_sessionsById.TryGetValue(rowId, out AudioSessionItem? item)
                || item.SessionCount > 1)
            {
                return false;
            }

            if (HasPendingVolumeChange(rowId) || _sharedSessionBridge?.HasPendingVolumeChange(rowId) == true)
            {
                return false;
            }

            float normalizedVolume = Math.Clamp(volumePercent, 0f, 100f);
            item.SetVolumeFromSystem(normalizedVolume);
            if (!item.HasPendingMuteEdit) item.SetMuteFromSystem(isMuted);
            _userSetVolumes[rowId] = normalizedVolume;
            return true;
        }

        internal bool HasPendingVolumeChange(string rowId)
        {
            if (_sessionsById.TryGetValue(rowId, out AudioSessionItem? item) && item.HasPendingVolumeEdit) return true;
            if (!_throttleStates.TryGetValue(rowId, out ThrottleState? state)) { return false; }
            lock (state.Lock) { return state.HasPending; }
        }

        private void ReplaceSessionInstanceMappings(Dictionary<string, string>? mappings)
        {
            if (mappings == null)
            {
                _sessionRowIdByInstanceId.Clear();
                return;
            }

            foreach ((string sessionInstanceId, _) in _sessionRowIdByInstanceId)
            {
                if (!mappings.ContainsKey(sessionInstanceId))
                {
                    _sessionRowIdByInstanceId.TryRemove(sessionInstanceId, out _);
                }
            }

            foreach ((string sessionInstanceId, string rowId) in mappings)
            {
                if (!_sessionRowIdByInstanceId.TryGetValue(sessionInstanceId, out string? existingRowId)
                    || !string.Equals(existingRowId, rowId, StringComparison.OrdinalIgnoreCase))
                {
                    _sessionRowIdByInstanceId[sessionInstanceId] = rowId;
                }
            }
        }
    }
}
