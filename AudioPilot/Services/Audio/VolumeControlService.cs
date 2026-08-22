using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    public sealed class SessionVolumeSnapshot
    {
        public float? MasterVolumePercent { get; init; }
        public float? MicVolumePercent { get; init; }
        public float? SystemSoundsVolumePercent { get; init; }
        public Dictionary<uint, float> ByPid { get; init; } = [];
        public Dictionary<string, float> ByName { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> WordIndex { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime SnapshotTime { get; init; } = DateTime.UtcNow;
    }

    public partial class VolumeControlService : IDisposable
    {
        internal readonly record struct PendingSessionRestore(
            SessionVolumeSnapshot Snapshot,
            string TargetDeviceId,
            DateTime ExpiresAtUtc);

        internal readonly record struct PendingSessionRestoreResolution(
            SessionVolumeSnapshot? Snapshot,
            bool ShouldClearPending);

        internal readonly record struct SessionVolumeApplicationCandidate(
            uint Pid,
            string? DisplayName,
            string? ProcessName,
            float CurrentVolumePercent,
            bool IsSystemSounds = false);

        internal enum SessionVolumeApplicationAction
        {
            None,
            Skip,
            Apply,
        }

        internal readonly record struct SessionVolumeApplicationPlan(
            SessionVolumeApplicationAction Action,
            string? SessionLabel,
            string? MatchMethod,
            float? CurrentVolumePercent,
            float? TargetVolumePercent,
            bool ShouldTraceFuzzyMatch);

        private readonly IAudioDeviceEnumerator _deviceEnumerator;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, string> _normalizedNameCache = new(StringComparer.Ordinal);
        private readonly ConcurrentBag<HashSet<string>> _wordSetPool = [];
        private readonly TimeSpan _retryStateTtl = TimeSpan.FromMinutes(AppConstants.Timing.RetryStateTtlMinutes);
        private readonly TimeSpan _volumeCacheTtl = TimeSpan.FromMinutes(AppConstants.Timing.VolumeCacheTtlMinutes);
        private const int MaxVolumeCacheEntries = AppConstants.Limits.MaxVolumeCacheEntries;
        private const int MaxVolumeAliasEntries = AppConstants.Limits.MaxVolumeAliasEntries;
        private const int MaxNormalizedNameCacheEntries = AppConstants.Limits.MaxVolumeAliasEntries * 2;
        private readonly Func<uint, (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)?> _lookupProcessInfo;
        private readonly Func<long, bool> _isCacheEntryExpired;
        private readonly VolumeSnapshotMatcher _snapshotMatcher;
        private readonly VolumeRetryStateTracker _retryStateTracker;
        private readonly VolumeCacheStore _cacheStore;
        private readonly Lock _pendingRestoreLock = new();
        private PendingSessionRestore? _pendingRestore;
        private static readonly TimeSpan s_pendingRestoreTtl = TimeSpan.FromSeconds(AppConstants.Timing.PendingRestoreTtlSeconds);
        private static readonly TimeSpan s_pidFallbackFreshness = TimeSpan.FromSeconds(AppConstants.Timing.PidFallbackFreshnessSeconds);
        private const int MaxSessionApplyFailureDetails = 3;
        private volatile bool _disposed;

        public VolumeControlService(
            IAudioDeviceEnumerator deviceEnumerator,
            Func<uint, (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)?> lookupProcessInfo,
            Func<long, bool> isCacheEntryExpired)
        {
            _deviceEnumerator = deviceEnumerator;
            _lookupProcessInfo = lookupProcessInfo;
            _isCacheEntryExpired = isCacheEntryExpired;
            _logger = Logger.Instance;
            _snapshotMatcher = new VolumeSnapshotMatcher(
                _logger,
                _normalizedNameCache,
                _wordSetPool,
                MaxNormalizedNameCacheEntries,
                s_pidFallbackFreshness);
            _retryStateTracker = new VolumeRetryStateTracker(
                _retryStateTtl,
                TimeSpan.FromSeconds(AppConstants.Timing.CircuitBreakerCooldownSeconds));
            _cacheStore = new VolumeCacheStore(
                _logger,
                _normalizedNameCache,
                NormalizeForMatching,
                _volumeCacheTtl,
                MaxVolumeCacheEntries,
                MaxVolumeAliasEntries,
                MaxNormalizedNameCacheEntries);
            _logger.Info("VolumeControlService", "service-init");
        }

        private MMDevice? GetDefaultPlaybackDevice(string reason)
        {
            if (_deviceEnumerator is AudioDeviceService audioDeviceService)
            {
                return audioDeviceService.GetDefaultPlaybackDevice(reason);
            }

            return _deviceEnumerator.GetDefaultPlaybackDevice();
        }

        private MMDevice? GetDefaultRecordingDevice(string reason)
        {
            if (_deviceEnumerator is AudioDeviceService audioDeviceService)
            {
                return audioDeviceService.GetDefaultRecordingDevice(reason);
            }

            return _deviceEnumerator.GetDefaultRecordingDevice();
        }

        private void ResetRetryState(string deviceId)
        {
            _retryStateTracker.Reset(deviceId);
        }

        private void RecordRetryFailure(string deviceId)
        {
            _retryStateTracker.RecordFailure(deviceId);
        }



        public void ApplySavedVolume(AudioSessionControl session, string processName, string displayName)
        {
            if (session == null || _disposed) return;

            string lookupName = displayName;
            string processNameNormalized = NormalizeForMatching(processName);
            float? savedVolume = null;

            uint pid = 0;
            try
            {
                pid = session.GetProcessID;
            }
            catch
            {
            }

            SessionVolumeSnapshot? pendingSnapshot = GetPendingSnapshotForCurrentPlaybackDevice();
            if (pendingSnapshot != null)
            {
                string normalizedLookupName = NormalizeForMatching(lookupName);
                if (TryResolveSnapshotTarget(
                    pendingSnapshot,
                    pid,
                    normalizedLookupName,
                    processNameNormalized,
                    nowUtc: null,
                    out float pendingTarget,
                    out string pendingMethod))
                {
                    savedVolume = pendingTarget;
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.SavedVolumeMatch} | method=pending-{pendingMethod} key={LogPrivacy.Label(normalizedLookupName)} volume={savedVolume.Value}%");
                    }
                    UpdateCache(lookupName, processName, savedVolume.Value);
                }
            }

            if (savedVolume == null && !string.IsNullOrWhiteSpace(lookupName))
            {
                string normalizedName = NormalizeForMatching(lookupName);
                savedVolume = TryGetCachedVolume(normalizedName);
                if (savedVolume.HasValue)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.SavedVolumeMatch} | method=display-name key={LogPrivacy.Label(normalizedName)} volume={savedVolume.Value}%");
                    }
                }
            }

            if (savedVolume == null && !string.IsNullOrWhiteSpace(processNameNormalized))
            {
                savedVolume = TryGetCachedVolume(processNameNormalized);
                if (savedVolume.HasValue)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.SavedVolumeMatch} | method=process-name key={LogPrivacy.Label(processNameNormalized)} volume={savedVolume.Value}%");
                    }
                }
                else
                {
                    string sanitized = AudioDeviceHelper.SanitizeProcessName(processName);
                    string normalizedSanitized = NormalizeForMatching(sanitized);
                    if (normalizedSanitized != processNameNormalized)
                    {
                        savedVolume = TryGetCachedVolume(normalizedSanitized);
                        if (savedVolume.HasValue)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.Trace("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.SavedVolumeMatch} | method=sanitized-process key={LogPrivacy.Process(sanitized)} volume={savedVolume.Value}%");
                            }
                        }
                    }
                }
            }

            if (savedVolume.HasValue)
            {
                try
                {
                    if (AudioDeviceHelper.TryGetSessionVolume(_logger, session, out float currentVol))
                    {
                        float currentVolPercent = currentVol * 100f;
                        if (Math.Abs(currentVolPercent - savedVolume.Value) > 1.0f)
                        {
                            if (AudioDeviceHelper.TrySetSessionVolume(_logger, session, savedVolume.Value / 100f))
                            {
                                if (_logger.IsEnabled(LogLevel.Info))
                                {
                                    _logger.Info("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.RestoreSavedVolumeSuccess} | displayName={LogPrivacy.Session(lookupName)} processName={LogPrivacy.Process(processName)} targetVolume={savedVolume.Value:F0}%");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.RestoreSavedVolumeFailed} | displayName={LogPrivacy.Session(lookupName)} processName={LogPrivacy.Process(processName)}", nameof(ApplySavedVolume), ex);
                }
            }
        }

        public void RegisterPostSwitchSnapshot(SessionVolumeSnapshot snapshot, string targetDeviceId)
        {
            if (_disposed || snapshot == null || string.IsNullOrWhiteSpace(targetDeviceId))
            {
                return;
            }

            bool isEmptySnapshot =
                !snapshot.MasterVolumePercent.HasValue &&
                !snapshot.MicVolumePercent.HasValue &&
                !snapshot.SystemSoundsVolumePercent.HasValue &&
                snapshot.ByPid.Count == 0 &&
                snapshot.ByName.Count == 0;

            if (isEmptySnapshot)
            {
                return;
            }

            var pending = new PendingSessionRestore(
                snapshot,
                targetDeviceId,
                DateTime.UtcNow.Add(s_pendingRestoreTtl));

            lock (_pendingRestoreLock)
            {
                _pendingRestore = pending;
            }
        }

        private SessionVolumeSnapshot? GetPendingSnapshotForCurrentPlaybackDevice()
        {
            MMDevice? playbackDevice = null;
            try
            {
                playbackDevice = GetDefaultPlaybackDevice("pending-snapshot:playback");
                if (playbackDevice == null)
                {
                    return null;
                }

                return GetPendingSnapshotForPlaybackDeviceId(playbackDevice.ID, DateTime.UtcNow);
            }
            catch
            {
                return null;
            }
            finally
            {
                playbackDevice?.Dispose();
            }
        }

        internal SessionVolumeSnapshot? GetPendingSnapshotForPlaybackDeviceId(string? currentPlaybackDeviceId, DateTime nowUtc)
        {
            lock (_pendingRestoreLock)
            {
                PendingSessionRestoreResolution resolution = ResolvePendingSnapshotForPlaybackDevice(
                    _pendingRestore,
                    currentPlaybackDeviceId,
                    nowUtc);

                if (resolution.ShouldClearPending)
                {
                    _pendingRestore = null;
                }

                return resolution.Snapshot;
            }
        }

        internal static PendingSessionRestoreResolution ResolvePendingSnapshotForPlaybackDevice(
            PendingSessionRestore? pending,
            string? currentPlaybackDeviceId,
            DateTime nowUtc)
        {
            if (!pending.HasValue)
            {
                return new PendingSessionRestoreResolution(null, ShouldClearPending: false);
            }

            if (nowUtc > pending.Value.ExpiresAtUtc)
            {
                return new PendingSessionRestoreResolution(null, ShouldClearPending: true);
            }

            if (string.IsNullOrWhiteSpace(currentPlaybackDeviceId) ||
                !string.Equals(currentPlaybackDeviceId, pending.Value.TargetDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return new PendingSessionRestoreResolution(null, ShouldClearPending: false);
            }

            return new PendingSessionRestoreResolution(pending.Value.Snapshot, ShouldClearPending: false);
        }

        internal static string BuildSessionApplyFailureDetail(
            string? displayName,
            uint pid,
            string? matchMethod,
            float? currentVolume,
            float? targetVolume,
            string reason)
        {
            string method = string.IsNullOrWhiteSpace(matchMethod) ? "unknown" : matchMethod;
            string current = currentVolume.HasValue ? $"{currentVolume.Value:F1}%" : "unknown";
            string target = targetVolume.HasValue ? $"{targetVolume.Value:F1}%" : "unknown";
            return $"session={LogPrivacy.Session(displayName ?? pid.ToString())} pid={LogPrivacy.Id(pid.ToString(System.Globalization.CultureInfo.InvariantCulture))} method={method} current={current} target={target} reason={reason}";
        }

        internal static string BuildSessionApplyFailureSummary(IReadOnlyList<string> failureDetails, int suppressedFailureCount)
        {
            if (failureDetails.Count == 0 && suppressedFailureCount <= 0)
            {
                return "failureDetails=none";
            }

            string detailText = failureDetails.Count == 0
                ? "none"
                : string.Join(" || ", failureDetails);
            return $"failureDetails={detailText} suppressedFailureDetails={suppressedFailureCount}";
        }

        internal SessionVolumeApplicationPlan BuildSessionVolumeApplicationPlan(
            SessionVolumeSnapshot snapshot,
            SessionVolumeApplicationCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            string? sessionLabel = candidate.DisplayName ?? candidate.ProcessName;
            string normalizedDisplayName = string.IsNullOrWhiteSpace(candidate.DisplayName)
                ? string.Empty
                : NormalizeForMatching(candidate.DisplayName);
            string normalizedProcessName = string.IsNullOrWhiteSpace(candidate.ProcessName)
                ? string.Empty
                : NormalizeForMatching(candidate.ProcessName);

            if (candidate.IsSystemSounds || candidate.Pid == 0)
            {
                if (!snapshot.SystemSoundsVolumePercent.HasValue)
                {
                    return new SessionVolumeApplicationPlan(
                        SessionVolumeApplicationAction.None,
                        sessionLabel,
                        MatchMethod: null,
                        candidate.CurrentVolumePercent,
                        TargetVolumePercent: null,
                        ShouldTraceFuzzyMatch: false);
                }
                sessionLabel ??= "System Sounds";

                float targetVolume = snapshot.SystemSoundsVolumePercent.Value;
                bool requiresApply = Math.Abs(candidate.CurrentVolumePercent - targetVolume) > 0.5f;
                return new SessionVolumeApplicationPlan(
                    requiresApply ? SessionVolumeApplicationAction.Apply : SessionVolumeApplicationAction.Skip,
                    sessionLabel,
                    "SystemSounds",
                    candidate.CurrentVolumePercent,
                    targetVolume,
                    ShouldTraceFuzzyMatch: false);
            }

            if (TryResolveSnapshotTarget(
                snapshot,
                candidate.Pid,
                normalizedDisplayName,
                normalizedProcessName,
                nowUtc: null,
                out float resolvedVolume,
                out string resolvedMethod))
            {
                bool requiresApply = Math.Abs(candidate.CurrentVolumePercent - resolvedVolume) > 0.5f;
                return new SessionVolumeApplicationPlan(
                    requiresApply ? SessionVolumeApplicationAction.Apply : SessionVolumeApplicationAction.Skip,
                    sessionLabel,
                    resolvedMethod,
                    candidate.CurrentVolumePercent,
                    resolvedVolume,
                    ShouldTraceFuzzyMatch: resolvedMethod.StartsWith("Fuzzy", StringComparison.OrdinalIgnoreCase));
            }

            return new SessionVolumeApplicationPlan(
                SessionVolumeApplicationAction.None,
                sessionLabel,
                MatchMethod: null,
                candidate.CurrentVolumePercent,
                TargetVolumePercent: null,
                ShouldTraceFuzzyMatch: false);
        }

        public void UpdateVolume(AudioSessionInfo sessionInfo)
        {
            if (_disposed) return;

            if (sessionInfo.AudioSessionControl != null)
            {
                UpdateCache(sessionInfo.DisplayName, sessionInfo.ProcessName, sessionInfo.Volume);
            }

            MMDevice? device = null;
            try
            {
                if (sessionInfo.DisplayName == "Master Volume")
                {
                    device = GetDefaultPlaybackDevice("update-volume:master");
                    if (device != null &&
                        AudioDeviceHelper.TryGetEndpointVolume(_logger, device, out var volume, "update-volume:master"))
                    {
                        volume.MasterVolumeLevelScalar = Math.Clamp(sessionInfo.Volume / 100f, 0f, 1f);
                    }
                }
                else if (sessionInfo.DisplayName == "Microphone Volume")
                {
                    device = GetDefaultRecordingDevice("update-volume:recording");
                    if (device != null &&
                        AudioDeviceHelper.TryGetEndpointVolume(_logger, device, out var volume, "update-volume:recording"))
                    {
                        volume.MasterVolumeLevelScalar = Math.Clamp(sessionInfo.Volume / 100f, 0f, 1f);
                    }
                }
                else if (sessionInfo.AudioSessionControl != null)
                {
                    bool success = AudioDeviceHelper.TrySetSessionVolume(
                        _logger,
                        sessionInfo.AudioSessionControl,
                        sessionInfo.Volume / 100f);

                    if (!success)
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.Warning("VolumeControlService",
                                () => $"Failed to set volume for {sessionInfo.DisplayName} - session may be dead");
                    }
                }
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(UpdateVolume), ex);
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(UpdateVolume), ex);
            }
            finally
            {
                device?.Dispose();
            }
        }

        public void UpdateSessionVolume(string displayName, string? processName, float volume)
        {
            if (_disposed) return;
            UpdateCache(displayName, processName, volume);
        }

        private void UpdateCache(string displayName, string? processName, float volume)
        {
            _cacheStore.UpdateCache(displayName, processName, volume);
        }

        private float? TryGetCachedVolume(string normalizedKey)
        {
            return _cacheStore.TryGetCachedVolume(normalizedKey);
        }

        private string NormalizeForMatching(string name)
        {
            return _snapshotMatcher.NormalizeForMatching(name);
        }

        private static void IndexNormalizedWords(string normalizedValue, Dictionary<string, HashSet<string>> wordIndex)
        {
            VolumeSnapshotMatcher.IndexNormalizedWords(normalizedValue, wordIndex);
        }

        private float? FindFuzzyMatch(ReadOnlySpan<char> normalizedName, SessionVolumeSnapshot snapshot)
        {
            return _snapshotMatcher.FindFuzzyMatch(normalizedName, snapshot);
        }

        /// <summary>
        /// Resolves the best target volume for a session from a captured snapshot.
        /// </summary>
        /// <remarks>
        /// Match precedence is: exact/fuzzy display name, exact/fuzzy process name, then PID fallback when identity
        /// is unavailable or the snapshot is still fresh.
        /// </remarks>
        internal bool TryResolveSnapshotTarget(
            SessionVolumeSnapshot snapshot,
            uint pid,
            string? normalizedDisplayName,
            string? normalizedProcessName,
            DateTime? nowUtc,
            out float targetVolume,
            out string matchMethod)
        {
            return _snapshotMatcher.TryResolveSnapshotTarget(
                snapshot,
                pid,
                normalizedDisplayName,
                normalizedProcessName,
                nowUtc,
                out targetVolume,
                out matchMethod);
        }

        public async Task ApplySessionVolumesSimpleAsync(
            SessionVolumeSnapshot snapshot,
            bool applyMasterVolume = true,
            bool applyMicVolume = true)
        {
            CleanupExpiredRetryStates();
            if ((!applyMasterVolume || !snapshot.MasterVolumePercent.HasValue) && (!applyMicVolume || !snapshot.MicVolumePercent.HasValue) &&
                !snapshot.SystemSoundsVolumePercent.HasValue &&
                snapshot.ByPid.Count == 0 && snapshot.ByName.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.ApplySessionVolumesSimpleSkip} | reason=empty-snapshot");
                return;
            }

            MMDevice? circuitCheckDevice = null;
            try
            {
                circuitCheckDevice = GetDefaultPlaybackDevice("apply-session-volumes-simple:circuit-check");
                if (circuitCheckDevice != null)
                {
                    if (_retryStateTracker.IsCircuitOpen(circuitCheckDevice.ID))
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.Warning("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.ApplySessionVolumesSimpleSkip} | reason=circuit-open deviceId={LogPrivacy.Id(circuitCheckDevice.ID)}");
                        return;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                circuitCheckDevice?.Dispose();
            }

            MMDevice? playbackDevice = null;
            MMDevice? recordingDevice = null;

            try
            {
                playbackDevice = GetDefaultPlaybackDevice("apply-session-volumes-simple:playback");
                if (playbackDevice == null)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.Warning("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.ApplySessionVolumesSimpleSkip} | reason=playback-device-unavailable");
                    return;
                }
            }
            catch
            {
                return;
            }

            bool operationSuccess = false;

            try
            {
                bool masterApplied = false;
                bool micApplied = false;
                bool systemSoundsApplied = false;
                int sessionsApplied = 0;
                int sessionsSkipped = 0;
                int sessionApplyFailures = 0;
                List<string> sessionFailureDetails = [];
                int suppressedSessionFailureDetails = 0;
                int detailedSessionTraceEvents = 0;
                int detailedSessionTraceSuppressed = 0;
                int detailedSessionTraceInterval = Math.Max(1, AppConstants.Timing.VolumeSessionTraceLogEveryN);

                void RecordSessionApplyFailure(
                    string? displayName,
                    uint pid,
                    string? matchMethod,
                    float? currentVolume,
                    float? targetVolume,
                    string reason)
                {
                    if (sessionFailureDetails.Count < MaxSessionApplyFailureDetails)
                    {
                        sessionFailureDetails.Add(BuildSessionApplyFailureDetail(
                            displayName,
                            pid,
                            matchMethod,
                            currentVolume,
                            targetVolume,
                            reason));
                    }
                    else
                    {
                        suppressedSessionFailureDetails++;
                    }
                }

                bool ShouldLogDetailedSessionTrace()
                {
                    detailedSessionTraceEvents++;
                    bool shouldLog = detailedSessionTraceEvents == 1 ||
                        (detailedSessionTraceEvents % detailedSessionTraceInterval) == 0;
                    if (!shouldLog)
                    {
                        detailedSessionTraceSuppressed++;
                    }

                    return shouldLog;
                }

                if (applyMasterVolume && snapshot.MasterVolumePercent.HasValue)
                {
                    if (AudioDeviceHelper.TryGetEndpointVolume(_logger, playbackDevice, out var playbackVolume, "apply-session-volumes-simple:playback-endpoint"))
                    {
                        try
                        {
                            float currentMaster = playbackVolume.MasterVolumeLevelScalar * 100f;
                            float targetMaster = snapshot.MasterVolumePercent.Value;

                            if (Math.Abs(currentMaster - targetMaster) > 0.5f)
                            {
                                playbackVolume.MasterVolumeLevelScalar = Math.Clamp(targetMaster / 100f, 0f, 1f);
                                masterApplied = true;
                                if (_logger.IsEnabled(LogLevel.Trace))
                                    _logger.Trace("VolumeControlService",
                                        () => $"Master volume updated: {currentMaster:F1}% -> {targetMaster:F1}%");
                            }
                            else
                            {
                                masterApplied = true;
                                sessionsSkipped++;
                                if (_logger.IsEnabled(LogLevel.Trace))
                                    _logger.Trace("VolumeControlService",
                                        () => $"Master volume already matches: {currentMaster:F1}% (skipped)");
                            }
                        }
                        catch (COMException ex)
                        {
                            AudioDeviceHelper.LogComException(_logger, nameof(ApplySessionVolumesSimpleAsync), ex);
                            RecordRetryFailure(playbackDevice.ID);
                            throw;
                        }
                    }
                }

                if (applyMicVolume && snapshot.MicVolumePercent.HasValue)
                {
                    try
                    {
                        recordingDevice = GetDefaultRecordingDevice("apply-session-volumes-simple:recording");
                        if (recordingDevice != null &&
                            AudioDeviceHelper.TryGetEndpointVolume(_logger, recordingDevice, out var recordingVolume, "apply-session-volumes-simple:recording-endpoint"))
                        {
                            float currentMic = recordingVolume.MasterVolumeLevelScalar * 100f;
                            float targetMic = snapshot.MicVolumePercent.Value;

                            if (Math.Abs(currentMic - targetMic) > 0.5f)
                            {
                                recordingVolume.MasterVolumeLevelScalar = Math.Clamp(targetMic / 100f, 0f, 1f);
                                micApplied = true;
                                if (_logger.IsEnabled(LogLevel.Trace))
                                    _logger.Trace("VolumeControlService",
                                        () => $"Mic volume updated: {currentMic:F1}% -> {targetMic:F1}%");
                            }
                            else
                            {
                                micApplied = true;
                                sessionsSkipped++;
                                if (_logger.IsEnabled(LogLevel.Trace))
                                    _logger.Trace("VolumeControlService",
                                        () => $"Mic volume already matches: {currentMic:F1}% (skipped)");
                            }
                        }
                    }
                    catch (COMException ex)
                    {
                        AudioDeviceHelper.LogComException(_logger, nameof(ApplySessionVolumesSimpleAsync), ex);
                        if (recordingDevice != null)
                        {
                            RecordRetryFailure(recordingDevice.ID);
                        }
                        throw;
                    }
                }

                using AudioSessionManager sessionManager = playbackDevice.AudioSessionManager;
                using SessionCollection sessions = sessionManager.Sessions;
                int sessionCount = sessions.Count;

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.Trace("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.ApplySessionVolumesSimpleScan} | sessionCount={sessionCount}");

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

                    if (session == null || !AudioDeviceHelper.IsSessionValid(_logger, session))
                        continue;

                    uint pid = 0;
                    float? targetVol = null;
                    string? matchMethod = null;
                    string? sessionLabel = null;

                    try
                    {
                        pid = session.GetProcessID;

                        if (pid == 0)
                        {
                            if (snapshot.SystemSoundsVolumePercent.HasValue && !systemSoundsApplied)
                            {
                                float currentSystemSounds = session.SimpleAudioVolume.Volume * 100f;
                                SessionVolumeApplicationPlan systemPlan = BuildSessionVolumeApplicationPlan(
                                    snapshot,
                                    new SessionVolumeApplicationCandidate(
                                        pid,
                                        DisplayName: "System Sounds",
                                        ProcessName: "System",
                                        currentSystemSounds,
                                        IsSystemSounds: true));

                                if (systemPlan.Action == SessionVolumeApplicationAction.Apply)
                                {
                                    if (AudioDeviceHelper.TrySetSessionVolume(
                                        _logger, session, systemPlan.TargetVolumePercent!.Value / 100f))
                                    {
                                        systemSoundsApplied = true;
                                        sessionsApplied++;
                                        if (_logger.IsEnabled(LogLevel.Trace))
                                            _logger.Trace("VolumeControlService",
                                                () => $"System Sounds volume updated: {currentSystemSounds:F1}% -> {systemPlan.TargetVolumePercent.Value:F1}%");
                                    }
                                    else
                                    {
                                        sessionApplyFailures++;
                                        RecordSessionApplyFailure(
                                            displayName: "System Sounds",
                                            pid,
                                            matchMethod: systemPlan.MatchMethod,
                                            currentSystemSounds,
                                            systemPlan.TargetVolumePercent,
                                            reason: "set-failed");
                                    }
                                }
                                else if (systemPlan.Action == SessionVolumeApplicationAction.Skip)
                                {
                                    systemSoundsApplied = true;
                                    sessionsSkipped++;
                                    if (_logger.IsEnabled(LogLevel.Trace))
                                        _logger.Trace("VolumeControlService",
                                            () => $"System Sounds volume already matches: {currentSystemSounds:F1}% (skipped)");
                                }
                            }
                            continue;
                        }

                        string? displayName = session.DisplayName;
                        string normalizedDisplayName = string.Empty;
                        string normalizedProcessName = string.Empty;
                        sessionLabel = displayName;

                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            var cachedEntry = _lookupProcessInfo(pid);
                            if (cachedEntry.HasValue)
                            {
                                displayName = cachedEntry.Value.DisplayName;
                                sessionLabel = displayName ?? cachedEntry.Value.ProcessName;
                                normalizedProcessName = NormalizeForMatching(cachedEntry.Value.ProcessName);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            normalizedDisplayName = NormalizeForMatching(displayName);
                        }

                        if (string.IsNullOrWhiteSpace(normalizedProcessName))
                        {
                            var cachedEntry = _lookupProcessInfo(pid);
                            if (cachedEntry.HasValue)
                            {
                                normalizedProcessName = NormalizeForMatching(cachedEntry.Value.ProcessName);
                                sessionLabel ??= cachedEntry.Value.ProcessName;
                            }
                        }

                        SessionVolumeApplicationPlan sessionPlan = BuildSessionVolumeApplicationPlan(
                            snapshot,
                            new SessionVolumeApplicationCandidate(
                                pid,
                                displayName,
                                string.IsNullOrWhiteSpace(normalizedProcessName) ? null : normalizedProcessName,
                                session.SimpleAudioVolume.Volume * 100f));

                        targetVol = sessionPlan.TargetVolumePercent;
                        matchMethod = sessionPlan.MatchMethod;

                        if (sessionPlan.ShouldTraceFuzzyMatch &&
                            _logger.IsEnabled(LogLevel.Trace) &&
                            ShouldLogDetailedSessionTrace())
                        {
                            _logger.Trace("VolumeControlService",
                                () => $"{AppConstants.Audio.LogEvents.Volume.FuzzyMatchSession} | session={LogPrivacy.Session(displayName)} method={sessionPlan.MatchMethod}");
                        }

                        if (sessionPlan.Action == SessionVolumeApplicationAction.Apply)
                        {
                            float currentSessionVol = sessionPlan.CurrentVolumePercent!.Value;
                            if (AudioDeviceHelper.TrySetSessionVolume(_logger, session, sessionPlan.TargetVolumePercent!.Value / 100f))
                            {
                                sessionsApplied++;
                                if (_logger.IsEnabled(LogLevel.Trace) && ShouldLogDetailedSessionTrace())
                                {
                                    _logger.Trace("VolumeControlService",
                                        () => $"{AppConstants.Audio.LogEvents.Volume.SessionVolumeApply} | method={matchMethod} session={LogPrivacy.Session(session.DisplayName ?? pid.ToString())} from={currentSessionVol:F1}% to={sessionPlan.TargetVolumePercent.Value:F1}%");
                                }
                            }
                            else
                            {
                                sessionApplyFailures++;
                                RecordSessionApplyFailure(sessionLabel, pid, matchMethod, currentSessionVol, sessionPlan.TargetVolumePercent, reason: "set-failed");
                            }
                        }
                        else if (sessionPlan.Action == SessionVolumeApplicationAction.Skip)
                        {
                            sessionsSkipped++;
                            if (_logger.IsEnabled(LogLevel.Trace) && ShouldLogDetailedSessionTrace())
                            {
                                _logger.Trace("VolumeControlService",
                                    () => $"{AppConstants.Audio.LogEvents.Volume.SessionVolumeSkip} | method={matchMethod} session={LogPrivacy.Session(session.DisplayName ?? pid.ToString())} volume={sessionPlan.CurrentVolumePercent:F1}");
                            }
                        }
                    }
                    catch (COMException ex)
                    {
                        sessionApplyFailures++;
                        RecordSessionApplyFailure(
                            sessionLabel,
                            pid,
                            matchMethod,
                            currentVolume: null,
                            targetVolume: targetVol,
                            reason: $"com:{ex.HResult:X8}");
                        _logger.Trace("VolumeControlService",
                            () => $"COM exception applying session volume: {ex.HResult:X8}");
                    }
                }

                operationSuccess = true;

                if (sessionApplyFailures > 0 && _logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning(
                        "VolumeControlService",
                        () => $"apply-session-volumes-simple-partial-failure | sessionApplyFailures={sessionApplyFailures} sessionsApplied={sessionsApplied} sessionsSkipped={sessionsSkipped} {BuildSessionApplyFailureSummary(sessionFailureDetails, suppressedSessionFailureDetails)}");
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug("VolumeControlService",
                        () => $"{AppConstants.Audio.LogEvents.Volume.ApplySessionVolumesSimpleComplete} | masterApplied={masterApplied} micApplied={micApplied} systemSoundsApplied={systemSoundsApplied} sessionsApplied={sessionsApplied} snapshotPidCount={snapshot.ByPid.Count} sessionsSkipped={sessionsSkipped} sessionApplyFailures={sessionApplyFailures} detailedTraceSuppressed={detailedSessionTraceSuppressed}");
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(ApplySessionVolumesSimpleAsync), ex);
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(ApplySessionVolumesSimpleAsync), ex);
            }
            finally
            {
                if (operationSuccess && playbackDevice != null)
                {
                    ResetRetryState(playbackDevice.ID);
                }
                playbackDevice?.Dispose();
                recordingDevice?.Dispose();
            }
        }

        internal void CleanupExpiredRetryStates()
        {
            _ = _retryStateTracker.CleanupExpiredStates();
            VolumeCacheCleanupResult cleanupResult = _cacheStore.CleanupExpiredEntries();

            if ((cleanupResult.ExpiredVolumeEntries > 0 || cleanupResult.ExpiredAliases > 0) && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("VolumeControlService", () => $"{AppConstants.Audio.LogEvents.Volume.CleanupCacheComplete} | expiredVolumeEntries={cleanupResult.ExpiredVolumeEntries} expiredAliases={cleanupResult.ExpiredAliases}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info("VolumeControlService", "dispose-start");

            _retryStateTracker.Clear();
            _cacheStore.Clear();

            lock (_pendingRestoreLock)
            {
                _pendingRestore = null;
            }

            GC.SuppressFinalize(this);
        }
    }
}
