using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Platform
{
    public static partial class AudioDeviceHelper
    {
        public readonly record struct SessionMutationResult(int Attempted, int Succeeded, int Failed)
        {
            public bool IsCompleteSuccess => Attempted > 0 && Succeeded == Attempted && Failed == 0;
            public static SessionMutationResult None => default;
            public SessionMutationResult Add(SessionMutationResult other) =>
                new(Attempted + other.Attempted, Succeeded + other.Succeeded, Failed + other.Failed);
        }

        public static string GetSessionDisplayNameFromCache(
            string processName,
            string? cachedDisplayName)
        {
            if (!string.IsNullOrWhiteSpace(cachedDisplayName))
                return cachedDisplayName;

            string displayName = SanitizeProcessName(processName);

            if (string.IsNullOrWhiteSpace(displayName))
                return "Unknown";

            return CapitalizeName(displayName);
        }

        internal static bool IsKnownHelperProcessName(string processName)
        {
            return !string.IsNullOrWhiteSpace(processName)
                && AppConstants.Audio.KnownHelperProcesses.Contains(processName);
        }

        internal static bool IsIgnoredProcessName(string processName)
        {
            return !string.IsNullOrWhiteSpace(processName)
                && AppConstants.Audio.IgnoredProcesses.Contains(processName);
        }

        public static bool ShouldIgnoreSession(ILogger logger, string displayName, Process? process)
        {
            if (displayName.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase))
            {
                logger.Trace("AudioDeviceService", () => $"  Skipping system sounds: {LogPrivacy.Session(displayName)}");
                return true;
            }

            if (process != null && IsIgnoredProcessName(process.ProcessName))
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"  Skipping ignored process: process={LogPrivacy.Process(process.ProcessName)} display={LogPrivacy.Session(displayName)}");
                return true;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                logger.Trace("AudioDeviceService", "  Skipping session with empty display name");
                return true;
            }

            return false;
        }

        public static bool ShouldIgnoreSessionFromCache(string displayName, string processName)
        {
            return displayName.Contains("@%SystemRoot%", StringComparison.OrdinalIgnoreCase)
                || IsIgnoredProcessName(processName)
                || string.IsNullOrWhiteSpace(displayName);
        }

        public static bool IsSessionValid(ILogger logger, AudioSessionControl session)
        {
            if (session == null) return false;
            try
            {
                _ = session.GetProcessID;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace("AudioDeviceService", () => $"  Session validation failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TryGetSessionVolume(
            ILogger logger,
            AudioSessionControl session,
            out float volume)
        {
            volume = 0f;
            if (session == null) return false;

            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                volume = session.SimpleAudioVolume.Volume;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TryGetSessionVolume failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TryGetSessionVolumeAndMute(
            ILogger logger,
            AudioSessionControl session,
            out float volume,
            out bool isMuted)
        {
            volume = 0f;
            isMuted = false;
            if (session == null) return false;

            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                var simpleAudioVolume = session.SimpleAudioVolume;
                volume = simpleAudioVolume.Volume;
                isMuted = simpleAudioVolume.Mute;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TryGetSessionVolumeAndMute failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TrySetSessionVolume(
            ILogger logger,
            AudioSessionControl session,
            float volume)
        {
            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                var scalar = Math.Clamp(volume, 0f, 1f);
                session.SimpleAudioVolume.Volume = scalar;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TrySetSessionVolume failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool TrySetSessionMute(
            ILogger logger,
            AudioSessionControl session,
            bool isMuted)
        {
            if (!IsSessionValid(logger, session))
                return false;

            try
            {
                session.SimpleAudioVolume.Mute = isMuted;
                return true;
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"TrySetSessionMute failed: {ex.GetType().Name}");
                return false;
            }
        }

        /// <summary>
        /// Applies a volume value to all valid sessions that belong to a process id.
        /// </summary>
        public static int SetVolumeForSessionsByPid(
            AudioDeviceService audioService,
            uint pid,
            float scalar,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger)
        {
            return SetVolumeForSessionsByPid(audioService, pid, scalar, pidToProcessName, logger, DataFlow.Render).Succeeded;
        }

        public static SessionMutationResult SetVolumeForSessionsByPid(
            AudioDeviceService audioService,
            uint pid,
            float scalar,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger,
            DataFlow dataFlow)
        {
            SessionMutationResult result = SessionMutationResult.None;

            try
            {
                using MMDeviceCollection activeDevices = dataFlow == DataFlow.Capture
                    ? audioService.GetActiveCaptureDevices()
                    : audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return SessionMutationResult.None;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device == null)
                            continue;

                        result = result.Add(SetVolumeForSessionsOnDevice(device, pid, scalar, pidToProcessName, logger));
                    }
                    catch (Exception ex)
                    {
                        result = result.Add(new SessionMutationResult(1, 0, 1));
                        logger.Warning("AudioDeviceService", () => $"mixer-process-volume-endpoint-failed | pid={LogPrivacy.Id(pid.ToString())} flow={dataFlow} deviceIndex={di} hresult=0x{ex.HResult:X8}", nameof(SetVolumeForSessionsByPid), ex);
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"SetVolumeForSessionsByPid failed for pid={LogPrivacy.Id(pid.ToString())} flow={dataFlow} reason={ex.GetType().Name}");
                }
                return result.Add(new SessionMutationResult(1, 0, 1));
            }

            return result;
        }

        private static SessionMutationResult SetVolumeForSessionsOnDevice(
            MMDevice device,
            uint pid,
            float scalar,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger)
        {
            SessionMutationResult result = default;
            using AudioSessionManager sessionManager = device.AudioSessionManager;
            using SessionCollection sessions = sessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? session = null;
                try
                {
                    session = sessions[i];
                    if (session == null || session.GetProcessID != pid) continue;
                    result = result.Add(ApplyProcessSessionVolume(device, session, pid, scalar, pidToProcessName, logger));
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("AudioDeviceService", () => $"SetVolumeForSessionsByPid skipped session index {i}: {ex.GetType().Name}");
                }
                finally { session?.Dispose(); }
            }

            return result;
        }

        internal readonly record struct SessionVolumeTarget(float Scalar, Func<bool> CanApply);

        internal static Dictionary<uint, SessionMutationResult> SetPlaybackSessionVolumesByPid(
            AudioDeviceService audioService, Dictionary<uint, SessionVolumeTarget> targets,
            ConcurrentDictionary<uint, string> pidToProcessName, Logger logger) =>
            SetSessionVolumesByPid(audioService.GetActivePlaybackDevices, targets, pidToProcessName, logger);

        internal static Dictionary<uint, SessionMutationResult> SetSessionVolumesByPid(
            Func<MMDeviceCollection> getActiveDevices, Dictionary<uint, SessionVolumeTarget> targets,
            ConcurrentDictionary<uint, string> pidToProcessName, Logger logger,
            Func<AudioSessionControl, float, bool>? writeVolume = null) =>
            ApplySessionChangesByPid(getActiveDevices, targets, logger, (device, session, pid, target) =>
                ApplyProcessSessionVolume(device, session, pid, target.Scalar, pidToProcessName, logger, target.CanApply, writeVolume));

        internal static Dictionary<uint, SessionMutationResult> SetPlaybackSessionMutesByPid(
            AudioDeviceService audioService, Dictionary<uint, Func<bool>> targets, bool muted,
            ConcurrentDictionary<uint, string> pidToProcessName, Logger logger) =>
            ApplySessionChangesByPid(audioService.GetActivePlaybackDevices, targets, logger, (device, session, pid, canApply) =>
                ApplyProcessSessionMute(device, session, pid, muted, pidToProcessName, logger, canApply));

        private static Dictionary<uint, SessionMutationResult> ApplySessionChangesByPid<T>(
            Func<MMDeviceCollection> getActiveDevices, Dictionary<uint, T> targets, Logger logger,
            Func<MMDevice, AudioSessionControl, uint, T, SessionMutationResult> applyChange)
        {
            var results = new Dictionary<uint, SessionMutationResult>(targets.Count);
            if (targets.Count == 0) return results;
            bool incomplete = false;
            try
            {
                using MMDeviceCollection devices = getActiveDevices();
                int deviceCount = devices.Count;
                for (int index = 0; index < deviceCount; index++)
                {
                    try
                    {
                        using MMDevice device = devices[index];
                        using AudioSessionManager manager = device.AudioSessionManager;
                        using SessionCollection sessions = manager.Sessions;
                        int sessionCount = sessions.Count;
                        for (int i = 0; i < sessionCount; i++)
                        {
                            try
                            {
                                using AudioSessionControl session = sessions[i];
                                uint pid = session.GetProcessID;
                                if (!targets.TryGetValue(pid, out T? target)) continue;
                                SessionMutationResult outcome = applyChange(device, session, pid, target);
                                results[pid] = results.GetValueOrDefault(pid).Add(outcome);
                            }
                            catch (Exception ex)
                            {
                                incomplete = true;
                                logger.Trace("AudioDeviceService", () => $"mixer-group-session-unavailable | hresult=0x{ex.HResult:X8}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        incomplete = true;
                        logger.Warning("AudioDeviceService", () => $"mixer-group-endpoint-failed | deviceIndex={index} hresult=0x{ex.HResult:X8}");
                    }
                }
            }
            catch (Exception ex)
            {
                incomplete = true;
                logger.Warning("AudioDeviceService", () => $"mixer-group-inventory-failed | hresult=0x{ex.HResult:X8}");
            }

            if (incomplete)
                foreach (uint pid in targets.Keys) results[pid] = results.GetValueOrDefault(pid).Add(new(1, 0, 1));
            return results;
        }

        private static SessionMutationResult ApplyProcessSessionVolume(MMDevice device, AudioSessionControl session,
            uint pid, float scalar, ConcurrentDictionary<uint, string> pidToProcessName, Logger logger,
            Func<bool>? canApply = null, Func<AudioSessionControl, float, bool>? writeVolume = null)
        {
            SessionMutationResult result = new(1, 0, 1);
            try
            {
                AudioSessionState state = session.State;
                if (state == AudioSessionState.AudioSessionStateExpired || canApply?.Invoke() == false) return default;
                if (writeVolume?.Invoke(session, scalar) ?? TrySetSessionVolume(logger, session, scalar)) result = new(1, 1, 0);
                string? displayName = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(displayName)) pidToProcessName.TryAdd(pid, displayName);
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.Trace("AudioDeviceService", $"mixer-process-volume-write | pid={LogPrivacy.Id(pid.ToString())} endpoint={LogPrivacy.Id(device.ID)} instance={LogPrivacy.Id(session.GetSessionInstanceIdentifier)} state={state} requested={scalar} observed={session.SimpleAudioVolume.Volume}");
            }
            catch (Exception ex)
            {
                logger.Trace("AudioDeviceService", () => $"mixer-process-volume-session-failed | hresult=0x{ex.HResult:X8}");
            }
            return result;
        }

        /// <summary>
        /// Applies volume to live sessions identified by Windows as system sounds.
        /// </summary>
        public static int SetVolumeForSystemSounds(
            AudioDeviceService audioService,
            float scalar,
            Logger logger)
        {
            int updated = 0;

            try
            {
                using MMDeviceCollection activeDevices = audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return 0;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device == null)
                            continue;

                        using AudioSessionManager sessionManager = device.AudioSessionManager;
                        using SessionCollection sessions = sessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];
                                if (session == null)
                                    continue;

                                if (session.State == AudioSessionState.AudioSessionStateExpired || !session.IsSystemSoundsSession)
                                    continue;

                                float clamped = Math.Clamp(scalar, 0f, 1f);
                                try
                                {
                                    session.SimpleAudioVolume.Volume = clamped;

                                    updated++;
                                }
                                catch (Exception ex)
                                {
                                    logger.Trace("AudioDeviceService", () => $"SetVolumeForSystemSounds failed: {ex.GetType().Name}");
                                }
                            }
                            catch
                            {
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch
            {
                return updated;
            }

            return updated;
        }

        public static SessionMutationResult SetMuteForSessionsByPid(
            AudioDeviceService audioService,
            uint pid,
            bool isMuted,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger,
            DataFlow dataFlow)
        {
            SessionMutationResult result = SessionMutationResult.None;

            try
            {
                using MMDeviceCollection activeDevices = dataFlow == DataFlow.Capture
                    ? audioService.GetActiveCaptureDevices()
                    : audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return SessionMutationResult.None;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device == null)
                            continue;

                        result = result.Add(SetMuteForSessionsOnDevice(device, pid, isMuted, pidToProcessName, logger));
                    }
                    catch (Exception ex)
                    {
                        result = result.Add(new SessionMutationResult(1, 0, 1));
                        logger.Warning("AudioDeviceService", () => $"mixer-process-mute-endpoint-failed | pid={LogPrivacy.Id(pid.ToString())} flow={dataFlow} deviceIndex={di} hresult=0x{ex.HResult:X8}", nameof(SetMuteForSessionsByPid), ex);
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"SetMuteForSessionsByPid failed for pid={LogPrivacy.Id(pid.ToString())} flow={dataFlow} reason={ex.GetType().Name}");
                }

                return result.Add(new SessionMutationResult(1, 0, 1));
            }

            return result;
        }

        private static SessionMutationResult SetMuteForSessionsOnDevice(
            MMDevice device,
            uint pid,
            bool isMuted,
            ConcurrentDictionary<uint, string> pidToProcessName,
            Logger logger)
        {
            SessionMutationResult result = default;
            using AudioSessionManager sessionManager = device.AudioSessionManager;
            using SessionCollection sessions = sessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? session = null;
                try
                {
                    session = sessions[i];
                    if (session == null || session.GetProcessID != pid) continue;
                    result = result.Add(ApplyProcessSessionMute(device, session, pid, isMuted, pidToProcessName, logger));
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("AudioDeviceService", () => $"SetMuteForSessionsByPid skipped session index {i}: {ex.GetType().Name}");
                }
                finally { session?.Dispose(); }
            }

            return result;
        }

        private static SessionMutationResult ApplyProcessSessionMute(MMDevice device, AudioSessionControl session,
            uint pid, bool muted, ConcurrentDictionary<uint, string> pidToProcessName, Logger logger, Func<bool>? canApply = null)
        {
            SessionMutationResult result = new(1, 0, 1);
            try
            {
                AudioSessionState state = session.State;
                if (state == AudioSessionState.AudioSessionStateExpired || canApply?.Invoke() == false) return default;
                if (TrySetSessionMute(logger, session, muted)) result = new(1, 1, 0);
                string? displayName = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(displayName)) pidToProcessName.TryAdd(pid, displayName);
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.Trace("AudioDeviceService", $"mixer-process-mute-write | pid={LogPrivacy.Id(pid.ToString())} endpoint={LogPrivacy.Id(device.ID)} instance={LogPrivacy.Id(session.GetSessionInstanceIdentifier)} state={state} requested={muted} observed={session.SimpleAudioVolume.Mute}");
            }
            catch (Exception ex)
            {
                logger.Trace("AudioDeviceService", () => $"mixer-process-mute-session-failed | hresult=0x{ex.HResult:X8}");
            }
            return result;
        }

        public static int SetMuteForSystemSounds(
            AudioDeviceService audioService,
            bool isMuted,
            Logger logger)
        {
            int updated = 0;

            try
            {
                using MMDeviceCollection activeDevices = audioService.GetActivePlaybackDevices();
                if (activeDevices == null || activeDevices.Count == 0)
                    return 0;

                for (int di = 0; di < activeDevices.Count; di++)
                {
                    var device = activeDevices[di];
                    try
                    {
                        if (device == null)
                            continue;

                        using AudioSessionManager sessionManager = device.AudioSessionManager;
                        using SessionCollection sessions = sessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            AudioSessionControl? session = null;
                            try
                            {
                                session = sessions[i];
                                if (session == null)
                                    continue;

                                if (session.State == AudioSessionState.AudioSessionStateExpired || !session.IsSystemSoundsSession)
                                    continue;

                                if (TrySetSessionMute(logger, session, isMuted))
                                {
                                    updated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.Trace("AudioDeviceService", () => $"SetMuteForSystemSounds skipped session index {i}: {ex.GetType().Name}");
                                }
                            }
                            finally
                            {
                                session?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        device?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("AudioDeviceService", () => $"SetMuteForSystemSounds failed: {ex.GetType().Name}");
                }

                return updated;
            }

            return updated;
        }

        public static bool TryGetEndpointVolume(
            ILogger logger,
            MMDevice device,
            [NotNullWhen(true)] out AudioEndpointVolume? volume)
        {
            return TryGetEndpointVolume(logger, device, out volume, reason: null);
        }

        public static bool TryGetEndpointVolume(
            ILogger logger,
            MMDevice device,
            [NotNullWhen(true)] out AudioEndpointVolume? volume,
            string? reason)
        {
            volume = null;
            if (device == null)
            {
                logger.Trace("AudioDeviceHelper", "TryGetEndpointVolume called with null device");
                return false;
            }

            string logReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;

            string ResolveDeviceLabel()
            {
                try
                {
                    return LogPrivacy.Device(device.FriendlyName);
                }
                catch (Exception ex)
                {
                    logger.Trace(
                        "AudioDeviceHelper",
                        $"Could not read endpoint friendly name: {ex.GetType().Name}");
                    return "audio endpoint";
                }
            }

            try
            {
                volume = device.AudioEndpointVolume;
                if (volume != null)
                {
                    return true;
                }
                logger.Warning(
                    "AudioDeviceHelper",
                    $"{ResolveDeviceLabel()} returned null AudioEndpointVolume | reason={logReason}");
                return false;
            }
            catch (InvalidCastException)
            {
                logger.Trace(
                    "AudioDeviceHelper",
                    $"{ResolveDeviceLabel()} does not support IAudioEndpointVolume interface | reason={logReason}");
                return false;
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80004002))
            {
                logger.Trace(
                    "AudioDeviceHelper",
                    $"{ResolveDeviceLabel()} returns E_NOINTERFACE for IAudioEndpointVolume | reason={logReason}");
                return false;
            }
            catch (COMException ex)
            {
                logger.Error(
                    "AudioDeviceHelper",
                    $"COM exception for {ResolveDeviceLabel()}: 0x{ex.HResult:X8} - {ex.GetType().Name} | reason={logReason}",
                    nameof(TryGetEndpointVolume),
                    ex);
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(
                    "AudioDeviceHelper",
                    $"Unexpected exception for {ResolveDeviceLabel()}: {ex.GetType().Name} | reason={logReason}",
                    nameof(TryGetEndpointVolume),
                    ex);
                return false;
            }
        }
    }
}
