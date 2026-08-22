using System.Collections.Concurrent;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class MixerViewModel
    {
        private readonly ConcurrentDictionary<string, string> _sessionRowIdByInstanceId = new(StringComparer.OrdinalIgnoreCase);

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
                    item.SetMuteFromSystem(playbackMuted);
                }
                else if (item.IsMic)
                {
                    item.SetMuteFromSystem(microphoneMuted);
                }
            }
        }

        internal void ApplyEndpointStateFromSystem(AudioMixerMode mode, float volumePercent, bool isMuted)
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
                    string rowId = GetSessionIdForItem(item);
                    if (HasPendingVolumeChange(rowId) || _sharedSessionBridge?.HasPendingVolumeChange(rowId) == true)
                    {
                        continue;
                    }

                    item.SetStateFromSystem(normalizedVolume, isMuted);
                    _userSetVolumes[rowId] = normalizedVolume;
                }
            }
        }

        internal bool ApplySessionStateFromSystem(string sessionInstanceId, float volumePercent, bool isMuted)
        {
            if (_disposed
                || string.IsNullOrWhiteSpace(sessionInstanceId)
                || !_sessionRowIdByInstanceId.TryGetValue(sessionInstanceId, out string? rowId)
                || !_sessionsById.TryGetValue(rowId, out AudioSessionItem? item))
            {
                return false;
            }

            if (HasPendingVolumeChange(rowId) || _sharedSessionBridge?.HasPendingVolumeChange(rowId) == true)
            {
                return false;
            }

            float normalizedVolume = Math.Clamp(volumePercent, 0f, 100f);
            item.SetStateFromSystem(normalizedVolume, isMuted);
            _userSetVolumes[rowId] = normalizedVolume;
            return true;
        }

        internal bool HasPendingVolumeChange(string rowId)
        {
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
