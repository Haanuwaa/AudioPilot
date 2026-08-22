using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceListenStateHelper(
        Logger logger,
        IInputListenPropertyWriter inputListenPropertyWriter,
        IInputListenPropertyReader inputListenPropertyReader,
        IInputListenAudioDeviceResolver inputListenDeviceResolver)
    {
        private readonly Logger _logger = logger;
        private readonly IInputListenPropertyWriter _inputListenPropertyWriter = inputListenPropertyWriter;
        private readonly IInputListenPropertyReader _inputListenPropertyReader = inputListenPropertyReader;
        private readonly IInputListenAudioDeviceResolver _inputListenDeviceResolver = inputListenDeviceResolver;
        private readonly Lock _listenStateMutationLock = new();

        public bool TryGetCurrentInputListenState(out bool enabled, out string? error)
        {
            enabled = false;
            error = null;

            IAudioEndpointInfo? inputEndpoint = null;
            try
            {
                inputEndpoint = _inputListenDeviceResolver.GetDefaultRecordingEndpoint();
                if (inputEndpoint == null)
                {
                    error = AppConstants.Audio.ErrorCodes.Listen.NoDefaultInputDevice;
                    return false;
                }

                return TryGetListenState(inputEndpoint, out enabled, out error);
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.Listen.StateReadException, nameof(TryGetCurrentInputListenState), ex);
                error = AppConstants.Audio.ErrorCodes.Listen.StateReadException;
                return false;
            }
            finally
            {
                inputEndpoint?.Dispose();
            }
        }

        public bool TryGetCurrentInputListenTargetOutputDeviceName(out string? targetOutputDeviceName, out string? error)
        {
            targetOutputDeviceName = null;
            error = null;

            IAudioEndpointInfo? inputEndpoint = null;
            IAudioEndpointInfo? targetOutputEndpoint = null;
            try
            {
                inputEndpoint = _inputListenDeviceResolver.GetDefaultRecordingEndpoint();
                if (inputEndpoint == null)
                {
                    error = AppConstants.Audio.ErrorCodes.Listen.NoDefaultInputDevice;
                    return false;
                }

                if (!TryGetListenState(inputEndpoint, out bool enabled, out error))
                {
                    return false;
                }

                if (!enabled)
                {
                    return true;
                }

                if (!_inputListenPropertyReader.TryGetListenRenderTargetId(inputEndpoint, out string? renderTargetId, out error))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(renderTargetId))
                {
                    return true;
                }

                targetOutputEndpoint = _inputListenDeviceResolver.TryGetPlaybackEndpoint(renderTargetId);
                if (targetOutputEndpoint == null)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioDeviceService", () => $"Listen monitor target could not be resolved targetId={LogPrivacy.Id(renderTargetId)} reason=unavailable");
                    }

                    return true;
                }

                targetOutputDeviceName = targetOutputEndpoint.Name;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.Listen.StateReadException, nameof(TryGetCurrentInputListenTargetOutputDeviceName), ex);
                error = AppConstants.Audio.ErrorCodes.Listen.StateReadException;
                return false;
            }
            finally
            {
                targetOutputEndpoint?.Dispose();
                inputEndpoint?.Dispose();
            }
        }

        public bool TrySetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, out bool changed, out string? error)
        {
            return TrySetCurrentInputListenState(enabled, preferredRenderDeviceId, string.Empty, out changed, out error);
        }

        public bool TrySetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, string? preferredRenderDeviceName, out bool changed, out string? error)
        {
            InputListenStateChangeResult result = SetCurrentInputListenState(enabled, preferredRenderDeviceId, preferredRenderDeviceName);
            changed = result.Applied;
            error = result.Error;
            return result.Applied;
        }

        public InputListenStateChangeResult SetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, string? preferredRenderDeviceName)
        {
            lock (_listenStateMutationLock)
            {
                IAudioEndpointInfo? inputEndpoint = null;
                try
                {
                    inputEndpoint = _inputListenDeviceResolver.GetDefaultRecordingEndpoint();
                    if (inputEndpoint == null)
                    {
                        return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, enabled, AppConstants.Audio.ErrorCodes.Listen.NoDefaultInputDevice);
                    }

                    bool applied = TrySetInputListenStateOnEndpoint(inputEndpoint, enabled, preferredRenderDeviceId, preferredRenderDeviceName, out bool changed, out bool verified, out string? error);
                    InputListenApplyStatus status = applied && changed
                        ? verified ? InputListenApplyStatus.AppliedVerified : InputListenApplyStatus.AppliedUnverified
                        : InputListenApplyStatus.NotApplied;
                    return new InputListenStateChangeResult(status, enabled, error);
                }
                catch (Exception ex)
                {
                    _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.Listen.StateSetException, nameof(TrySetCurrentInputListenState), ex);
                    return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, enabled, AppConstants.Audio.ErrorCodes.Listen.StateSetException);
                }
                finally
                {
                    inputEndpoint?.Dispose();
                }
            }
        }

        public bool TryToggleCurrentInputListenState(string? preferredRenderDeviceId, out bool enabled, out string? error)
        {
            return TryToggleCurrentInputListenState(preferredRenderDeviceId, string.Empty, out enabled, out error);
        }

        public bool TryToggleCurrentInputListenState(string? preferredRenderDeviceId, string? preferredRenderDeviceName, out bool enabled, out string? error)
        {
            InputListenStateChangeResult result = ToggleCurrentInputListenState(preferredRenderDeviceId, preferredRenderDeviceName);
            enabled = result.Enabled;
            error = result.Error;
            return result.Applied;
        }

        public InputListenStateChangeResult ToggleCurrentInputListenState(string? preferredRenderDeviceId, string? preferredRenderDeviceName)
        {
            lock (_listenStateMutationLock)
            {
                IAudioEndpointInfo? inputEndpoint = null;
                try
                {
                    inputEndpoint = _inputListenDeviceResolver.GetDefaultRecordingEndpoint();
                    if (inputEndpoint == null)
                    {
                        return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, Enabled: false, Error: AppConstants.Audio.ErrorCodes.Listen.NoDefaultInputDevice);
                    }

                    if (!TryGetListenState(inputEndpoint, out bool current, out string? error))
                    {
                        return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, Enabled: false, error);
                    }

                    bool target = !current;
                    if (!TrySetInputListenStateOnEndpoint(inputEndpoint, target, preferredRenderDeviceId, preferredRenderDeviceName, out bool changed, out bool verified, out error))
                    {
                        return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, target, error);
                    }

                    if (!changed)
                    {
                        return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, target, AppConstants.Audio.ErrorCodes.Listen.StateVerifyMismatch);
                    }

                    return new InputListenStateChangeResult(
                        verified ? InputListenApplyStatus.AppliedVerified : InputListenApplyStatus.AppliedUnverified,
                        target,
                        error);
                }
                catch (Exception ex)
                {
                    _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.Listen.StateSetException, nameof(TryToggleCurrentInputListenState), ex);
                    return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, Enabled: false, Error: AppConstants.Audio.ErrorCodes.Listen.StateSetException);
                }
                finally
                {
                    inputEndpoint?.Dispose();
                }
            }
        }

        private bool TryGetListenState(IAudioEndpointInfo inputEndpoint, out bool enabled, out string? error)
        {
            return _inputListenPropertyReader.TryGetListenEnabled(inputEndpoint, out enabled, out error);
        }

        private bool TrySetInputListenStateOnEndpoint(
            IAudioEndpointInfo inputEndpoint,
            bool enabled,
            string? preferredRenderDeviceId,
            string? preferredRenderDeviceName,
            out bool changed,
            out bool verified,
            out string? error)
        {
            changed = false;
            verified = false;
            error = null;

            IAudioEndpointInfo? playbackEndpoint = null;
            try
            {
                string inputDeviceId = inputEndpoint.Id;
                string inputDeviceName = inputEndpoint.Name;

                if (enabled)
                {
                    playbackEndpoint = TryResolvePreferredPlaybackEndpoint(preferredRenderDeviceId, preferredRenderDeviceName);
                    playbackEndpoint ??= _inputListenDeviceResolver.GetDefaultPlaybackEndpoint();
                    if (playbackEndpoint == null)
                    {
                        error = AppConstants.Audio.ErrorCodes.Listen.NoDefaultOutputDevice;
                        return false;
                    }
                }

                string? renderDeviceId = enabled ? playbackEndpoint?.Id : null;
                if (!_inputListenPropertyWriter.TrySetListenProperties(inputEndpoint, renderDeviceId, enabled, out string? setError))
                {
                    error = setError ?? AppConstants.Audio.ErrorCodes.Listen.StateSetFailed;
                    return false;
                }

                bool verifiedEnabled = TryGetListenState(inputEndpoint, out bool postEnabled, out _);
                bool verifiedRenderTarget = _inputListenPropertyReader.TryGetListenRenderTargetId(inputEndpoint, out string? postRenderDeviceId, out _);
                if (!verifiedEnabled || !verifiedRenderTarget)
                {
                    changed = true;
                    error = AppConstants.Audio.ErrorCodes.Listen.StateVerifyUnknown;
                    _logger.Warning(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.Listen.SetVerifyUnknown} | target={enabled} stateReadVerified={verifiedEnabled} renderTargetReadVerified={verifiedRenderTarget} device={LogPrivacy.Device(inputDeviceName)} id={LogPrivacy.Id(inputDeviceId)}");
                    return true;
                }

                bool enabledMatches = postEnabled == enabled;
                bool renderTargetMatches = enabled
                    ? string.Equals(postRenderDeviceId, renderDeviceId, StringComparison.OrdinalIgnoreCase)
                    : string.IsNullOrWhiteSpace(postRenderDeviceId);

                changed = enabledMatches && renderTargetMatches;
                verified = true;
                if (changed)
                {
                    _logger.Info(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.Listen.SetSuccess} | target={enabled} verified={postEnabled} renderTargetVerified={LogPrivacy.Id(postRenderDeviceId)} device={LogPrivacy.Device(inputDeviceName)} id={LogPrivacy.Id(inputDeviceId)}");
                    return true;
                }

                error = AppConstants.Audio.ErrorCodes.Listen.StateVerifyMismatch;
                _logger.Warning(
                    "AudioDeviceService",
                    () => $"{AppConstants.Audio.LogEvents.Listen.SetVerifyMismatch} | target={enabled} verified={postEnabled} renderTargetExpected={LogPrivacy.Id(renderDeviceId)} renderTargetVerified={LogPrivacy.Id(postRenderDeviceId)} device={LogPrivacy.Device(inputDeviceName)} id={LogPrivacy.Id(inputDeviceId)}");
                return false;
            }
            finally
            {
                playbackEndpoint?.Dispose();
            }
        }

        private IAudioEndpointInfo? TryResolvePreferredPlaybackEndpoint(string? preferredRenderDeviceId, string? preferredRenderDeviceName)
        {
            string preferredRenderId = preferredRenderDeviceId?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(preferredRenderId))
            {
                IAudioEndpointInfo? exactEndpoint = _inputListenDeviceResolver.TryGetPlaybackEndpoint(preferredRenderId);
                if (exactEndpoint != null)
                {
                    return exactEndpoint;
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"Configured listen monitor output unavailable targetId={LogPrivacy.Id(preferredRenderId)} reason=unavailable");
                }
            }

            if (string.IsNullOrWhiteSpace(preferredRenderDeviceName))
            {
                return null;
            }

            CycleDevice? remappedDevice = PersistedAudioDeviceResolver.TryResolveUniqueBestNameMatch(
                preferredRenderDeviceName,
                _inputListenDeviceResolver.GetActivePlaybackDeviceInfos());
            if (remappedDevice == null)
            {
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("AudioDeviceService", () => $"Configured listen monitor output remapped targetId={LogPrivacy.Id(preferredRenderId)} remappedId={LogPrivacy.Id(remappedDevice.Id)} reason=name-match");
            }

            return _inputListenDeviceResolver.TryGetPlaybackEndpoint(remappedDevice.Id);
        }
    }
}
