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
                    item.SetStateFromSystem(normalizedVolume, isMuted);
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

            if (_throttleStates.TryGetValue(rowId, out ThrottleState? throttleState))
            {
                lock (throttleState.Lock)
                {
                    if (throttleState.HasPending)
                    {
                        return false;
                    }
                }
            }

            float normalizedVolume = Math.Clamp(volumePercent, 0f, 100f);
            item.SetStateFromSystem(normalizedVolume, isMuted);
            _userSetVolumes[GetUserVolumeKey(rowId)] = normalizedVolume;
            return true;
        }

        private void ReplaceSessionInstanceMappings(Dictionary<string, string>? mappings)
        {
            _sessionRowIdByInstanceId.Clear();
            if (mappings == null)
            {
                return;
            }

            foreach ((string sessionInstanceId, string rowId) in mappings)
            {
                _sessionRowIdByInstanceId[sessionInstanceId] = rowId;
            }
        }
    }
}
