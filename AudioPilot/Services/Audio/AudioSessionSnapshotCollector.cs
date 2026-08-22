using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    [ExcludeFromCodeCoverage(Justification = "Hardware-dependent Core Audio session enumeration is exercised by opt-in integration and soak tests.")]
    internal sealed partial class AudioSessionSnapshotCollector(
        IAudioDeviceEnumerator deviceEnumerator,
        Logger logger,
        Func<DeviceCacheHelper?> deviceCacheAccessor,
        AudioSessionProcessCacheCoordinator processCacheCoordinator,
        AudioSessionRecentSnapshotCache recentSnapshotCache,
        Func<string, MMDevice?> getDefaultPlaybackDevice,
        Func<string, MMDevice?> getDefaultRecordingDevice,
        Func<Role> getPlaybackDetectionRole,
        Func<Role> getRecordingDetectionRole,
        Action<double, int> recordSnapshotMetric)
    {
        private const int SelectivePlaybackScanLimit = 6;
        private readonly IAudioDeviceEnumerator _deviceEnumerator = deviceEnumerator;
        private readonly Logger _logger = logger;
        private readonly Func<DeviceCacheHelper?> _deviceCacheAccessor = deviceCacheAccessor;
        private readonly AudioSessionProcessCacheCoordinator _processCacheCoordinator = processCacheCoordinator;
        private readonly AudioSessionRecentSnapshotCache _recentSnapshotCache = recentSnapshotCache;
        private readonly Func<string, MMDevice?> _getDefaultPlaybackDevice = getDefaultPlaybackDevice;
        private readonly Func<string, MMDevice?> _getDefaultRecordingDevice = getDefaultRecordingDevice;
        private readonly Func<Role> _getPlaybackDetectionRole = getPlaybackDetectionRole;
        private readonly Func<Role> _getRecordingDetectionRole = getRecordingDetectionRole;
        private readonly Action<double, int> _recordSnapshotMetric = recordSnapshotMetric;

        internal IReadOnlyList<AudioSessionSnapshot> Collect(AudioMixerMode mixerMode, CancellationToken cancellationToken)
        {
            return mixerMode == AudioMixerMode.Input
                ? CollectInputSnapshots(cancellationToken)
                : CollectOutputSnapshots(cancellationToken);
        }

        private IReadOnlyList<AudioSessionSnapshot> CollectOutputSnapshots(CancellationToken cancellationToken)
        {
            Role playbackDetectionRole = _getPlaybackDetectionRole();
            Role recordingDetectionRole = _getRecordingDetectionRole();
            var snapshotStopwatch = Stopwatch.StartNew();
            var allSessions = new List<AudioSessionSnapshot>(64);
            string currentPlaybackFingerprint = string.Empty;
            bool useSelectivePlaybackScan = false;
            HashSet<string> sessionBearingPlaybackDeviceIds = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string>? preferredPlaybackDeviceIds = null;
            HashSet<uint> seenPids = [];

            MMDevice? defaultDevice = null;
            MMDevice? recordingDevice = null;
            List<MMDevice>? playbackDevices = null;

            try
            {
                if (_recentSnapshotCache.TryGetCachedPrimaryEndpointSnapshot(playbackDetectionRole, isPlayback: true, _deviceCacheAccessor, out var playbackEndpointSnapshot))
                {
                    allSessions.Add(new AudioSessionSnapshot(
                        "Master Volume",
                        playbackEndpointSnapshot.VolumePercent,
                        playbackEndpointSnapshot.DeviceName,
                        null,
                        null,
                        null,
                        playbackEndpointSnapshot.IsMuted,
                        EndpointId: playbackEndpointSnapshot.DeviceId));
                }
                else
                {
                    defaultDevice = TryGetPrimaryPlaybackDeviceForSnapshot(playbackDetectionRole);
                    if (defaultDevice == null)
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                        {
                            _logger.Warning("AudioSessionService", "No default playback device available");
                        }

                        return allSessions;
                    }

                    if (AudioDeviceHelper.TryGetEndpointVolume(_logger, defaultDevice, out var masterVolume, "session-snapshot:playback-endpoint"))
                    {
                        float masterVolumePercent = masterVolume.MasterVolumeLevelScalar * 100f;
                        bool isPlaybackMuted = masterVolume.Mute;
                        string defaultDeviceName = _logger.IsEnabled(LogLevel.Debug)
                            ? defaultDevice.FriendlyName
                            : string.Empty;

                        allSessions.Add(new AudioSessionSnapshot(
                            "Master Volume",
                            masterVolumePercent,
                            defaultDeviceName,
                            null,
                            null,
                            null,
                            isPlaybackMuted,
                            EndpointId: defaultDevice.ID));

                        _recentSnapshotCache.UpdatePrimaryEndpointSnapshot(isPlayback: true, defaultDevice.ID, defaultDeviceName, masterVolumePercent, isPlaybackMuted);
                    }
                }

                try
                {
                    if (_recentSnapshotCache.TryGetCachedPrimaryEndpointSnapshot(recordingDetectionRole, isPlayback: false, _deviceCacheAccessor, out var recordingEndpointSnapshot))
                    {
                        allSessions.Add(new AudioSessionSnapshot(
                            "Microphone Volume",
                            recordingEndpointSnapshot.VolumePercent,
                            recordingEndpointSnapshot.DeviceName,
                            null,
                            null,
                            null,
                            recordingEndpointSnapshot.IsMuted,
                            EndpointId: recordingEndpointSnapshot.DeviceId));
                    }
                    else
                    {
                        recordingDevice = TryGetPrimaryRecordingDeviceForSnapshot(recordingDetectionRole);
                        if (recordingDevice != null &&
                            AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out var micVolume, "session-snapshot:recording-endpoint"))
                        {
                            float micVolumePercent = micVolume.MasterVolumeLevelScalar * 100f;
                            bool isMicMuted = micVolume.Mute;
                            string recordingDeviceName = _logger.IsEnabled(LogLevel.Debug)
                                ? recordingDevice.FriendlyName
                                : string.Empty;

                            allSessions.Add(new AudioSessionSnapshot(
                                "Microphone Volume",
                                micVolumePercent,
                                recordingDeviceName,
                                null,
                                null,
                                null,
                                isMicMuted,
                                EndpointId: recordingDevice.ID));

                            _recentSnapshotCache.UpdatePrimaryEndpointSnapshot(isPlayback: false, recordingDevice.ID, recordingDeviceName, micVolumePercent, isMicMuted);
                        }
                    }
                }
                catch (COMException ex)
                {
                    AudioDeviceHelper.LogComException(_logger, nameof(CollectOutputSnapshots), ex);
                }

                defaultDevice ??= TryGetPrimaryPlaybackDeviceForSnapshot(playbackDetectionRole);

                if (TryGetSystemSoundsSnapshot(defaultDevice, out AudioSessionSnapshot systemSoundsSnapshot))
                {
                    allSessions.Add(systemSoundsSnapshot);
                }

                AudioSessionRecentSnapshotCache.OutputSnapshotScanState scanState = _recentSnapshotCache.GetOutputScanState();
                try
                {
                    using MMDeviceCollection allDevices = _deviceEnumerator.GetActivePlaybackDevices();
                    currentPlaybackFingerprint = BuildPlaybackDeviceFingerprint(allDevices);
                    playbackDevices = MaterializePlaybackDeviceList(allDevices);
                }
                catch (Exception ex)
                {
                    _logger.Error("AudioSessionService",
                        "Failed to enumerate active playback devices",
                        nameof(CollectOutputSnapshots), ex);
                    return allSessions;
                }

                scanState = _recentSnapshotCache.GetOutputScanState();
                useSelectivePlaybackScan = AudioSessionService.ShouldUseSelectivePlaybackDeviceScan(
                    includeSessionControls: false,
                    currentPlaybackFingerprint,
                    scanState.PlaybackFingerprint,
                    scanState.SessionBearingPlaybackDeviceIds?.Count ?? 0,
                    scanState.SelectivePlaybackScanStreak,
                    SelectivePlaybackScanLimit);

                preferredPlaybackDeviceIds = useSelectivePlaybackScan && scanState.SessionBearingPlaybackDeviceIds != null
                    ? new HashSet<string>(scanState.SessionBearingPlaybackDeviceIds, StringComparer.OrdinalIgnoreCase)
                    : null;

                if (playbackDevices.Count == 0)
                {
                    return allSessions;
                }

                var sessionList = new List<AudioSessionSnapshot>(Math.Max(16, playbackDevices.Count * 4));
                var windowPidMap = new AudioSessionService.DeferredWindowPidMap();

                for (int deviceIndex = 0; deviceIndex < playbackDevices.Count; deviceIndex++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    MMDevice? device = playbackDevices[deviceIndex];
                    try
                    {
                        string? deviceId = null;
                        try
                        {
                            deviceId = device?.ID;
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.Trace("AudioSessionService", () => $"Failed to read playback device id at index {deviceIndex}: {ex.GetType().Name}");
                            }
                        }

                        if (useSelectivePlaybackScan &&
                            (string.IsNullOrWhiteSpace(deviceId) || preferredPlaybackDeviceIds == null || !preferredPlaybackDeviceIds.Contains(deviceId)))
                        {
                            continue;
                        }

                        if (device == null)
                        {
                            continue;
                        }

                        using AudioSessionManager sessionManager = device.AudioSessionManager;
                        using SessionCollection sessions = sessionManager.Sessions;
                        int sessionCount = sessions.Count;
                        if (sessionCount == 0)
                        {
                            continue;
                        }

                        string deviceFriendlyName = device.FriendlyName;
                        bool deviceProducedSession = false;

                        for (int sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[sessionIndex];
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsEnabled(LogLevel.Trace))
                                {
                                    _logger.Trace("AudioSessionService", () => $"Failed to get session at index {sessionIndex}: {ex.GetType().Name}");
                                }

                                continue;
                            }

                            if (session == null)
                            {
                                continue;
                            }

                            try
                            {
                                if (ProcessSnapshotSession(session, deviceFriendlyName, deviceId ?? string.Empty, sessionList, windowPidMap, seenPids, includeSystemSounds: false))
                                {
                                    deviceProducedSession = true;
                                }
                            }
                            finally
                            {
                                session.Dispose();
                            }
                        }

                        if (deviceProducedSession && !string.IsNullOrWhiteSpace(deviceId))
                        {
                            sessionBearingPlaybackDeviceIds.Add(deviceId);
                        }
                    }
                    finally
                    {
                        try
                        {
                            device?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.Trace("AudioSessionService", () => $"Failed to dispose playback device snapshot: {ex.GetType().Name}");
                            }
                        }
                    }
                }

                DeduplicateSnapshotNames(sessionList);
                sessionList.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                allSessions.AddRange(sessionList);
            }
            finally
            {
                defaultDevice?.Dispose();
                recordingDevice?.Dispose();

                snapshotStopwatch.Stop();
                _recordSnapshotMetric(snapshotStopwatch.Elapsed.TotalMilliseconds, allSessions.Count);
            }

            return _recentSnapshotCache.UpdateRecentNoControlsSnapshot(
                AudioMixerMode.Output,
                allSessions,
                currentPlaybackFingerprint,
                sessionBearingPlaybackDeviceIds,
                useSelectivePlaybackScan);
        }

        private IReadOnlyList<AudioSessionSnapshot> CollectInputSnapshots(CancellationToken cancellationToken)
        {
            Role playbackDetectionRole = _getPlaybackDetectionRole();
            Role recordingDetectionRole = _getRecordingDetectionRole();
            List<AudioSessionSnapshot> inputSessions = GetSharedMixerSnapshotsForRecordingMixer(playbackDetectionRole, recordingDetectionRole);
            HashSet<uint> seenPids = [];

            if (_deviceEnumerator is not AudioDeviceService audioDeviceService)
            {
                return inputSessions;
            }

            List<MMDevice> captureDevices;
            try
            {
                using MMDeviceCollection activeCaptureDevices = audioDeviceService.GetActiveCaptureDevices();
                captureDevices = AudioDeviceCollectionHelper.MaterializeDevices(activeCaptureDevices);
            }
            catch (Exception ex)
            {
                _logger.Error(
                    "AudioSessionService",
                    "Failed to enumerate active capture devices",
                    nameof(CollectInputSnapshots),
                    ex);
                return inputSessions;
            }

            if (captureDevices.Count == 0)
            {
                return inputSessions;
            }

            var windowPidMap = new AudioSessionService.DeferredWindowPidMap();
            for (int deviceIndex = 0; deviceIndex < captureDevices.Count; deviceIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                MMDevice? device = captureDevices[deviceIndex];
                try
                {
                    if (device == null)
                    {
                        continue;
                    }

                    using AudioSessionManager sessionManager = device.AudioSessionManager;
                    using SessionCollection sessions = sessionManager.Sessions;
                    int sessionCount = sessions.Count;
                    if (sessionCount == 0)
                    {
                        continue;
                    }

                    string deviceFriendlyName = device.FriendlyName;
                    for (int sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        AudioSessionControl? session = null;
                        try
                        {
                            session = sessions[sessionIndex];
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.Trace("AudioSessionService", () => $"Failed to get capture session at index {sessionIndex}: {ex.GetType().Name}");
                            }

                            continue;
                        }

                        if (session == null)
                        {
                            continue;
                        }

                        try
                        {
                            ProcessSnapshotSession(session, deviceFriendlyName, device.ID, inputSessions, windowPidMap, seenPids, includeSystemSounds: false);
                        }
                        finally
                        {
                            session.Dispose();
                        }
                    }
                }
                finally
                {
                    device?.Dispose();
                }
            }

            return _recentSnapshotCache.UpdateRecentNoControlsSnapshot(AudioMixerMode.Input, inputSessions);
        }

        private List<AudioSessionSnapshot> GetSharedMixerSnapshotsForRecordingMixer(Role playbackDetectionRole, Role recordingDetectionRole)
        {
            var sharedSessions = new List<AudioSessionSnapshot>(3);
            MMDevice? playbackDevice = null;
            MMDevice? recordingDevice = null;

            try
            {
                if (_recentSnapshotCache.TryGetCachedPrimaryEndpointSnapshot(playbackDetectionRole, isPlayback: true, _deviceCacheAccessor, out var playbackEndpointSnapshot))
                {
                    sharedSessions.Add(new AudioSessionSnapshot(
                        "Master Volume",
                        playbackEndpointSnapshot.VolumePercent,
                        playbackEndpointSnapshot.DeviceName,
                        null,
                        null,
                        null,
                        playbackEndpointSnapshot.IsMuted,
                        EndpointId: playbackEndpointSnapshot.DeviceId));
                }
                else
                {
                    playbackDevice = TryGetPrimaryPlaybackDeviceForSnapshot(playbackDetectionRole);
                    if (playbackDevice != null &&
                        AudioDeviceHelper.TryGetEndpointVolume(_logger, playbackDevice, out var masterVolume, "session-snapshot:recording-shared-playback-endpoint"))
                    {
                        float masterVolumePercent = masterVolume.MasterVolumeLevelScalar * 100f;
                        bool isPlaybackMuted = masterVolume.Mute;
                        string playbackDeviceName = _logger.IsEnabled(LogLevel.Debug)
                            ? playbackDevice.FriendlyName
                            : string.Empty;

                        sharedSessions.Add(new AudioSessionSnapshot(
                            "Master Volume",
                            masterVolumePercent,
                            playbackDeviceName,
                            null,
                            null,
                            null,
                            isPlaybackMuted,
                            EndpointId: playbackDevice.ID));

                        _recentSnapshotCache.UpdatePrimaryEndpointSnapshot(isPlayback: true, playbackDevice.ID, playbackDeviceName, masterVolumePercent, isPlaybackMuted);
                    }
                }

                try
                {
                    if (_recentSnapshotCache.TryGetCachedPrimaryEndpointSnapshot(recordingDetectionRole, isPlayback: false, _deviceCacheAccessor, out var recordingEndpointSnapshot))
                    {
                        sharedSessions.Add(new AudioSessionSnapshot(
                            "Microphone Volume",
                            recordingEndpointSnapshot.VolumePercent,
                            recordingEndpointSnapshot.DeviceName,
                            null,
                            null,
                            null,
                            recordingEndpointSnapshot.IsMuted,
                            EndpointId: recordingEndpointSnapshot.DeviceId));
                    }
                    else
                    {
                        recordingDevice = TryGetPrimaryRecordingDeviceForSnapshot(recordingDetectionRole);
                        if (recordingDevice != null &&
                            AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out var micVolume, "session-snapshot:recording-shared-capture-endpoint"))
                        {
                            float micVolumePercent = micVolume.MasterVolumeLevelScalar * 100f;
                            bool isMicMuted = micVolume.Mute;
                            string recordingDeviceName = _logger.IsEnabled(LogLevel.Debug)
                                ? recordingDevice.FriendlyName
                                : string.Empty;

                            sharedSessions.Add(new AudioSessionSnapshot(
                                "Microphone Volume",
                                micVolumePercent,
                                recordingDeviceName,
                                null,
                                null,
                                null,
                                isMicMuted,
                                EndpointId: recordingDevice.ID));

                            _recentSnapshotCache.UpdatePrimaryEndpointSnapshot(isPlayback: false, recordingDevice.ID, recordingDeviceName, micVolumePercent, isMicMuted);
                        }
                    }
                }
                catch (COMException ex)
                {
                    AudioDeviceHelper.LogComException(_logger, nameof(GetSharedMixerSnapshotsForRecordingMixer), ex);
                }

                playbackDevice ??= TryGetPrimaryPlaybackDeviceForSnapshot(playbackDetectionRole);
                if (TryGetSystemSoundsSnapshot(playbackDevice, out AudioSessionSnapshot systemSoundsSnapshot))
                {
                    sharedSessions.Add(systemSoundsSnapshot);
                }
            }
            finally
            {
                playbackDevice?.Dispose();
                recordingDevice?.Dispose();
            }

            return sharedSessions;
        }

        private bool TryGetSystemSoundsSnapshot(MMDevice? playbackDevice, out AudioSessionSnapshot snapshot)
        {
            snapshot = default;

            if (playbackDevice == null)
            {
                return false;
            }

            string deviceFriendlyName;
            string endpointId;
            try
            {
                deviceFriendlyName = playbackDevice.FriendlyName;
                endpointId = playbackDevice.ID;
            }
            catch
            {
                deviceFriendlyName = string.Empty;
                endpointId = string.Empty;
            }

            AudioSessionManager sessionManager;
            try
            {
                sessionManager = playbackDevice.AudioSessionManager;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioSessionService", () => $"Failed to enumerate system-sounds session collection: {ex.GetType().Name}");
                }

                return false;
            }

            SessionCollection sessions;
            try
            {
                sessions = sessionManager.Sessions;
            }
            catch (Exception ex)
            {
                sessionManager.Dispose();
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioSessionService", () => $"Failed to enumerate system-sounds session collection: {ex.GetType().Name}");
                }

                return false;
            }

            using AudioSessionManager ownedSessionManager = sessionManager;
            using SessionCollection ownedSessions = sessions;

            bool foundCandidate = false;
            int bestCandidateScore = -1;

            for (int index = 0; index < sessions.Count; index++)
            {
                AudioSessionControl? session = null;
                try
                {
                    session = sessions[index];
                    if (session == null)
                    {
                        continue;
                    }

                    bool isSystemByPid = false;
                    try
                    {
                        isSystemByPid = session.GetProcessID == 0;
                    }
                    catch
                    {
                    }

                    string? sessionDisplayName = null;
                    bool isSystemByName = false;
                    try
                    {
                        sessionDisplayName = session.DisplayName;
                        isSystemByName =
                            string.Equals(sessionDisplayName, "System Sounds", StringComparison.OrdinalIgnoreCase)
                            || (sessionDisplayName?.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase) == true);
                    }
                    catch
                    {
                    }

                    bool isSystemCandidate = AudioSessionService.IsSystemSoundsSessionCandidate(
                        isSystemByPid ? 0u : null,
                        sessionDisplayName);

                    if (!isSystemCandidate)
                    {
                        continue;
                    }

                    if (!AudioDeviceHelper.TryGetSessionVolumeAndMute(_logger, session, out float volume, out bool muted))
                    {
                        continue;
                    }

                    int candidateScore = isSystemByName ? 2 : 1;
                    if (candidateScore <= bestCandidateScore)
                    {
                        continue;
                    }

                    snapshot = new AudioSessionSnapshot(
                        "System Sounds",
                        volume * 100f,
                        deviceFriendlyName,
                        "System",
                        null,
                        0,
                        muted,
                        TryGetSessionInstanceId(session),
                        endpointId);
                    bestCandidateScore = candidateScore;
                    foundCandidate = true;
                }
                catch (COMException ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioSessionService", () => $"COM exception processing system-sounds snapshot: {ex.HResult:X8}");
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioSessionService", () => $"Failed to inspect system-sounds session at index {index}: {ex.GetType().Name}");
                    }
                }
                finally
                {
                    session?.Dispose();
                }
            }

            return foundCandidate;
        }

        private bool ProcessSnapshotSession(
            AudioSessionControl session,
            string deviceFriendlyName,
            string endpointId,
            List<AudioSessionSnapshot> sessionList,
            AudioSessionService.DeferredWindowPidMap windowPidMap,
            HashSet<uint> seenPids,
            bool includeSystemSounds = true)
        {
            try
            {
                uint processId = session.GetProcessID;

                if (AudioSessionProcessCacheCoordinator.ShouldSkipSelfSession(processId, Environment.ProcessId) ||
                    !seenPids.Add(processId))
                {
                    return false;
                }

                if (!AudioDeviceHelper.TryGetSessionVolumeAndMute(_logger, session, out float volume, out bool sessionMuted))
                {
                    seenPids.Remove(processId);
                    return false;
                }

                if (processId == 0)
                {
                    if (!includeSystemSounds)
                    {
                        seenPids.Remove(processId);
                        return false;
                    }

                    sessionList.Add(new AudioSessionSnapshot(
                        "System Sounds",
                        volume * 100f,
                        deviceFriendlyName,
                        "System",
                        null,
                        0,
                        sessionMuted,
                        TryGetSessionInstanceId(session),
                        endpointId));

                    return true;
                }

                if (!TryGetSessionProcessMetadata(processId, session, windowPidMap, out var processMetadata))
                {
                    seenPids.Remove(processId);
                    return false;
                }

                sessionList.Add(new AudioSessionSnapshot(
                    processMetadata.DisplayName,
                    volume * 100f,
                    deviceFriendlyName,
                    processMetadata.ProcessName,
                    processMetadata.MainWindowTitle,
                    processId,
                    sessionMuted,
                    TryGetSessionInstanceId(session),
                    endpointId));
                return true;
            }
            catch (COMException ex)
            {
                _logger.Trace("AudioSessionService",
                    () => $"COM exception processing snapshot session: {ex.HResult:X8}");
                return false;
            }
        }

        private static string TryGetSessionInstanceId(AudioSessionControl session)
        {
            try
            {
                string? instanceId = session.GetSessionInstanceIdentifier;
                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    return instanceId;
                }
            }
            catch
            {
            }

            try
            {
                return session.GetSessionIdentifier ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool TryGetSessionProcessMetadata(
            uint processId,
            AudioSessionControl session,
            AudioSessionService.DeferredWindowPidMap windowPidMap,
            out AudioSessionProcessCacheCoordinator.SessionProcessMetadata metadata)
        {
            metadata = default;

            if (!_processCacheCoordinator.TryGetOrAddEntry(
                processId,
                () =>
                {
                    (string processName, string? displayName, string? mainWindowTitle) = ResolveProcessInfoSync(processId, session, windowPidMap);
                    return processName == null
                        ? null
                        : AudioSessionProcessCacheCoordinator.CacheEntry.Create(processName, displayName, mainWindowTitle);
                },
                out var cacheEntry))
            {
                return false;
            }

            return AudioSessionProcessCacheCoordinator.TryProjectSessionProcessMetadata(cacheEntry, out metadata);
        }

        private (string processName, string? displayName, string? mainWindowTitle) ResolveProcessInfoSync(
            uint processId,
            AudioSessionControl session,
            AudioSessionService.DeferredWindowPidMap windowPidMap)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName;
                string displayName = session.DisplayName;
                string? mainWindowTitle = null;

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return (AudioDeviceHelper.SanitizeProcessName(processName), displayName, mainWindowTitle);
                }

                try
                {
                    mainWindowTitle = process.MainWindowTitle;
                }
                catch (Exception ex)
                {
                    _logger.Trace("AudioSessionService", () => $"Failed to get MainWindowTitle for process {LogPrivacy.Id(processId.ToString())}: {ex.GetType().Name}");
                }

                if (!string.IsNullOrWhiteSpace(mainWindowTitle) &&
                    !AudioDeviceHelper.IsInternalWindowTitle(mainWindowTitle))
                {
                    displayName = mainWindowTitle;
                }

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return (AudioDeviceHelper.SanitizeProcessName(processName), displayName, mainWindowTitle);
                }

                int parentPid = AudioDeviceHelper.GetParentPid((int)processId);
                bool isActualChild = parentPid != 0 && parentPid != (int)processId;
                bool isKnownHelper = AppConstants.Audio.KnownHelperProcesses
                    .Contains(processName, StringComparer.OrdinalIgnoreCase);
                bool isChildProcess = isActualChild && isKnownHelper;

                if (isChildProcess)
                {
                    string? registeredName = AudioDeviceHelper.GetProcessRegisteredName((uint)parentPid, 0, windowPidMap.GetOrCreate());
                    if (!string.IsNullOrEmpty(registeredName))
                    {
                        processName = AudioDeviceHelper.SanitizeProcessName(registeredName);
                    }
                    else
                    {
                        using var realAppProcess = AudioDeviceHelper.FindRealAppProcess(process);
                        processName = realAppProcess != null
                            ? AudioDeviceHelper.SanitizeProcessName(realAppProcess.ProcessName)
                            : AudioDeviceHelper.SanitizeProcessName(processName);
                    }
                }
                else
                {
                    string? fileDesc = AudioDeviceHelper.GetFileDescription((int)processId);
                    processName = !string.IsNullOrEmpty(fileDesc)
                        ? fileDesc
                        : AudioDeviceHelper.SanitizeProcessName(processName);
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = processName;
                }

                return (processName, displayName, mainWindowTitle);
            }
            catch (ArgumentException)
            {
                return (null!, null, null);
            }
            catch (InvalidOperationException)
            {
                return (null!, null, null);
            }
            finally
            {
                process?.Dispose();
            }
        }

        private MMDevice? TryGetPrimaryPlaybackDeviceForSnapshot(Role detectionRole)
        {
            DeviceCacheHelper? deviceCache = _deviceCacheAccessor();
            if (deviceCache != null)
            {
                try
                {
                    var cachedDevice = deviceCache.GetPlaybackDeviceWithoutRefresh(detectionRole);
                    if (cachedDevice != null)
                    {
                        return cachedDevice;
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioSessionService", () => $"Primary playback cache lookup failed: {ex.GetType().Name}");
                    }
                }
            }

            return _getDefaultPlaybackDevice("session-snapshot:playback-default");
        }

        private MMDevice? TryGetPrimaryRecordingDeviceForSnapshot(Role detectionRole)
        {
            DeviceCacheHelper? deviceCache = _deviceCacheAccessor();
            if (deviceCache != null)
            {
                try
                {
                    var cachedDevice = deviceCache.GetRecordingDeviceWithoutRefresh(detectionRole);
                    if (cachedDevice != null)
                    {
                        return cachedDevice;
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioSessionService", () => $"Primary recording cache lookup failed: {ex.GetType().Name}");
                    }
                }
            }

            return _getDefaultRecordingDevice("session-snapshot:recording-default");
        }

        private static List<MMDevice> MaterializePlaybackDeviceList(MMDeviceCollection devices)
        {
            var materialized = new List<MMDevice>(devices.Count);
            for (int index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                if (device != null)
                {
                    materialized.Add(device);
                }
            }

            return materialized;
        }

        private static string BuildPlaybackDeviceFingerprint(MMDeviceCollection devices)
        {
            var builder = new System.Text.StringBuilder(devices.Count * 48);
            for (int index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                if (device == null)
                {
                    continue;
                }

                try
                {
                    builder.Append(device.ID);
                    builder.Append('|');
                }
                catch
                {
                }
            }

            return builder.ToString();
        }

        private static void DeduplicateSnapshotNames(List<AudioSessionSnapshot> sessions)
        {
            if (sessions.Count <= 1)
            {
                return;
            }

            var grouped = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < sessions.Count; index++)
            {
                string displayName = sessions[index].DisplayName;
                if (!grouped.TryGetValue(displayName, out var bucket))
                {
                    bucket = [];
                    grouped[displayName] = bucket;
                }

                bucket.Add(index);
            }

            foreach (var items in grouped.Values)
            {
                if (items.Count <= 1)
                {
                    continue;
                }

                bool allUniqueProcesses = true;
                var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int index = 0; index < items.Count; index++)
                {
                    var item = sessions[items[index]];
                    if (string.IsNullOrEmpty(item.ProcessName) || !seenProcesses.Add(item.ProcessName))
                    {
                        allUniqueProcesses = false;
                        break;
                    }
                }

                if (allUniqueProcesses)
                {
                    for (int index = 0; index < items.Count; index++)
                    {
                        int sessionIndex = items[index];
                        var item = sessions[sessionIndex];
                        if (!item.DisplayName.Contains(item.ProcessName!, StringComparison.OrdinalIgnoreCase))
                        {
                            sessions[sessionIndex] = item with { DisplayName = $"{item.DisplayName} ({item.ProcessName})" };
                        }
                    }

                    continue;
                }

                var itemsWithTitles = new List<(int Index, string Title)>(items.Count);
                var seenTitles = new HashSet<string>(StringComparer.Ordinal);
                bool anyTitleEmpty = false;

                for (int index = 0; index < items.Count; index++)
                {
                    int sessionIndex = items[index];
                    string title = sessions[sessionIndex].MainWindowTitle?.Trim() ?? string.Empty;
                    itemsWithTitles.Add((sessionIndex, title));

                    if (string.IsNullOrEmpty(title))
                    {
                        anyTitleEmpty = true;
                    }

                    seenTitles.Add(title);
                }

                if (seenTitles.Count == items.Count)
                {
                    for (int index = 0; index < itemsWithTitles.Count; index++)
                    {
                        var (sessionIndex, title) = itemsWithTitles[index];
                        if (string.IsNullOrEmpty(title))
                        {
                            continue;
                        }

                        var item = sessions[sessionIndex];
                        string updatedDisplayName = title.Contains(item.DisplayName, StringComparison.OrdinalIgnoreCase)
                            ? title
                            : $"{item.DisplayName} ({title})";

                        if (anyTitleEmpty &&
                            string.Equals(updatedDisplayName, item.ProcessName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        {
                            updatedDisplayName = $"{updatedDisplayName} (Window)";
                        }

                        sessions[sessionIndex] = item with { DisplayName = updatedDisplayName };
                    }
                }
                else
                {
                    for (int index = 1; index < items.Count; index++)
                    {
                        int sessionIndex = items[index];
                        var item = sessions[sessionIndex];
                        sessions[sessionIndex] = item with { DisplayName = $"{item.DisplayName} ({index + 1})" };
                    }
                }
            }
        }
    }
}
