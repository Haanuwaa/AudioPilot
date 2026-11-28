using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Routines;

internal readonly record struct RoutineAppSessionRestoreRecord(
    uint ProcessId,
    string SessionInstanceId,
    float? OriginalVolume,
    float? AppliedVolume,
    bool? OriginalMute,
    bool? AppliedMute);

internal readonly record struct RoutineAppAudioApplicationResult(
    bool Success,
    bool Applied = false,
    bool DeferredNoAudio = false,
    IRoutineAudioChange? Change = null,
    string? FailureDetail = null,
    bool Changed = false,
    IReadOnlyList<RoutineAppSessionRestoreRecord>? RestorableSnapshots = null,
    IReadOnlyCollection<uint>? AppliedProcessIds = null);

internal interface IRoutineAppAudioSession : IDisposable
{
    uint ProcessId { get; }
    string InstanceId { get; }
    bool IsExpired { get; }
    float Volume { get; set; }
    bool Mute { get; set; }
}

/// <summary>Applies per-application playback volume/mute to live WASAPI render sessions and restores prior values on deactivation unless modified externally.</summary>
internal static class RoutineAppSessionAudioService
{
    private const float VolumeTolerance = 0.005f;

    internal static RoutineAppAudioApplicationResult Apply(
        AudioDeviceService audio,
        IRoutineProcessSnapshotProvider processSnapshots,
        Logger logger,
        uint rootProcessId,
        string targetAppPath,
        decimal? volumePercent,
        RoutineMuteAction muteAction,
        bool captureRestore,
        string operationId)
    {
        HashSet<uint> targetPids = CollectCandidateProcessIds(processSnapshots, rootProcessId, targetAppPath);
        return ApplyToSessions(
            () => EnumerateMatchingRenderSessions(audio, targetPids, logger),
            logger,
            volumePercent,
            muteAction,
            captureRestore,
            operationId);
    }

    internal static RoutineAppAudioApplicationResult ApplyToProcessIds(
        AudioDeviceService audio,
        Logger logger,
        IReadOnlyCollection<uint> processIds,
        decimal? volumePercent,
        RoutineMuteAction muteAction,
        bool captureRestore,
        string operationId)
    {
        HashSet<uint> targetPids = processIds is HashSet<uint> set ? set : [.. processIds];
        return ApplyToSessions(
            () => EnumerateMatchingRenderSessions(audio, targetPids, logger),
            logger,
            volumePercent,
            muteAction,
            captureRestore,
            operationId);
    }

