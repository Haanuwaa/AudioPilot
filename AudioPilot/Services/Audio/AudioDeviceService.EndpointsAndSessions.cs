using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    public partial class AudioDeviceService
    {
        public AudioStatusSnapshot GetAudioStatus() => _endpointQueryHelper.GetAudioStatus();

        public MMDeviceCollection GetActivePlaybackDevices()
        {
            return _endpointQueryHelper.GetActivePlaybackDevices();
        }

        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds)
        {
            return _endpointQueryHelper.GetPlaybackDevicesById(deviceIds);
        }

        public MMDeviceCollection GetActiveCaptureDevices()
        {
            return _endpointQueryHelper.GetActiveCaptureDevices();
        }

        public List<CycleDevice> GetActivePlaybackCycleEntries()
        {
            return _endpointQueryHelper.GetActiveCycleDevices(GetActivePlaybackDevices, nameof(GetActivePlaybackCycleEntries));
        }

        public List<CycleDevice> GetActiveCaptureCycleEntries()
        {
            return _endpointQueryHelper.GetActiveCycleDevices(GetActiveCaptureDevices, nameof(GetActiveCaptureCycleEntries));
        }

        public MMDevice GetDefaultPlaybackDevice() => _endpointQueryHelper.GetDefaultPlaybackDevice();

        internal MMDevice GetDefaultPlaybackDevice(NRole role) => _endpointQueryHelper.GetDefaultPlaybackDevice(role);

        public MMDevice? GetDefaultRecordingDevice() => _endpointQueryHelper.GetDefaultRecordingDevice();

        internal MMDevice? GetDefaultRecordingDevice(NRole role) => _endpointQueryHelper.GetDefaultRecordingDevice(role);

        private static string? GetDefaultPlaybackDeviceId(NRole role)
        {
            using var localEnumerator = new MMDeviceEnumerator();
            using MMDevice? device = localEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            return device?.ID;
        }

        private static string? GetDefaultRecordingDeviceId(NRole role)
        {
            using var localEnumerator = new MMDeviceEnumerator();
            using MMDevice? device = localEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            return device?.ID;
        }

        public CycleDevice? TryGetActivePlaybackCycleEntry(string deviceId, string fallbackName)
        {
            return TryGetActiveCycleDevice(deviceId, fallbackName, DataFlow.Render);
        }

        public CycleDevice? TryGetActiveRecordingCycleEntry(string deviceId, string fallbackName)
        {
            return TryGetActiveCycleDevice(deviceId, fallbackName, DataFlow.Capture);
        }

        internal MMDevice? TryGetPlaybackDeviceForRoutine(string? deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId)
                ? GetDefaultPlaybackDevice()
                : TryGetPlaybackDeviceById(deviceId);
        }

        internal MMDevice? TryGetRecordingDeviceForRoutine(string? deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId)
                ? GetDefaultRecordingDevice()
                : TryGetCaptureDeviceById(deviceId);
        }

        private CycleDevice? TryGetActiveCycleDevice(string deviceId, string fallbackName, DataFlow flow)
        {
            _enumeratorLock.EnterReadLock();
            try
            {
                return AudioDeviceActiveCycleResolverHelper.TryResolve(
                    deviceId,
                    fallbackName,
                    _disposed,
                    _enumerator.GetDevice,
                    device => device.State == NDeviceState.Active && device.DataFlow == flow,
                    static (resolvedDeviceId, resolvedFallbackName, getFriendlyName) =>
                        TryCreateActiveCycleDevice(resolvedDeviceId, resolvedFallbackName, getFriendlyName),
                    static device => device.FriendlyName,
                    static device => device.Dispose(),
                    message =>
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("AudioDeviceService", () => message);
                        }
                    });
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        private static CycleDevice TryCreateActiveCycleDevice(
            string deviceId,
            string fallbackName,
            Func<string?> getFriendlyName)
        {
            string resolvedName;
            try
            {
                string? friendlyName = getFriendlyName();
                resolvedName = string.IsNullOrWhiteSpace(friendlyName) ? fallbackName : friendlyName;
            }
            catch
            {
                resolvedName = fallbackName;
            }

            return new CycleDevice
            {
                Id = deviceId,
                Name = resolvedName,
            };
        }

        public List<MMDevice?> GetAllDefaultPlaybackDevices()
        {
            return _endpointQueryHelper.GetAllDefaultPlaybackDevices();
        }

        public List<MMDevice?> GetAllDefaultRecordingDevices()
        {
            return _endpointQueryHelper.GetAllDefaultRecordingDevices();
        }

        internal async Task<IReadOnlyList<AudioSessionSnapshot>> GetAllAudioSessionSnapshotsAsync(
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            CancellationToken cancellationToken = default)
        {
            return await GetAllAudioSessionSnapshotsAsync(AudioMixerMode.Output, recentSnapshotCacheWindowMs, cancellationToken);
        }

        internal async Task<IReadOnlyList<AudioSessionSnapshot>> GetAllAudioSessionSnapshotsAsync(
            AudioMixerMode mixerMode,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            CancellationToken cancellationToken = default)
        {
            return await _sessionService.GetAllAudioSessionSnapshotsAsync(
                mixerMode,
                recentSnapshotCacheWindowMs,
                cancellationToken);
        }

        public void UpdateSessionVolumeCache(string? displayName, string? processName, float volume, AudioMixerMode mixerMode = AudioMixerMode.Output, uint? processId = null)
        {
            if (mixerMode != AudioMixerMode.Output) return;
            if (processId is uint pid && _sessionProcessResolver.TryResolveProcessName(pid, out string executableName))
                processName = executableName;
            if (string.IsNullOrEmpty(displayName) && string.IsNullOrEmpty(processName))
            {
                return;
            }

            _volumeService.UpdateSessionVolume(displayName ?? string.Empty, processName ?? string.Empty, volume, mixerMode);
        }
    }
}
