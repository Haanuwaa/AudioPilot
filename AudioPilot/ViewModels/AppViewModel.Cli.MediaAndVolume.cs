using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        public async Task RefreshFromCliAsync()
        {
            await RefreshDevicesAsync(promptOnPotentialOverwrite: false);
        }

        public void MediaPlayPauseFromCli()
        {
            _cliOverlayCoordinator.MediaPlayPause();
        }

        public void MediaNextTrackFromCli()
        {
            _cliOverlayCoordinator.MediaNextTrack();
        }

        public void MediaPreviousTrackFromCli()
        {
            _cliOverlayCoordinator.MediaPreviousTrack();
        }

        public void MediaPlayPauseFromHotkey()
        {
            _cliOverlayCoordinator.MediaPlayPause("hotkey");
        }

        public void MediaNextTrackFromHotkey()
        {
            _cliOverlayCoordinator.MediaNextTrack("hotkey");
        }

        public void MediaPreviousTrackFromHotkey()
        {
            _cliOverlayCoordinator.MediaPreviousTrack("hotkey");
        }

        public Task<MediaSeekResult> MediaSeekFromCliAsync(bool backward, int? seconds, CancellationToken cancellationToken = default) =>
            _cliOverlayCoordinator.MediaSeekAsync(backward, seconds, cancellationToken: cancellationToken);

        public Task<MediaPlaybackResult> MediaSetPlayingFromCliAsync(bool playing, CancellationToken cancellationToken = default) => _cliOverlayCoordinator.MediaSetPlayingAsync(playing, cancellationToken);

        public void MediaSeekForwardFromHotkey() => _ = _cliOverlayCoordinator.MediaSeekAsync(false, source: "hotkey");

        public void MediaSeekBackwardFromHotkey() => _ = _cliOverlayCoordinator.MediaSeekAsync(true, source: "hotkey");

        public void ShowAudioStatusFromHotkey() => _cliOverlayCoordinator.ShowAudioStatus();

        public void ShowCurrentTrackFromCli()
        {
            _cliOverlayCoordinator.ShowCurrentTrack();
        }

        public async Task<string> GetMediaStatusFromCliAsync(bool jsonOutput, bool redactOutput = false)
        {
            MediaOverlaySessionSnapshot snapshot = await _cliOverlayCoordinator.GetCurrentMediaSnapshotAsync();
            return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput, redactOutput);
        }

        public bool ToggleMuteMicFromCli()
        {
            bool success = _cliOverlayCoordinator.ToggleMuteMic(() => MuteMic, value => MuteMic = value);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, "mute-mic-toggle", success, skipped: false, success ? $"Microphone mute is now {(MuteMic ? "enabled" : "disabled")}." : "Failed to toggle microphone mute.", success ? null : "Failed to toggle microphone mute.", target: "mic", enabled: success ? MuteMic : null);
            return success;
        }

        public bool SetMuteMicFromCli(bool enabled)
        {
            bool success = _cliOverlayCoordinator.SetMuteMic(enabled, value => MuteMic = value);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-mic-on" : "mute-mic-off", success, skipped: false, success ? $"Microphone mute {(enabled ? "enabled" : "disabled")}." : "Failed to set microphone mute.", success ? null : "Failed to set microphone mute.", target: "mic", enabled: success ? enabled : null);
            return success;
        }

        public bool ToggleMuteSoundFromCli()
        {
            bool success = _cliOverlayCoordinator.ToggleMuteSound(() => MuteSound, value => MuteSound = value);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, "mute-sound-toggle", success, skipped: false, success ? $"Playback mute is now {(MuteSound ? "enabled" : "disabled")}." : "Failed to toggle playback mute.", success ? null : "Failed to toggle playback mute.", target: "sound", enabled: success ? MuteSound : null);
            return success;
        }

        public bool SetMuteSoundFromCli(bool enabled)
        {
            bool success = _cliOverlayCoordinator.SetMuteSound(enabled, value => MuteSound = value);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-sound-on" : "mute-sound-off", success, skipped: false, success ? $"Playback mute {(enabled ? "enabled" : "disabled")}." : "Failed to set playback mute.", success ? null : "Failed to set playback mute.", target: "sound", enabled: success ? enabled : null);
            return success;
        }

        public bool ToggleDeafenFromCli()
        {
            bool success = _cliOverlayCoordinator.ToggleDeafen(() => Deafen, value => Deafen = value);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, "deafen-toggle", success, skipped: false, success ? $"Deafen is now {(Deafen ? "enabled" : "disabled")}." : "Failed to toggle deafen.", success ? null : "Failed to toggle deafen.", target: "deafen", enabled: success ? Deafen : null);
            return success;
        }

        public bool SetDeafenFromCli(bool enabled)
        {
            bool success = _cliOverlayCoordinator.SetDeafen(enabled, value => Deafen = value);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "deafen-on" : "deafen-off", success, skipped: false, success ? $"Deafen {(enabled ? "enabled" : "disabled")}." : "Failed to set deafen.", success ? null : "Failed to set deafen.", target: "deafen", enabled: success ? enabled : null);
            return success;
        }

        public bool ToggleListenToInputFromCli()
        {
            return _cliOverlayCoordinator.ToggleListenToInput();
        }

        public bool SetListenToInputFromCli(bool enabled)
        {
            return _cliOverlayCoordinator.SetListenToInput(enabled);
        }

        public string GetMuteStatusFromCli(string target, bool jsonOutput)
        {
            bool enabled = target switch
            {
                "mic" => MuteMic,
                "sound" => MuteSound,
                "deafen" => Deafen,
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown mute target."),
            };

            return CliOutputFormatter.FormatMuteStatus(target, enabled, jsonOutput);
        }

        public bool StepMasterVolumeUpFromCli()
        {
            return _cliOverlayCoordinator.StepMasterVolume(increase: true);
        }

        public bool StepMasterVolumeDownFromCli()
        {
            return _cliOverlayCoordinator.StepMasterVolume(increase: false);
        }

        public bool StepMicVolumeUpFromCli()
        {
            return _cliOverlayCoordinator.StepMicVolume(increase: true);
        }

        public bool StepMicVolumeDownFromCli()
        {
            return _cliOverlayCoordinator.StepMicVolume(increase: false);
        }

        public (bool Success, string Output) GetVolumeFromCli(bool playback, string? deviceId, bool jsonOutput, bool redactOutput = false) =>
            ExecuteVolumeFromCli(playback, deviceId, null, jsonOutput, redactOutput, false);

        public (bool Success, string Output) SetVolumeFromCli(bool playback, string? deviceId, float percent, bool jsonOutput, bool redactOutput = false, bool relative = false) =>
            ExecuteVolumeFromCli(playback, deviceId, percent, jsonOutput, redactOutput, relative);

        private (bool Success, string Output) ExecuteVolumeFromCli(bool playback, string? selector, float? percent, bool json, bool redact, bool relative)
        {
            string failure = percent == null ? "volume-get-failed" : relative ? "volume-adjust-failed" : "volume-set-failed";
            string action = percent == null ? "read" : relative ? "adjust" : "set";
            return ExecuteCliVolumeAction(playback, selector, json, redact, failure, action, $"cli-{failure}", nameof(ExecuteVolumeFromCli),
                (kind, description, id, device) => CliVolumeOperation.Execute(playback, relative ? device.ID : id, percent, relative, json, redact,
                    (out value, out muted) => AppCliOverlayCoordinator.TryGetEndpointVolumeState(_logger, device, $"cli-volume:{kind}", out value, out muted),
                    (value, out applied, out muted) =>
                    {
                        bool success = AppCliOverlayCoordinator.TryApplyEndpointVolume(_logger, device, value, $"cli-volume:{kind}", true, true, out applied);
                        muted = applied <= 0f;
                        return success;
                    },
                    () => $"Failed to {action} {description} volume.",
                    (applied, muted) =>
                    {
                        if ((!relative && string.IsNullOrWhiteSpace(id)) || IsCurrentDefaultCliVolumeEndpoint(playback, device))
                            ProjectEndpointVolumeStateFromCommand(playback ? AudioMixerMode.Output : AudioMixerMode.Input, device.ID, applied, muted);
                    }));
        }

        public string GetListenStatusFromCli(bool jsonOutput, bool redactOutput = false)
        {
            var (currentInputListenEnabled, listenMonitorTargetOutputDeviceName) = GetCurrentListenStatusSnapshot();
            return CliOutputFormatter.FormatListenStatus(currentInputListenEnabled, listenMonitorTargetOutputDeviceName, jsonOutput, redactOutput);
        }

        private (bool? CurrentInputListenEnabled, string? ListenMonitorTargetOutputDeviceName) GetCurrentListenStatusSnapshot()
        {
            bool? currentInputListenEnabled = null;

            if (_audio.TryGetCurrentInputListenState(out bool enabled, out _))
            {
                currentInputListenEnabled = enabled;
            }

            _audio.TryGetCurrentInputListenTargetOutputDeviceName(out string? listenMonitorTargetOutputDeviceName, out _);

            return (currentInputListenEnabled, listenMonitorTargetOutputDeviceName);
        }

        private (bool Success, string Output) ExecuteCliVolumeAction(
            bool playback,
            string? deviceId,
            bool jsonOutput,
            bool redactOutput,
            string failureDiagCode,
            string actionDescription,
            string logOperationName,
            string callerName,
            Func<string, string, string?, MMDevice, (bool Success, string Output)> execute)
        {
            string kind = playback ? "master" : "mic";
            string targetDescription = playback ? "playback" : "recording";
            string normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId.Trim();
            if (!CliVolumeOperation.TryResolve(playback, normalizedDeviceId, () => playback ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos(), out string? resolvedDeviceId, out string? selectorError))
            {
                return (false, CliOutputFormatter.FormatVolumeError(kind, failureDiagCode, selectorError ?? $"Failed to {actionDescription} {targetDescription} volume.", jsonOutput, normalizedDeviceId, redactOutput));
            }

            try
            {
                using var device = GetCliVolumeEndpoint(playback, resolvedDeviceId);
                if (device == null)
                {
                    return (false, CliOutputFormatter.FormatVolumeError(kind, failureDiagCode, BuildCliVolumeUnavailableMessage(targetDescription, resolvedDeviceId), jsonOutput, resolvedDeviceId, redactOutput));
                }

                return execute(kind, targetDescription, resolvedDeviceId, device);
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", $"{logOperationName}:{targetDescription}", callerName, ex);
                return (false, CliOutputFormatter.FormatVolumeError(kind, failureDiagCode, $"Failed to {actionDescription} {targetDescription} volume.", jsonOutput, resolvedDeviceId, redactOutput));
            }
        }

        private MMDevice? GetCliVolumeEndpoint(bool playback, string? resolvedDeviceId)
        {
            return playback
                ? string.IsNullOrWhiteSpace(resolvedDeviceId)
                    ? _audio.GetDefaultPlaybackDevice()
                    : _audio.TryGetPlaybackDeviceById(resolvedDeviceId)
                : string.IsNullOrWhiteSpace(resolvedDeviceId)
                    ? _audio.GetDefaultRecordingDevice()
                    : _audio.TryGetCaptureDeviceById(resolvedDeviceId);
        }

        private bool IsCurrentDefaultCliVolumeEndpoint(bool playback, MMDevice selectedDevice)
        {
            try
            {
                using MMDevice? defaultDevice = playback
                    ? _audio.GetDefaultPlaybackDevice("cli-volume-default-match")
                    : _audio.GetDefaultRecordingDevice("cli-volume-default-match");
                return defaultDevice != null
                    && string.Equals(defaultDevice.ID, selectedDevice.ID, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(AudioPilot.Logging.LogLevel.Trace))
                {
                    _logger.Trace("AppViewModel", () => $"cli-volume-default-match-failed | playback={playback} error={ex.GetType().Name}");
                }

                return false;
            }
        }

        private static string BuildCliVolumeUnavailableMessage(string targetDescription, string? resolvedDeviceId)
        {
            return string.IsNullOrWhiteSpace(resolvedDeviceId)
                ? $"No default {targetDescription} device is available."
                : $"{char.ToUpperInvariant(targetDescription[0])}{targetDescription[1..]} device '{resolvedDeviceId}' is not available.";
        }
    }
}