    internal static RoutineAppAudioApplicationResult ApplyToSessions(
        Func<IReadOnlyList<IRoutineAppAudioSession>> getMatchingSessions,
        Logger logger,
        decimal? volumePercent,
        RoutineMuteAction muteAction,
        bool captureRestore,
        string operationId)
    {
        if (!volumePercent.HasValue && muteAction == RoutineMuteAction.Unchanged)
        {
            return new RoutineAppAudioApplicationResult(true);
        }

        return ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            IReadOnlyList<IRoutineAppAudioSession> sessions = [];
            try
            {
                sessions = getMatchingSessions();
                List<IRoutineAppAudioSession> liveSessions = [.. sessions.Where(static session => !session.IsExpired)];
                if (liveSessions.Count == 0)
                {
                    logger.Debug("RoutineAppAudio", () => $"routine-app-audio-deferred-no-session | opId={operationId}");
                    return new RoutineAppAudioApplicationResult(true, Applied: false, DeferredNoAudio: true);
                }

                float? targetVolume = volumePercent.HasValue ? (float)Math.Clamp(volumePercent.Value, 0m, 100m) / 100f : null;
                bool? targetMute = muteAction switch
                {
                    RoutineMuteAction.Mute => true,
                    RoutineMuteAction.Unmute => false,
                    _ => null,
                };

                List<RoutineAppSessionRestoreRecord> snapshots = [];
                HashSet<uint> appliedProcessIds = [];
                bool anyApplied = false;
                bool anyChanged = false;

                foreach (IRoutineAppAudioSession session in liveSessions)
                {
                    float? originalVolume = null;
                    float? appliedVolume = null;
                    bool? originalMute = null;
                    bool? appliedMute = null;
                    try
                    {
                        if (targetVolume.HasValue)
                        {
                            originalVolume = session.Volume;
                            session.Volume = targetVolume.Value;
                            appliedVolume = session.Volume;
                            if (Math.Abs(appliedVolume.Value - originalVolume.Value) > VolumeTolerance)
                            {
                                anyChanged = true;
                            }
                        }

                        if (targetMute.HasValue)
                        {
                            originalMute = session.Mute;
                            session.Mute = targetMute.Value;
                            appliedMute = session.Mute;
                            if (appliedMute.Value != originalMute.Value)
                            {
                                anyChanged = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Debug("RoutineAppAudio", () => $"routine-app-audio-session-write-failed | pid={LogPrivacy.Id(session.ProcessId.ToString())} opId={operationId} error={ex.GetType().Name}");
                    }
                    finally
                    {
                        if (appliedVolume.HasValue || appliedMute.HasValue)
                        {
                            anyApplied = true;
                            appliedProcessIds.Add(session.ProcessId);
                            if (captureRestore)
                            {
                                snapshots.Add(new RoutineAppSessionRestoreRecord(
                                    session.ProcessId,
                                    session.InstanceId,
                                    originalVolume,
                                    appliedVolume,
                                    originalMute,
                                    appliedMute));
                            }
                        }
                    }
                }

                if (!anyApplied)
                {
                    return new RoutineAppAudioApplicationResult(false, FailureDetail: "Failed to apply per-app playback volume or mute.");
                }

                logger.Debug("RoutineAppAudio", () => $"routine-app-audio-applied | sessions={liveSessions.Count} changed={anyChanged} opId={operationId}");
                IRoutineAudioChange? change = captureRestore && snapshots.Count > 0
                    ? new AppSessionAudioChange(getMatchingSessions, snapshots, logger, operationId)
                    : null;
                return new RoutineAppAudioApplicationResult(
                    true,
                    Applied: true,
                    DeferredNoAudio: false,
                    Change: change,
                    Changed: anyChanged,
                    RestorableSnapshots: captureRestore && snapshots.Count > 0 ? snapshots : null,
                    AppliedProcessIds: appliedProcessIds);
            }
            catch (Exception ex)
            {
                logger.Warning("RoutineAppAudio", $"routine-app-audio-failed | opId={operationId}", nameof(ApplyToSessions), ex);
                return new RoutineAppAudioApplicationResult(false, FailureDetail: "Failed to apply per-app playback volume or mute.");
            }
            finally
            {
                foreach (IRoutineAppAudioSession session in sessions)
                {
                    try { session.Dispose(); }
                    catch { }
                }
            }
        });
    }

