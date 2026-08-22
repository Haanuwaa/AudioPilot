using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    public partial class AudioDeviceService
    {
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

        public MMDevice GetDefaultPlaybackDevice()
        {
            return GetDefaultPlaybackDevice(reason: null);
        }

        public MMDevice GetDefaultPlaybackDevice(string? reason = null)
        {
            return _endpointQueryHelper.GetDefaultPlaybackDevice(reason);
        }

        public MMDevice? GetDefaultRecordingDevice()
        {
            return GetDefaultRecordingDevice(reason: null);
        }

        public MMDevice? GetDefaultRecordingDevice(string? reason = null)
        {
            return _endpointQueryHelper.GetDefaultRecordingDevice(reason);
        }

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
            return TryGetActiveCycleDevice(deviceId, fallbackName);
        }

        public CycleDevice? TryGetActiveRecordingCycleEntry(string deviceId, string fallbackName)
        {
            return TryGetActiveCycleDevice(deviceId, fallbackName);
        }

        internal MMDevice? TryGetPlaybackDeviceForRoutine(string? deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId)
                ? GetDefaultPlaybackDevice("routine-volume:playback-default")
                : TryGetPlaybackDeviceById(deviceId);
        }

        internal MMDevice? TryGetRecordingDeviceForRoutine(string? deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId)
                ? GetDefaultRecordingDevice("routine-volume:recording-default")
                : TryGetCaptureDeviceById(deviceId);
        }

        private CycleDevice? TryGetActiveCycleDevice(string deviceId, string fallbackName)
        {
            _enumeratorLock.EnterReadLock();
            try
            {
                return AudioDeviceActiveCycleResolverHelper.TryResolve(
                    deviceId,
                    fallbackName,
                    _disposed,
                    _enumerator.GetDevice,
                    static device => device.State == NDeviceState.Active,
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

        private List<CycleDevice> GetActiveCycleDevices(Func<MMDeviceCollection> getDevices, string operationName)
        {
            MMDeviceCollection? devices = null;
            try
            {
                devices = getDevices();
                return AudioDeviceCycleCollectionProjectionHelper.Project<MMDeviceCollection, MMDevice, CycleDevice>(
                    devices,
                    static (collection, onDisposeFailure) => AudioDeviceCollectionHelper.ProjectCycleDevices(
                        collection,
                        onDisposeFailure: onDisposeFailure),
                    static device => device?.FriendlyName,
                    static device => device?.ID,
                    message =>
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("AudioDeviceService", () => message);
                        }
                    });
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, operationName, ex);
                throw;
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, operationName, ex);
                throw;
            }
            finally
            {
                devices?.Dispose();
            }
        }

        public List<MMDevice?> GetAllDefaultPlaybackDevices()
        {
            return _endpointQueryHelper.GetAllDefaultPlaybackDevices();
        }

        public List<MMDevice?> GetAllDefaultRecordingDevices()
        {
            return _endpointQueryHelper.GetAllDefaultRecordingDevices();
        }

        public async Task<ObservableCollection<AudioSessionInfo>> GetAllAudioSessionsAsync(
            Action<AudioSessionInfo> onVolumeChanged,
            CancellationToken cancellationToken = default)
        {
            return await _sessionService.GetAllAudioSessionsAsync(onVolumeChanged, cancellationToken);
        }

        public async Task<List<AudioSessionInfo>> GetAllAudioSessionsSnapshotAsync(
            Action<AudioSessionInfo> onVolumeChanged,
            bool includeSessionControls = true,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            CancellationToken cancellationToken = default)
        {
            return await _sessionService.GetAllAudioSessionsSnapshotAsync(
                onVolumeChanged,
                includeSessionControls,
                recentSnapshotCacheWindowMs,
                cancellationToken);
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

        public ObservableCollection<AudioSessionInfo> GetAllAudioSessions(
            Action<AudioSessionInfo> onVolumeChanged)
        {
            return _sessionService.GetAllAudioSessions(onVolumeChanged);
        }

        public List<AudioSessionInfo> GetAllAudioSessionsSnapshot(
            Action<AudioSessionInfo> onVolumeChanged,
            bool includeSessionControls = true,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs)
        {
            return _sessionService.GetAllAudioSessionsSnapshot(
                onVolumeChanged,
                includeSessionControls,
                recentSnapshotCacheWindowMs);
        }

        internal IReadOnlyList<AudioSessionSnapshot> GetAllAudioSessionSnapshots(
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs)
        {
            return GetAllAudioSessionSnapshots(AudioMixerMode.Output, recentSnapshotCacheWindowMs);
        }

        internal IReadOnlyList<AudioSessionSnapshot> GetAllAudioSessionSnapshots(
            AudioMixerMode mixerMode,
            int recentSnapshotCacheWindowMs = AppConstants.Timing.SessionSnapshotFastPathCacheMs)
        {
            return _sessionService.GetAllAudioSessionSnapshots(mixerMode, recentSnapshotCacheWindowMs);
        }

        public void UpdateVolume(AudioSessionInfo sessionInfo)
        {
            _volumeService.UpdateVolume(sessionInfo);
        }

        public SessionVolumeSnapshot CaptureSessionVolumes()
        {
            return _volumeService.CaptureSessionVolumes();
        }

        public void UpdateSessionVolumeCache(string? displayName, string? processName, float volume)
        {
            if (string.IsNullOrEmpty(displayName) && string.IsNullOrEmpty(processName))
            {
                return;
            }

            _volumeService.UpdateSessionVolume(displayName ?? string.Empty, processName ?? string.Empty, volume);
        }
    }
}
