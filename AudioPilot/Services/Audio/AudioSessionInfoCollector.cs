using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioSessionInfoCollector(
        IAudioDeviceEnumerator deviceEnumerator,
        Logger logger,
        AudioSessionProcessCacheCoordinator processCacheCoordinator,
        AudioSessionRecentSnapshotCache recentSnapshotCache,
        Func<string, MMDevice?> getDefaultPlaybackDevice,
        Func<string, MMDevice?> getDefaultRecordingDevice,
        Action<double, int> recordSnapshotMetric)
    {
        private const int SelectivePlaybackScanLimit = 6;
        private readonly IAudioDeviceEnumerator _deviceEnumerator = deviceEnumerator;
        private readonly Logger _logger = logger;
        private readonly AudioSessionProcessCacheCoordinator _processCacheCoordinator = processCacheCoordinator;
        private readonly AudioSessionRecentSnapshotCache _recentSnapshotCache = recentSnapshotCache;
        private readonly Func<string, MMDevice?> _getDefaultPlaybackDevice = getDefaultPlaybackDevice;
        private readonly Func<string, MMDevice?> _getDefaultRecordingDevice = getDefaultRecordingDevice;
        private readonly Action<double, int> _recordSnapshotMetric = recordSnapshotMetric;

        internal List<AudioSessionInfo> Collect(
            Action<AudioSessionInfo> onVolumeChanged,
            bool includeSessionControls,
            CancellationToken cancellationToken)
        {
            var snapshotStopwatch = Stopwatch.StartNew();
            var allSessions = new List<AudioSessionInfo>(64);
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
                defaultDevice = _getDefaultPlaybackDevice("session-list:playback-default");
                if (defaultDevice == null)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Warning("AudioSessionService", "No default playback device available");
                    }

                    return allSessions;
                }

                if (AudioDeviceHelper.TryGetEndpointVolume(_logger, defaultDevice, out var masterVolume, "session-list:playback-endpoint"))
                {
                    float masterVolumePercent = masterVolume.MasterVolumeLevelScalar * 100f;
                    string defaultDeviceName = _logger.IsEnabled(LogLevel.Debug)
                        ? defaultDevice.FriendlyName
                        : string.Empty;

                    allSessions.Add(new AudioSessionInfo(
                        "Master Volume",
                        masterVolumePercent,
                        masterVolume.Mute,
                        null,
                        onVolumeChanged,
                        defaultDeviceName));
                }

                try
                {
                    recordingDevice = _getDefaultRecordingDevice("session-list:recording-default");
                    if (recordingDevice != null &&
                        AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out var micVolume, "session-list:recording-endpoint"))
                    {
                        float micVolumePercent = micVolume.MasterVolumeLevelScalar * 100f;

                        allSessions.Add(new AudioSessionInfo(
                            "Microphone Volume",
                            micVolumePercent,
                            micVolume.Mute,
                            null,
                            onVolumeChanged,
                            _logger.IsEnabled(LogLevel.Debug)
                                ? recordingDevice.FriendlyName
                                : string.Empty));
                    }
                }
                catch (COMException ex)
                {
                    AudioDeviceHelper.LogComException(_logger, nameof(Collect), ex);
                }

                if (TryGetSystemSoundsSessionInfo(defaultDevice, includeSessionControls, onVolumeChanged, out AudioSessionInfo? systemSoundsInfo)
                    && systemSoundsInfo != null)
                {
                    allSessions.Add(systemSoundsInfo);
                    seenPids.Add(0);
                }

                AudioSessionRecentSnapshotCache.OutputSnapshotScanState scanState = _recentSnapshotCache.GetOutputScanState();
                if (playbackDevices == null || playbackDevices.Count == 0)
                {
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
                            nameof(Collect), ex);
                        return allSessions;
                    }

                    scanState = _recentSnapshotCache.GetOutputScanState();
                    useSelectivePlaybackScan = AudioSessionService.ShouldUseSelectivePlaybackDeviceScan(
                        includeSessionControls,
                        currentPlaybackFingerprint,
                        scanState.PlaybackFingerprint,
                        scanState.SessionBearingPlaybackDeviceIds?.Count ?? 0,
                        scanState.SelectivePlaybackScanStreak,
                        SelectivePlaybackScanLimit);

                    preferredPlaybackDeviceIds = useSelectivePlaybackScan && scanState.SessionBearingPlaybackDeviceIds != null
                        ? new HashSet<string>(scanState.SessionBearingPlaybackDeviceIds, StringComparer.OrdinalIgnoreCase)
                        : null;
                }

                if (playbackDevices.Count == 0)
                {
                    return allSessions;
                }

                var sessionList = new List<AudioSessionInfo>(Math.Max(16, playbackDevices.Count * 4));
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
                                if (ProcessSession(session, deviceFriendlyName, onVolumeChanged, sessionList, includeSessionControls, windowPidMap, seenPids))
                                {
                                    deviceProducedSession = true;
                                }
                            }
                            finally
                            {
                                if (!includeSessionControls)
                                {
                                    session.Dispose();
                                }
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

                DeduplicateSessionNames(sessionList);
                sessionList.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                allSessions.AddRange(sessionList);

                if (_logger.IsEnabled(LogLevel.Trace) && sessionList.Count > 0)
                {
                    _logger.Trace("AudioSessionService", () => $"All sessions found: {sessionList.Count} entries");
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioSessionService", () => $"Retrieved {allSessions.Count} total audio sessions across all devices");
                }
            }
            finally
            {
                defaultDevice?.Dispose();
                recordingDevice?.Dispose();

                snapshotStopwatch.Stop();
                _recordSnapshotMetric(snapshotStopwatch.Elapsed.TotalMilliseconds, allSessions.Count);
            }

            return allSessions;
        }

        internal static List<AudioSessionInfo> MaterializeSnapshotEntries(
            IReadOnlyList<AudioSessionSnapshot> entries,
            Action<AudioSessionInfo> onVolumeChanged)
        {
            var materialized = new List<AudioSessionInfo>(entries.Count);
            for (int index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                materialized.Add(new AudioSessionInfo(
                    entry.DisplayName,
                    entry.Volume,
                    entry.IsMuted,
                    null,
                    onVolumeChanged,
                    entry.DeviceName,
                    entry.ProcessName,
                    entry.MainWindowTitle,
                    entry.ProcessId));
            }

            return materialized;
        }

        private bool ProcessSession(
            AudioSessionControl session,
            string deviceFriendlyName,
            Action<AudioSessionInfo> onVolumeChanged,
            List<AudioSessionInfo> sessionList,
            bool includeSessionControls,
            AudioSessionService.DeferredWindowPidMap windowPidMap,
            HashSet<uint> seenPids)
        {
            try
            {
                uint processId = session.GetProcessID;

                if (AudioSessionProcessCacheCoordinator.ShouldSkipSelfSession(processId, Environment.ProcessId) ||
                    !seenPids.Add(processId))
                {
                    return false;
                }

                if (processId == 0)
                {
                    seenPids.Remove(processId);
                    return false;
                }

                if (!TryGetSessionProcessMetadata(processId, session, windowPidMap, out var processMetadata))
                {
                    seenPids.Remove(processId);
                    return false;
                }

                if (!AudioDeviceHelper.TryGetSessionVolumeAndMute(_logger, session, out float volume, out bool sessionMuted))
                {
                    seenPids.Remove(processId);
                    return false;
                }

                sessionList.Add(new AudioSessionInfo(
                    processMetadata.DisplayName,
                    volume * 100f,
                    sessionMuted,
                    includeSessionControls ? session : null,
                    onVolumeChanged,
                    deviceName: deviceFriendlyName,
                    processName: processMetadata.ProcessName,
                    mainWindowTitle: processMetadata.MainWindowTitle,
                    processId: processId));
                return true;
            }
            catch (COMException ex)
            {
                _logger.Trace("AudioSessionService",
                    () => $"COM exception processing session: {ex.HResult:X8}");
                return false;
            }
        }

        private bool TryGetSystemSoundsSessionInfo(
            MMDevice? playbackDevice,
            bool includeSessionControls,
            Action<AudioSessionInfo> onVolumeChanged,
            out AudioSessionInfo? sessionInfo)
        {
            sessionInfo = null;

            if (playbackDevice == null)
            {
                return false;
            }

            string deviceFriendlyName;
            try
            {
                deviceFriendlyName = playbackDevice.FriendlyName;
            }
            catch
            {
                deviceFriendlyName = string.Empty;
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

            AudioSessionControl? bestSession = null;
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

                    uint? processId = null;
                    try
                    {
                        processId = session.GetProcessID;
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

                    if (!AudioSessionService.IsSystemSoundsSessionCandidate(processId, sessionDisplayName))
                    {
                        session.Dispose();
                        continue;
                    }

                    int candidateScore = isSystemByName ? 2 : 1;
                    if (candidateScore <= bestCandidateScore)
                    {
                        session.Dispose();
                        continue;
                    }

                    bestSession?.Dispose();
                    bestSession = session;
                    bestCandidateScore = candidateScore;
                    session = null;
                }
                catch (COMException ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioSessionService", () => $"COM exception processing system-sounds session: {ex.HResult:X8}");
                    }

                    session?.Dispose();
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioSessionService", () => $"Failed to inspect system-sounds session at index {index}: {ex.GetType().Name}");
                    }

                    session?.Dispose();
                }
            }

            if (bestSession == null)
            {
                return false;
            }

            bool keepBestSession = false;

            try
            {
                if (!AudioDeviceHelper.TryGetSessionVolumeAndMute(_logger, bestSession, out float volume, out bool systemMuted))
                {
                    return false;
                }

                sessionInfo = new AudioSessionInfo(
                    "System Sounds",
                    volume * 100f,
                    systemMuted,
                    includeSessionControls ? bestSession : null,
                    onVolumeChanged,
                    deviceName: deviceFriendlyName,
                    processName: "System",
                    processId: 0);

                if (includeSessionControls)
                {
                    keepBestSession = true;
                }

                return true;
            }
            finally
            {
                if (!keepBestSession && bestSession != null)
                {
                    bestSession.Dispose();
                }
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

        private static void DeduplicateSessionNames(List<AudioSessionInfo> sessions)
        {
            if (sessions.Count <= 1)
            {
                return;
            }

            var grouped = new Dictionary<string, List<AudioSessionInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in sessions)
            {
                if (!grouped.TryGetValue(session.DisplayName, out var bucket))
                {
                    bucket = [];
                    grouped[session.DisplayName] = bucket;
                }

                bucket.Add(session);
            }

            foreach (var items in grouped.Values)
            {
                if (items.Count <= 1)
                {
                    continue;
                }

                bool allUniqueProcesses = true;
                var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.ProcessName) || !seenProcesses.Add(item.ProcessName))
                    {
                        allUniqueProcesses = false;
                        break;
                    }
                }

                if (allUniqueProcesses)
                {
                    foreach (var item in items)
                    {
                        if (!item.DisplayName.Contains(item.ProcessName!, StringComparison.OrdinalIgnoreCase))
                        {
                            item.DisplayName = $"{item.DisplayName} ({item.ProcessName})";
                        }
                    }

                    continue;
                }

                var itemsWithTitles = new List<(AudioSessionInfo Item, string Title)>(items.Count);
                var seenTitles = new HashSet<string>(StringComparer.Ordinal);
                bool anyTitleEmpty = false;

                foreach (var item in items)
                {
                    string title = item.MainWindowTitle?.Trim() ?? string.Empty;
                    itemsWithTitles.Add((item, title));

                    if (string.IsNullOrEmpty(title))
                    {
                        anyTitleEmpty = true;
                    }

                    seenTitles.Add(title);
                }

                if (seenTitles.Count == items.Count)
                {
                    foreach (var (item, title) in itemsWithTitles)
                    {
                        if (string.IsNullOrEmpty(title))
                        {
                            continue;
                        }

                        item.DisplayName = title.Contains(item.DisplayName, StringComparison.OrdinalIgnoreCase)
                            ? title
                            : $"{item.DisplayName} ({title})";

                        if (anyTitleEmpty &&
                            string.Equals(item.DisplayName, item.ProcessName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        {
                            item.DisplayName = $"{item.DisplayName} (Window)";
                        }
                    }
                }
                else
                {
                    for (int index = 1; index < items.Count; index++)
                    {
                        items[index].DisplayName = $"{items[index].DisplayName} ({index + 1})";
                    }
                }
            }
        }
    }
}