    internal static void RestoreSnapshots(
        AudioDeviceService audio,
        Logger logger,
        IReadOnlyList<RoutineAppSessionRestoreRecord> snapshots,
        string operationId)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        HashSet<uint> targetPids = [.. snapshots.Select(static item => item.ProcessId)];
        RestoreToSessions(
            () => EnumerateMatchingRenderSessions(audio, targetPids, logger),
            snapshots,
            logger,
            operationId);
    }

    internal static void RestoreToSessions(
        Func<IReadOnlyList<IRoutineAppAudioSession>> getMatchingSessions,
        IReadOnlyList<RoutineAppSessionRestoreRecord> snapshots,
        Logger logger,
        string operationId)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            IReadOnlyList<IRoutineAppAudioSession> sessions = [];
            try
            {
                sessions = getMatchingSessions();
                foreach (RoutineAppSessionRestoreRecord record in snapshots)
                {
                    IRoutineAppAudioSession? match = !string.IsNullOrEmpty(record.SessionInstanceId)
                        ? sessions.FirstOrDefault(session =>
                            !session.IsExpired &&
                            string.Equals(session.InstanceId, record.SessionInstanceId, StringComparison.OrdinalIgnoreCase))
                        : sessions.FirstOrDefault(session => !session.IsExpired && session.ProcessId == record.ProcessId);

                    if (match == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (record.OriginalVolume.HasValue && record.AppliedVolume.HasValue)
                        {
                            float current = match.Volume;
                            if (Math.Abs(current - record.AppliedVolume.Value) <= VolumeTolerance)
                            {
                                match.Volume = record.OriginalVolume.Value;
                            }
                            else
                            {
                                logger.Debug("RoutineAppAudio", () => $"routine-app-audio-volume-restore-skipped | pid={LogPrivacy.Id(record.ProcessId.ToString())} opId={operationId} reason=newer-change");
                            }
                        }

                        if (record.OriginalMute.HasValue && record.AppliedMute.HasValue)
                        {
                            bool currentMute = match.Mute;
                            if (currentMute == record.AppliedMute.Value)
                            {
                                match.Mute = record.OriginalMute.Value;
                            }
                            else
                            {
                                logger.Debug("RoutineAppAudio", () => $"routine-app-audio-mute-restore-skipped | pid={LogPrivacy.Id(record.ProcessId.ToString())} opId={operationId} reason=newer-change");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Debug("RoutineAppAudio", () => $"routine-app-audio-restore-session-failed | pid={LogPrivacy.Id(record.ProcessId.ToString())} opId={operationId} error={ex.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning("RoutineAppAudio", $"routine-app-audio-restore-failed | opId={operationId}", nameof(RestoreToSessions), ex);
            }
            finally
            {
                foreach (IRoutineAppAudioSession session in sessions)
                {
                    try { session.Dispose(); }
                    catch { }
                }
            }
        });
    }

    private static HashSet<uint> CollectCandidateProcessIds(
        IRoutineProcessSnapshotProvider processSnapshots,
        uint rootProcessId,
        string targetAppPath)
    {
        HashSet<uint> result = [rootProcessId];
        if (rootProcessId == 0 || rootProcessId > int.MaxValue || string.IsNullOrWhiteSpace(targetAppPath))
        {
            return result;
        }

        try
        {
            List<RoutineProcessSnapshot> all = processSnapshots.CaptureAll(RoutineProcessSnapshotCaptureOptions.Full);
            IReadOnlyList<uint> candidates = RoutineApplicationRouting.CollectRoutineAppOutputCandidateProcessIds(
                (int)rootProcessId,
                targetAppPath,
                all,
                []);
            foreach (uint candidate in candidates)
            {
                result.Add(candidate);
            }
        }
        catch
        {
        }

        return result;
    }

    private static List<IRoutineAppAudioSession> EnumerateMatchingRenderSessions(
        AudioDeviceService audio,
        HashSet<uint> targetProcessIds,
        Logger logger)
    {
        List<IRoutineAppAudioSession> matched = [];
        if (targetProcessIds.Count == 0)
        {
            return matched;
        }

        try
        {
            using MMDeviceCollection devices = audio.GetActivePlaybackDevices();
            if (devices == null)
            {
                return matched;
            }

            for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
            {
                MMDevice? device = null;
                try
                {
                    device = devices[deviceIndex];
                    if (device == null)
                    {
                        continue;
                    }

                    using AudioSessionManager manager = device.AudioSessionManager;
                    using SessionCollection sessions = manager.Sessions;
                    for (int sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
                    {
                        AudioSessionControl? control = null;
                        try
                        {
                            control = sessions[sessionIndex];
                            if (control == null || control.IsSystemSoundsSession)
                            {
                                control?.Dispose();
                                continue;
                            }

                            uint pid = control.GetProcessID;
                            if (!targetProcessIds.Contains(pid) || control.State == AudioSessionState.AudioSessionStateExpired)
                            {
                                control.Dispose();
                                continue;
                            }

                            matched.Add(new NativeAppAudioSession(control, pid));
                            control = null;
                        }
                        catch
                        {
                            control?.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Trace("RoutineAppAudio", () => $"routine-app-audio-endpoint-skipped | deviceIndex={deviceIndex} error={ex.GetType().Name}");
                }
                finally
                {
                    device?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning("RoutineAppAudio", $"routine-app-audio-enumerate-failed | error={ex.GetType().Name}", nameof(EnumerateMatchingRenderSessions), ex);
        }

        return matched;
    }

    private sealed class NativeAppAudioSession : IRoutineAppAudioSession
    {
        private readonly AudioSessionControl _session;

        internal NativeAppAudioSession(AudioSessionControl session, uint processId)
        {
            _session = session;
            ProcessId = processId;
            string instanceId = string.Empty;
            try { instanceId = session.GetSessionInstanceIdentifier ?? string.Empty; }
            catch { }
            InstanceId = instanceId;
        }

        public uint ProcessId { get; }
        public string InstanceId { get; }
        public bool IsExpired
        {
            get
            {
                try { return _session.State == AudioSessionState.AudioSessionStateExpired; }
                catch { return true; }
            }
        }

        public float Volume
        {
            get => _session.SimpleAudioVolume.Volume;
            set => _session.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
        }

        public bool Mute
        {
            get => _session.SimpleAudioVolume.Mute;
            set => _session.SimpleAudioVolume.Mute = value;
        }

        public void Dispose() => _session.Dispose();
    }

    private sealed class AppSessionAudioChange(
        Func<IReadOnlyList<IRoutineAppAudioSession>> getMatchingSessions,
        List<RoutineAppSessionRestoreRecord> snapshots,
        Logger logger,
        string operationId) : IRoutineAudioChange
    {
        public void Restore() => RestoreToSessions(getMatchingSessions, snapshots, logger, operationId);

        public void Dispose()
        {
        }
    }
}
