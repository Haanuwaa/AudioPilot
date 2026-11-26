using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public Task<bool> ToggleMuteMicAsync()
        {
            bool? current = TryGetDefaultCaptureMute();
            if (!current.HasValue)
            {
                return Task.FromResult(false);
            }

            return SetMuteMicAsync(!current.Value);
        }

        public Task<bool> SetMuteMicAsync(bool enabled)
        {
            try
            {
                bool success = SetMuteMicCore(enabled);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-mic-on" : "mute-mic-off", success, skipped: false, success ? $"Microphone mute {(enabled ? "enabled" : "disabled")}." : "Failed to set microphone mute.", success ? null : "Failed to set microphone mute.", target: "mic", enabled: success ? enabled : null);
                return Task.FromResult(success);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetMuteMicAsync), "cli-mute-mic-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-mic-on" : "mute-mic-off", success: false, skipped: false, summary: "Failed to set microphone mute.", reason: "Failed to set microphone mute.", target: "mic");
                return Task.FromResult(false);
            }
        }

        public Task<bool> ToggleMuteSoundAsync()
        {
            bool? current = TryGetDefaultPlaybackMute();
            if (!current.HasValue)
            {
                return Task.FromResult(false);
            }

            return SetMuteSoundAsync(!current.Value);
        }

        public Task<bool> SetMuteSoundAsync(bool enabled)
        {
            try
            {
                bool success = SetMuteSoundCore(enabled);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-sound-on" : "mute-sound-off", success, skipped: false, success ? $"Playback mute {(enabled ? "enabled" : "disabled")}." : "Failed to set playback mute.", success ? null : "Failed to set playback mute.", target: "sound", enabled: success ? enabled : null);
                return Task.FromResult(success);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetMuteSoundAsync), "cli-mute-sound-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-sound-on" : "mute-sound-off", success: false, skipped: false, summary: "Failed to set playback mute.", reason: "Failed to set playback mute.", target: "sound");
                return Task.FromResult(false);
            }
        }

        public Task<bool> ToggleDeafenAsync()
        {
            bool? playbackMuted = TryGetDefaultPlaybackMute();
            bool? micMuted = TryGetDefaultCaptureMute();

            if (!playbackMuted.HasValue || !micMuted.HasValue)
            {
                return Task.FromResult(false);
            }

            bool next = !(playbackMuted.Value && micMuted.Value);
            return SetDeafenAsync(next);
        }

        public Task<bool> SetDeafenAsync(bool enabled)
        {
            bool success = SetMuteMicCore(enabled) && SetMuteSoundCore(enabled);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "deafen-on" : "deafen-off", success, skipped: false, success ? $"Deafen {(enabled ? "enabled" : "disabled")}." : "Failed to set deafen.", success ? null : "Failed to set deafen.", target: "deafen", enabled: success ? enabled : null);
            return Task.FromResult(success);
        }

        /// <summary>
        /// Toggles the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        public bool ToggleListenToInput()
        {
            try
            {
                if (_audioOverrides?.TryToggleListenToInput != null)
                {
                    return _audioOverrides.TryToggleListenToInput();
                }

                return AudioService.TryToggleCurrentInputListenState(out _, out _);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(ToggleListenToInput), "cli-listen-toggle-failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Sets the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        /// <remarks>
        /// This call is idempotent for CLI use: if the state is already applied, the operation still succeeds.
        /// </remarks>
        public bool SetListenToInput(bool enabled)
        {
            try
            {
                if (_audioOverrides?.TrySetListenToInput != null)
                {
                    return _audioOverrides.TrySetListenToInput(enabled);
                }

                return AudioService.TrySetCurrentInputListenState(enabled, out _, out _);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetListenToInput), "cli-listen-set-failed", ex);
                return false;
            }
        }

        public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput, bool redactOutput = false) =>
            ExecuteVolume(playback, deviceId, null, jsonOutput, redactOutput, false);

        public Task<CliExecutionResult> ExecuteAppAudioAsync(CliCommand command) => CliAppAudioOperation.ExecuteAsync(command, _lifetimeCts.Token);
        public Task<CliExecutionResult> ExecuteAppRoutingAsync(CliCommand command)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            return CliAppRoutingOperation.ExecuteAsync(command,
                _audioOverrides?.ApplicationRouting ?? new(AudioService.ReadApplicationRoutingAsync, AudioService.ChangeApplicationRoutingAsync), _lifetimeCts.Token);
        }

        public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput, bool redactOutput = false, bool relative = false) =>
            ExecuteVolume(playback, deviceId, percent, jsonOutput, redactOutput, relative);

        private (bool Success, string Output) ExecuteVolume(bool playback, string? selector, float? percent, bool json, bool redact, bool relative)
        {
            string kind = playback ? "master" : "mic";
            string failure = percent == null ? "volume-get-failed" : relative ? "volume-adjust-failed" : "volume-set-failed";
            string normalized = selector?.Trim() ?? string.Empty;
            if (!CliVolumeOperation.TryResolve(playback, normalized, () => playback ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos(), out string? id, out string? error))
                return (false, CliOutputFormatter.FormatVolumeError(kind, failure, error ?? "Failed to select volume endpoint.", json, normalized, redact));
            if (relative) id ??= GetCurrentDeviceId(playback);
            return CliVolumeOperation.Execute(playback, id, percent, relative, json, redact,
                (out value, out muted) => TryGetEndpointVolume(playback, id, out value, out muted),
                (value, out applied, out muted) => TrySetEndpointVolume(playback, id, value, out applied, out muted),
                () =>
                {
                    string description = playback ? "playback" : "recording";
                    if (HasDefaultEndpoint(playback, id)) return $"Failed to {(percent == null ? "read" : "set")} {description} volume.";
                    return string.IsNullOrWhiteSpace(id) ? $"No default {description} device is available."
                        : $"{char.ToUpperInvariant(description[0])}{description[1..]} device '{id}' is not available.";
                });
        }

        public string GetListenStatus(bool jsonOutput, bool redactOutput)
        {
            (bool? Enabled, string? MonitorTargetOutputDeviceName)? statusSnapshot = _audioOverrides?.GetListenStatusSnapshot?.Invoke();

            bool? currentInputListenEnabled = statusSnapshot?.Enabled;
            if (!currentInputListenEnabled.HasValue && AudioService.TryGetCurrentInputListenState(out bool enabled, out _))
            {
                currentInputListenEnabled = enabled;
            }

            string? listenMonitorTargetOutputDeviceName = statusSnapshot?.MonitorTargetOutputDeviceName;
            if (listenMonitorTargetOutputDeviceName == null)
            {
                AudioService.TryGetCurrentInputListenTargetOutputDeviceName(out listenMonitorTargetOutputDeviceName, out _);
            }

            return CliOutputFormatter.FormatListenStatus(currentInputListenEnabled, listenMonitorTargetOutputDeviceName, jsonOutput, redactOutput);
        }

        public string GetMuteStatus(string target, bool jsonOutput)
        {
            bool enabled = target switch
            {
                "mic" => TryGetDefaultCaptureMute() ?? false,
                "sound" => TryGetDefaultPlaybackMute() ?? false,
                "deafen" => (TryGetDefaultPlaybackMute() ?? false) && (TryGetDefaultCaptureMute() ?? false),
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown mute target."),
            };

            return CliOutputFormatter.FormatMuteStatus(target, enabled, jsonOutput);
        }

        private bool SetMuteMicCore(bool enabled)
        {
            if (_audioOverrides?.TrySetMicrophoneMute != null)
            {
                return _audioOverrides.TrySetMicrophoneMute(enabled);
            }

            return AudioService.SetMicrophoneMute(enabled).FullySucceeded;
        }

        private bool SetMuteSoundCore(bool enabled)
        {
            if (_audioOverrides?.TrySetPlaybackMute != null)
            {
                return _audioOverrides.TrySetPlaybackMute(enabled);
            }

            return AudioService.SetPlaybackMute(enabled).FullySucceeded;
        }

        private bool HasDefaultInputDevice()
        {
            try
            {
                if (_audioOverrides?.HasDefaultInputDevice != null)
                {
                    return _audioOverrides.HasDefaultInputDevice();
                }

                using var device = AudioService.GetDefaultRecordingDevice();
                return device != null;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(HasDefaultInputDevice), "cli-default-input-device-check-failed", ex);
                return false;
            }
        }

        private bool HasDefaultEndpoint(bool playback, string? deviceId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        return targetDevice != null;
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    return targetInputDevice != null;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return device != null;
                }

                if (_audioOverrides?.HasDefaultInputDevice != null)
                {
                    return _audioOverrides.HasDefaultInputDevice();
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                return inputDevice != null;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(HasDefaultEndpoint), playback ? "cli-default-playback-endpoint-check-failed" : "cli-default-capture-endpoint-check-failed", ex);
                return false;
            }
        }

        private bool TryGetEndpointVolume(bool playback, string? deviceId, out float percent, out bool muted)
        {
            percent = 0f;
            muted = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback && _audioOverrides?.GetPlaybackVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.GetPlaybackVolumeByDeviceId(deviceId);
                        percent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (!playback && _audioOverrides?.GetCaptureVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.GetCaptureVolumeByDeviceId(deviceId);
                        percent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        return EndpointVolumeOperations.TryGetEndpointVolumeState(Logger.Instance, targetDevice, "cli-volume-get:master:device", out percent, out muted);
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    return EndpointVolumeOperations.TryGetEndpointVolumeState(Logger.Instance, targetInputDevice, "cli-volume-get:mic:device", out percent, out muted);
                }

                if (playback && _audioOverrides?.GetDefaultPlaybackVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.GetDefaultPlaybackVolume();
                    percent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (!playback && _audioOverrides?.GetDefaultCaptureVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.GetDefaultCaptureVolume();
                    percent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return EndpointVolumeOperations.TryGetEndpointVolumeState(Logger.Instance, device, "cli-volume-get:master", out percent, out muted);
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                return EndpointVolumeOperations.TryGetEndpointVolumeState(Logger.Instance, inputDevice, "cli-volume-get:mic", out percent, out muted);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetEndpointVolume), playback ? "cli-volume-get-playback-failed" : "cli-volume-get-capture-failed", ex);
                return false;
            }
        }

        private bool TrySetEndpointVolume(bool playback, string? deviceId, float targetPercent, out float appliedPercent, out bool muted)
        {
            appliedPercent = 0f;
            muted = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback && _audioOverrides?.TrySetPlaybackVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.TrySetPlaybackVolumeByDeviceId(deviceId, targetPercent);
                        appliedPercent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (!playback && _audioOverrides?.TrySetCaptureVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.TrySetCaptureVolumeByDeviceId(deviceId, targetPercent);
                        appliedPercent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        if (!EndpointVolumeOperations.TryApplyEndpointVolume(Logger.Instance, targetDevice, targetPercent, "cli-volume-set:master:device", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                        {
                            return false;
                        }

                        muted = appliedPercent <= 0f;
                        return true;
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    if (!EndpointVolumeOperations.TryApplyEndpointVolume(Logger.Instance, targetInputDevice, targetPercent, "cli-volume-set:mic:device", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                    {
                        return false;
                    }

                    muted = appliedPercent <= 0f;
                    return true;
                }

                if (playback && _audioOverrides?.TrySetPlaybackVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.TrySetPlaybackVolume(targetPercent);
                    appliedPercent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (!playback && _audioOverrides?.TrySetCaptureVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.TrySetCaptureVolume(targetPercent);
                    appliedPercent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    if (!EndpointVolumeOperations.TryApplyEndpointVolume(Logger.Instance, device, targetPercent, "cli-volume-set:master", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                    {
                        return false;
                    }

                    muted = appliedPercent <= 0f;
                    return true;
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                if (!EndpointVolumeOperations.TryApplyEndpointVolume(Logger.Instance, inputDevice, targetPercent, "cli-volume-set:mic", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                {
                    return false;
                }

                muted = appliedPercent <= 0f;
                return true;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TrySetEndpointVolume), playback ? "cli-volume-set-playback-failed" : "cli-volume-set-capture-failed", ex);
                return false;
            }
        }

        private bool? TryGetDefaultPlaybackMute()
        {
            try
            {
                if (_audioOverrides?.GetDefaultPlaybackMute != null)
                {
                    return _audioOverrides.GetDefaultPlaybackMute();
                }

                using var device = AudioService.GetDefaultPlaybackDevice();
                return device?.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetDefaultPlaybackMute), "cli-default-playback-mute-read-failed", ex);
                return null;
            }
        }

        private bool? TryGetDefaultCaptureMute()
        {
            try
            {
                if (_audioOverrides?.GetDefaultCaptureMute != null)
                {
                    return _audioOverrides.GetDefaultCaptureMute();
                }

                using var device = AudioService.GetDefaultRecordingDevice();
                return device?.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetDefaultCaptureMute), "cli-default-capture-mute-read-failed", ex);
                return null;
            }
        }

        private static void LogAudioOperationFailure(string operation, string eventName, Exception ex)
        {
            Logger.Instance?.Debug(
                "LocalHeadlessCommandRunner",
                () => $"{eventName} | exceptionType={ex.GetType().Name}",
                operation);
        }
    }
}
