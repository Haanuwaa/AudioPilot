using System.Windows.Input;
using AudioPilot.Cli;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal async Task<CliExecutionResult> ExecuteApplicationRoutingFromCliAsync(CliCommand command, CancellationToken cancellationToken,
            ApplicationRoutingOperations? routing = null)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            if (_isCleaningUp || IsRuntimeShutdownRequested) throw new OperationCanceledException(linked.Token);
            return await CliAppRoutingOperation.ExecuteAsync(command,
                routing ?? new(_audio.ReadApplicationRoutingAsync, _audio.ChangeApplicationRoutingAsync), linked.Token, RunManualApplicationRoutingAsync);
        }

        private bool CanExecuteTaskbarMediaCommand(object? command) =>
            !IsRuntimeShutdownRequested && !IsUpdateAdmissionBlocked && !_isCleaningUp && !ShutdownToken.IsCancellationRequested &&
            (ReferenceEquals(command, MediaCommands.PreviousTrack) ||
             ReferenceEquals(command, MediaCommands.TogglePlayPause) ||
             ReferenceEquals(command, MediaCommands.NextTrack));

        private void ExecuteTaskbarMediaCommand(object? command)
        {
            if (!CanExecuteTaskbarMediaCommand(command)) return;
            try
            {
                if (ReferenceEquals(command, MediaCommands.PreviousTrack)) _audioCommandCoordinator.MediaPreviousTrack("taskbar");
                else if (ReferenceEquals(command, MediaCommands.TogglePlayPause)) _audioCommandCoordinator.MediaPlayPause("taskbar");
                else if (ReferenceEquals(command, MediaCommands.NextTrack)) _audioCommandCoordinator.MediaNextTrack("taskbar");
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "Taskbar media command failed", nameof(ExecuteTaskbarMediaCommand), ex);
            }
        }

        public async Task RefreshFromCliAsync()
        {
            await RefreshDevicesAsync(promptOnPotentialOverwrite: false);
        }

        public void MediaPlayPauseFromCli()
        {
            _audioCommandCoordinator.MediaPlayPause();
        }

        public void MediaNextTrackFromCli()
        {
            _audioCommandCoordinator.MediaNextTrack();
        }

        public void MediaPreviousTrackFromCli()
        {
            _audioCommandCoordinator.MediaPreviousTrack();
        }

        public void MediaPlayPauseFromHotkey()
        {
            _audioCommandCoordinator.MediaPlayPause("hotkey");
        }

        public void MediaNextTrackFromHotkey()
        {
            _audioCommandCoordinator.MediaNextTrack("hotkey");
        }

        public void MediaPreviousTrackFromHotkey()
        {
            _audioCommandCoordinator.MediaPreviousTrack("hotkey");
        }

        public Task<MediaSeekResult> MediaSeekFromCliAsync(bool backward, int? seconds, CancellationToken cancellationToken = default) =>
            _audioCommandCoordinator.MediaSeekAsync(backward, seconds, cancellationToken: cancellationToken);

        public Task<MediaPlaybackResult> MediaSetPlayingFromCliAsync(bool playing, CancellationToken cancellationToken = default) => _audioCommandCoordinator.MediaSetPlayingAsync(playing, cancellationToken);

        public void MediaSeekForwardFromHotkey() => _ = _audioCommandCoordinator.MediaSeekAsync(false, source: "hotkey");

        public void MediaSeekBackwardFromHotkey() => _ = _audioCommandCoordinator.MediaSeekAsync(true, source: "hotkey");

        public void ShowAudioStatusFromHotkey() => _audioCommandCoordinator.ShowAudioStatus();

        public void ShowCurrentTrackFromCli()
        {
            _audioCommandCoordinator.ShowCurrentTrack();
        }

        public async Task<string> GetMediaStatusFromCliAsync(bool jsonOutput, bool redactOutput = false)
        {
            MediaOverlaySessionSnapshot snapshot = await _audioCommandCoordinator.GetCurrentMediaSnapshotAsync(throwOnCaptureFailure: true);
            return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput, redactOutput);
        }

        public Task<bool> ToggleMuteMicFromCliAsync(CancellationToken cancellationToken = default) => ApplyCliMuteAsync("mute-mic-toggle", "mic", !MuteMic, value => MuteMic = value, cancellationToken);
        public Task<bool> SetMuteMicFromCliAsync(bool enabled, CancellationToken cancellationToken = default) => ApplyCliMuteAsync(enabled ? "mute-mic-on" : "mute-mic-off", "mic", enabled, value => MuteMic = value, cancellationToken);
        public Task<bool> ToggleMuteSoundFromCliAsync(CancellationToken cancellationToken = default) => ApplyCliMuteAsync("mute-sound-toggle", "sound", !MuteSound, value => MuteSound = value, cancellationToken);
        public Task<bool> SetMuteSoundFromCliAsync(bool enabled, CancellationToken cancellationToken = default) => ApplyCliMuteAsync(enabled ? "mute-sound-on" : "mute-sound-off", "sound", enabled, value => MuteSound = value, cancellationToken);
        public Task<bool> ToggleDeafenFromCliAsync(CancellationToken cancellationToken = default) => ApplyCliMuteAsync("deafen-toggle", "deafen", !Deafen, value => Deafen = value, cancellationToken);
        public Task<bool> SetDeafenFromCliAsync(bool enabled, CancellationToken cancellationToken = default) => ApplyCliMuteAsync(enabled ? "deafen-on" : "deafen-off", "deafen", enabled, value => Deafen = value, cancellationToken);
        public bool ToggleListenToInputFromCli()
        {
            return _audioCommandCoordinator.ToggleListenToInput();
        }

        public bool SetListenToInputFromCli(bool enabled)
        {
            return _audioCommandCoordinator.SetListenToInput(enabled);
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

        internal void StepEndpointVolumeFromHotkey(bool playback, bool increase)
        {
            long generation = _hotkeys.CaptureGeneration;
            if (CanApplyAudioHotkey(generation))
                _audioCommandCoordinator.StepEndpointVolume(playback, increase, () => CanApplyAudioHotkey(generation));
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
                    (out value, out muted) => EndpointVolumeOperations.TryGetEndpointVolumeState(_logger, device, $"cli-volume:{kind}", out value, out muted),
                    (value, out applied, out muted) =>
                    {
                        bool success = EndpointVolumeOperations.TryApplyEndpointVolume(_logger, device, value, $"cli-volume:{kind}", true, true, out applied);
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
                    ? _audio.GetDefaultPlaybackDevice()
                    : _audio.GetDefaultRecordingDevice();
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
