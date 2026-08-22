using AudioPilot.Constants;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioSessionRecentSnapshotCache
    {
        internal readonly record struct EndpointSnapshotEntry(
            string DeviceId,
            string DeviceName,
            float VolumePercent,
            bool IsMuted,
            long TimestampTicks);

        internal readonly record struct OutputSnapshotScanState(
            string PlaybackFingerprint,
            IReadOnlySet<string>? SessionBearingPlaybackDeviceIds,
            int SelectivePlaybackScanStreak);

        private readonly Lock _sync = new();
        private DateTime _recentNoControlsSnapshotCapturedUtc = DateTime.MinValue;
        private AudioSessionSnapshot[]? _recentNoControlsSnapshot;
        private DateTime _recentInputNoControlsSnapshotCapturedUtc = DateTime.MinValue;
        private AudioSessionSnapshot[]? _recentInputNoControlsSnapshot;
        private EndpointSnapshotEntry? _recentPlaybackEndpointSnapshot;
        private EndpointSnapshotEntry? _recentRecordingEndpointSnapshot;
        private HashSet<string>? _recentSessionBearingPlaybackDeviceIds;
        private string _recentPlaybackDeviceFingerprint = string.Empty;
        private int _selectivePlaybackScanStreak;

        internal OutputSnapshotScanState GetOutputScanState()
        {
            lock (_sync)
            {
                return new OutputSnapshotScanState(
                    _recentPlaybackDeviceFingerprint,
                    _recentSessionBearingPlaybackDeviceIds == null
                        ? null
                        : new HashSet<string>(_recentSessionBearingPlaybackDeviceIds, StringComparer.OrdinalIgnoreCase),
                    _selectivePlaybackScanStreak);
            }
        }

        internal (AudioSessionSnapshot[]? Snapshot, DateTime CapturedUtc) GetRecentSnapshotData(AudioMixerMode mixerMode)
        {
            lock (_sync)
            {
                return mixerMode == AudioMixerMode.Input
                    ? (_recentInputNoControlsSnapshot, _recentInputNoControlsSnapshotCapturedUtc)
                    : (_recentNoControlsSnapshot, _recentNoControlsSnapshotCapturedUtc);
            }
        }

        internal EndpointSnapshotEntry? GetEndpointSnapshot(AudioMixerMode mixerMode)
        {
            lock (_sync)
            {
                return mixerMode == AudioMixerMode.Input
                    ? _recentRecordingEndpointSnapshot
                    : _recentPlaybackEndpointSnapshot;
            }
        }

        internal void SeedRecentSnapshotForTests(
            AudioMixerMode mixerMode,
            AudioSessionSnapshot[]? snapshot,
            DateTime capturedUtc)
        {
            lock (_sync)
            {
                if (mixerMode == AudioMixerMode.Input)
                {
                    _recentInputNoControlsSnapshot = snapshot;
                    _recentInputNoControlsSnapshotCapturedUtc = snapshot == null ? DateTime.MinValue : capturedUtc;
                }
                else
                {
                    _recentNoControlsSnapshot = snapshot;
                    _recentNoControlsSnapshotCapturedUtc = snapshot == null ? DateTime.MinValue : capturedUtc;
                }
            }
        }

        internal void SeedEndpointSnapshotForTests(AudioMixerMode mixerMode, EndpointSnapshotEntry? snapshot)
        {
            lock (_sync)
            {
                if (mixerMode == AudioMixerMode.Input)
                {
                    _recentRecordingEndpointSnapshot = snapshot;
                }
                else
                {
                    _recentPlaybackEndpointSnapshot = snapshot;
                }
            }
        }

        internal void SetOutputScanStateForTests(
            string playbackFingerprint,
            HashSet<string>? sessionBearingPlaybackDeviceIds,
            int selectivePlaybackScanStreak)
        {
            lock (_sync)
            {
                _recentPlaybackDeviceFingerprint = playbackFingerprint ?? string.Empty;
                _recentSessionBearingPlaybackDeviceIds = sessionBearingPlaybackDeviceIds == null || sessionBearingPlaybackDeviceIds.Count == 0
                    ? null
                    : new HashSet<string>(sessionBearingPlaybackDeviceIds, StringComparer.OrdinalIgnoreCase);
                _selectivePlaybackScanStreak = selectivePlaybackScanStreak;
            }
        }

        internal bool TryGetCachedPrimaryEndpointSnapshot(
            Role role,
            bool isPlayback,
            Func<DeviceCacheHelper?> deviceCacheAccessor,
            out EndpointSnapshotEntry snapshot)
        {
            lock (_sync)
            {
                snapshot = default;

                EndpointSnapshotEntry? cachedSnapshot = isPlayback
                    ? _recentPlaybackEndpointSnapshot
                    : _recentRecordingEndpointSnapshot;

                if (cachedSnapshot == null)
                {
                    return false;
                }

                long nowTicks = DateTime.UtcNow.Ticks;
                if ((nowTicks - cachedSnapshot.Value.TimestampTicks) > TimeSpan.FromMilliseconds(AppConstants.Timing.MixerPrimaryEndpointRefreshCadenceMs).Ticks)
                {
                    return false;
                }

                DeviceCacheHelper? deviceCache = deviceCacheAccessor();
                if (deviceCache == null)
                {
                    return false;
                }

                string? currentDeviceId = isPlayback
                    ? deviceCache.GetPlaybackDeviceIdWithoutRefresh(role)
                    : deviceCache.GetRecordingDeviceIdWithoutRefresh(role);

                if (!string.Equals(cachedSnapshot.Value.DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                snapshot = cachedSnapshot.Value;
                return true;
            }
        }

        internal void UpdatePrimaryEndpointSnapshot(
            bool isPlayback,
            string deviceId,
            string deviceName,
            float volumePercent,
            bool isMuted)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            var snapshot = new EndpointSnapshotEntry(
                deviceId,
                deviceName,
                volumePercent,
                isMuted,
                DateTime.UtcNow.Ticks);

            lock (_sync)
            {
                if (isPlayback)
                {
                    _recentPlaybackEndpointSnapshot = snapshot;
                }
                else
                {
                    _recentRecordingEndpointSnapshot = snapshot;
                }
            }
        }

        internal bool TryGetRecentNoControlsSnapshotData(
            AudioMixerMode mixerMode,
            int recentSnapshotCacheWindowMs,
            out IReadOnlyList<AudioSessionSnapshot> sessions)
        {
            sessions = [];

            DateTime nowUtc = DateTime.UtcNow;

            lock (_sync)
            {
                DateTime capturedUtc = mixerMode == AudioMixerMode.Input
                    ? _recentInputNoControlsSnapshotCapturedUtc
                    : _recentNoControlsSnapshotCapturedUtc;

                if (!AudioSessionService.ShouldUseRecentSnapshotCache(
                    nowUtc,
                    capturedUtc,
                    recentSnapshotCacheWindowMs,
                    includeSessionControls: false))
                {
                    return false;
                }

                AudioSessionSnapshot[]? cachedSnapshot = mixerMode == AudioMixerMode.Input
                    ? _recentInputNoControlsSnapshot
                    : _recentNoControlsSnapshot;
                if (cachedSnapshot == null || cachedSnapshot.Length == 0)
                {
                    return false;
                }

                sessions = cachedSnapshot;
                return true;
            }
        }

        internal AudioSessionSnapshot[] UpdateRecentNoControlsSnapshot(
            AudioMixerMode mixerMode,
            List<AudioSessionSnapshot> sessions,
            string currentPlaybackFingerprint = "",
            HashSet<string>? sessionBearingPlaybackDeviceIds = null,
            bool useSelectivePlaybackScan = false)
        {
            AudioSessionSnapshot[] cachedSessions = sessions.Count == 0 ? [] : [.. sessions];

            lock (_sync)
            {
                if (mixerMode == AudioMixerMode.Input)
                {
                    _recentInputNoControlsSnapshot = cachedSessions;
                    _recentInputNoControlsSnapshotCapturedUtc = DateTime.UtcNow;
                    return cachedSessions;
                }

                _recentNoControlsSnapshot = cachedSessions;
                _recentNoControlsSnapshotCapturedUtc = DateTime.UtcNow;
                _recentPlaybackDeviceFingerprint = currentPlaybackFingerprint;
                _recentSessionBearingPlaybackDeviceIds = sessionBearingPlaybackDeviceIds == null || sessionBearingPlaybackDeviceIds.Count == 0
                    ? null
                    : new HashSet<string>(sessionBearingPlaybackDeviceIds, StringComparer.OrdinalIgnoreCase);
                _selectivePlaybackScanStreak = useSelectivePlaybackScan ? _selectivePlaybackScanStreak + 1 : 0;
                return cachedSessions;
            }
        }

        internal void InvalidateRecentMixerSnapshotState()
        {
            lock (_sync)
            {
                ClearUnderLock();
            }
        }

        internal bool RecordEndpointVolumeNotification(AudioMixerMode mixerMode, string endpointId, float volumePercent, bool isMuted)
        {
            float normalizedVolume = Math.Clamp(volumePercent, 0f, 100f);

            lock (_sync)
            {
                if (mixerMode == AudioMixerMode.Input)
                {
                    if (_recentRecordingEndpointSnapshot is not EndpointSnapshotEntry recordingSnapshot
                        || !string.Equals(recordingSnapshot.DeviceId, endpointId, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    _recentRecordingEndpointSnapshot = recordingSnapshot with
                    {
                        VolumePercent = normalizedVolume,
                        IsMuted = isMuted,
                        TimestampTicks = DateTime.UtcNow.Ticks,
                    };

                    _recentNoControlsSnapshot = UpdateCachedSnapshotRowState(_recentNoControlsSnapshot, "Microphone Volume", normalizedVolume, isMuted);
                    _recentInputNoControlsSnapshot = UpdateCachedSnapshotRowState(_recentInputNoControlsSnapshot, "Microphone Volume", normalizedVolume, isMuted);
                    return true;
                }

                if (_recentPlaybackEndpointSnapshot is not EndpointSnapshotEntry playbackSnapshot
                    || !string.Equals(playbackSnapshot.DeviceId, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                _recentPlaybackEndpointSnapshot = playbackSnapshot with
                {
                    VolumePercent = normalizedVolume,
                    IsMuted = isMuted,
                    TimestampTicks = DateTime.UtcNow.Ticks,
                };

                _recentNoControlsSnapshot = UpdateCachedSnapshotRowState(_recentNoControlsSnapshot, "Master Volume", normalizedVolume, isMuted);
                _recentInputNoControlsSnapshot = UpdateCachedSnapshotRowState(_recentInputNoControlsSnapshot, "Master Volume", normalizedVolume, isMuted);
                return true;
            }
        }

        internal void Clear()
        {
            lock (_sync)
            {
                ClearUnderLock();
            }
        }

        private void ClearUnderLock()
        {
            _recentPlaybackEndpointSnapshot = null;
            _recentRecordingEndpointSnapshot = null;
            _recentNoControlsSnapshot = null;
            _recentNoControlsSnapshotCapturedUtc = DateTime.MinValue;
            _recentInputNoControlsSnapshot = null;
            _recentInputNoControlsSnapshotCapturedUtc = DateTime.MinValue;
            _recentPlaybackDeviceFingerprint = string.Empty;
            _recentSessionBearingPlaybackDeviceIds = null;
            _selectivePlaybackScanStreak = 0;
        }

        private static AudioSessionSnapshot[]? UpdateCachedSnapshotRowState(AudioSessionSnapshot[]? cachedSnapshot, string displayName, float volumePercent, bool isMuted)
        {
            if (cachedSnapshot == null)
            {
                return null;
            }

            for (int index = 0; index < cachedSnapshot.Length; index++)
            {
                AudioSessionSnapshot item = cachedSnapshot[index];
                if (!string.Equals(item.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AudioSessionSnapshot[] updatedSnapshot = [.. cachedSnapshot];
                updatedSnapshot[index] = item with
                {
                    Volume = volumePercent,
                    IsMuted = isMuted,
                };
                return updatedSnapshot;
            }

            return cachedSnapshot;
        }
    }
}
