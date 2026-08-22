using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    public partial class VolumeControlService
    {
        public SessionVolumeSnapshot CaptureSessionVolumes()
        {
            return CaptureSessionVolumesCore(
                () => GetDefaultPlaybackDevice("capture-session-volumes:playback"),
                () => GetDefaultRecordingDevice("capture-session-volumes:recording"),
                includeRecordingVolume: true,
                nameof(CaptureSessionVolumes));
        }

        public SessionVolumeSnapshot CaptureSessionVolumesWithLocalEnumerator(Role playbackRole, Role recordingRole, bool includeRecordingVolume = true)
        {
            using var localEnumerator = new MMDeviceEnumerator();

            return CaptureSessionVolumesCore(
                () => localEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, playbackRole),
                () => localEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, recordingRole),
                includeRecordingVolume,
                nameof(CaptureSessionVolumesWithLocalEnumerator));
        }

        public SessionVolumeSnapshot CapturePlaybackSessionVolumesForDeviceId(string playbackDeviceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(playbackDeviceId);
            using var localEnumerator = new MMDeviceEnumerator();
            return CaptureSessionVolumesCore(
                () => localEnumerator.GetDevice(playbackDeviceId),
                static () => null,
                includeRecordingVolume: false,
                nameof(CapturePlaybackSessionVolumesForDeviceId));
        }

        public SessionVolumeSnapshot CaptureRecordingEndpointVolumeForDeviceId(string recordingDeviceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recordingDeviceId);
            float? micVolumePercent = null;
            using var localEnumerator = new MMDeviceEnumerator();
            using MMDevice? recordingDevice = localEnumerator.GetDevice(recordingDeviceId);
            if (recordingDevice != null &&
                AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out AudioEndpointVolume? recordingVolume, nameof(CaptureRecordingEndpointVolumeForDeviceId)))
            {
                micVolumePercent = recordingVolume.MasterVolumeLevelScalar * 100f;
            }

            return new SessionVolumeSnapshot { MicVolumePercent = micVolumePercent };
        }

        private SessionVolumeSnapshot CaptureSessionVolumesCore(
            Func<MMDevice?> getPlaybackDevice,
            Func<MMDevice?> getRecordingDevice,
            bool includeRecordingVolume,
            string operationName)
        {
            CleanupExpiredRetryStates();
            var byPid = new Dictionary<uint, float>();
            var byName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var wordIndex = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            float? masterVolumePercent = null;
            float? micVolumePercent = null;
            float? systemSoundsVolumePercent = null;

            MMDevice? playbackDevice = null;
            MMDevice? recordingDevice = null;

            try
            {
                playbackDevice = getPlaybackDevice();
                if (playbackDevice == null)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.Warning("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.CaptureSessionVolumesSkip} | reason=playback-device-unavailable");
                    return new SessionVolumeSnapshot
                    {
                        MasterVolumePercent = null,
                        MicVolumePercent = null,
                        SystemSoundsVolumePercent = null,
                        ByPid = byPid,
                        ByName = byName,
                        WordIndex = wordIndex
                    };
                }

                if (AudioDeviceHelper.TryGetEndpointVolume(_logger, playbackDevice, out var playbackVolume, $"{operationName}:playback-endpoint"))
                {
                    try
                    {
                        masterVolumePercent = playbackVolume.MasterVolumeLevelScalar * 100f;
                    }
                    catch (COMException ex)
                    {
                        AudioDeviceHelper.LogComException(_logger, nameof(CaptureSessionVolumes), ex);
                    }
                }

                if (includeRecordingVolume)
                {
                    try
                    {
                        recordingDevice = getRecordingDevice();
                        if (recordingDevice != null &&
                            AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out var recordingVolume, $"{operationName}:recording-endpoint"))
                        {
                            micVolumePercent = recordingVolume.MasterVolumeLevelScalar * 100f;
                        }
                    }
                    catch (COMException ex)
                    {
                        AudioDeviceHelper.LogComException(_logger, operationName, ex);
                    }
                }

                using AudioSessionManager sessionManager = playbackDevice.AudioSessionManager;
                using SessionCollection sessions = sessionManager.Sessions;
                int sessionCount = sessions.Count;

                byPid.EnsureCapacity(sessionCount);
                byName.EnsureCapacity(sessionCount);

                for (int i = 0; i < sessionCount; i++)
                {
                    AudioSessionControl? session = null;
                    try
                    {
                        session = sessions[i];
                    }
                    catch
                    {
                        continue;
                    }

                    if (session == null)
                        continue;

                    try
                    {
                        uint pid = session.GetProcessID;

                        if (!AudioDeviceHelper.TryGetSessionVolume(_logger, session, out float vol))
                            continue;

                        float volPercent = vol * 100f;

                        if (pid == 0)
                        {
                            systemSoundsVolumePercent = volPercent;
                        }
                        else
                        {
                            byPid.TryAdd(pid, volPercent);
                        }

                        string? name = session.DisplayName;
                        string? processName = null;
                        var cachedEntry = _lookupProcessInfo(pid);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            if (cachedEntry.HasValue)
                            {
                                if (!_isCacheEntryExpired(cachedEntry.Value.TimestampTicks))
                                {
                                    name = cachedEntry.Value.DisplayName ?? cachedEntry.Value.ProcessName;
                                }
                            }
                        }

                        if (cachedEntry.HasValue && !_isCacheEntryExpired(cachedEntry.Value.TimestampTicks))
                        {
                            processName = cachedEntry.Value.ProcessName;
                        }

                        if (pid != 0)
                        {
                            UpdateCache(name ?? processName ?? string.Empty, processName, volPercent);
                        }

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            string normalizedAppName = NormalizeForMatching(name);
                            if (!byName.ContainsKey(normalizedAppName))
                            {
                                byName[normalizedAppName] = volPercent;
                                IndexNormalizedWords(normalizedAppName, wordIndex);
                            }
                        }
                    }
                    catch (COMException ex)
                    {
                        _logger.Trace("VolumeControlService",
                            () => $"COM exception capturing session volume: {ex.HResult:X8}");
                    }
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug("VolumeControlService",
                        () => $"{AppConstants.Audio.LogEvents.Volume.CaptureSessionVolumesComplete} | sessionCount={byPid.Count} master={masterVolumePercent}% mic={micVolumePercent}% systemSounds={systemSoundsVolumePercent}%");
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, operationName, ex);
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, operationName, ex);
            }
            finally
            {
                playbackDevice?.Dispose();
                recordingDevice?.Dispose();
            }

            return new SessionVolumeSnapshot
            {
                MasterVolumePercent = masterVolumePercent,
                MicVolumePercent = micVolumePercent,
                SystemSoundsVolumePercent = systemSoundsVolumePercent,
                ByPid = byPid,
                ByName = byName,
                WordIndex = wordIndex
            };
        }
    }
}
