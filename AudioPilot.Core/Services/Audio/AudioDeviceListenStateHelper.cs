using System.Runtime.CompilerServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Audio
{
    internal readonly record struct InputListenStatusSnapshot(
        bool? Enabled,
        string? MonitorTargetOutputDeviceName,
        bool MonitorTargetUnavailable = false);

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

        public InputListenStatusSnapshot GetCurrentInputListenStatus()
        {
            IAudioEndpointInfo? inputEndpoint = null;
            IAudioEndpointInfo? targetOutputEndpoint = null;
            try
            {
                inputEndpoint = _inputListenDeviceResolver.GetDefaultRecordingEndpoint();
                if (inputEndpoint == null)
                {
                    return new InputListenStatusSnapshot(null, null);
                }

                InputListenPropertiesSnapshot properties = _inputListenPropertyReader.ReadListenSnapshot(inputEndpoint);
                if (!properties.EnabledRead)
                {
                    return new InputListenStatusSnapshot(null, null);
                }

                if (!properties.Enabled)
                {
                    return new InputListenStatusSnapshot(false, null);
                }

                if (!properties.RenderTargetRead)
                {
                    return new InputListenStatusSnapshot(true, null);
                }

                if (string.IsNullOrWhiteSpace(properties.RenderTargetId))
                {
                    targetOutputEndpoint = _inputListenDeviceResolver.GetDefaultPlaybackEndpoint();
                    string? defaultName = targetOutputEndpoint == null ? null : ReadEndpointName(targetOutputEndpoint);
                    return new InputListenStatusSnapshot(true, defaultName);
                }

                targetOutputEndpoint = _inputListenDeviceResolver.TryGetPlaybackEndpoint(properties.RenderTargetId);
                if (targetOutputEndpoint == null)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.Trace("AudioDeviceService", () => $"Listen monitor target could not be resolved targetId={LogPrivacy.Id(properties.RenderTargetId)} reason=unavailable");
                    }

                    return new InputListenStatusSnapshot(true, null, MonitorTargetUnavailable: true);
                }

                return new InputListenStatusSnapshot(true, ReadEndpointName(targetOutputEndpoint));
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.Listen.StateReadException, nameof(GetCurrentInputListenStatus), ex);
                return new InputListenStatusSnapshot(null, null);
            }
            finally
            {
                DisposeEndpoint(targetOutputEndpoint);
                DisposeEndpoint(inputEndpoint);
            }
        }

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
                DisposeEndpoint(inputEndpoint);
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

                    error = AppConstants.Audio.ErrorCodes.Listen.StateReadFailed;
                    return false;
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
                DisposeEndpoint(targetOutputEndpoint);
                DisposeEndpoint(inputEndpoint);
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

        public InputListenStateChangeResult SetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, string? preferredRenderDeviceName, string? preferredRenderDeviceStableId = null)
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

                    return SetInputListenStateOnEndpoint(inputEndpoint, enabled, preferredRenderDeviceId, preferredRenderDeviceName, preferredRenderDeviceStableId);
                }
                catch (Exception ex)
                {
                    _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.Listen.StateSetException, nameof(TrySetCurrentInputListenState), ex);
                    return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, enabled, AppConstants.Audio.ErrorCodes.Listen.StateSetException);
                }
                finally
                {
                    DisposeEndpoint(inputEndpoint);
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

        public InputListenStateChangeResult ToggleCurrentInputListenState(string? preferredRenderDeviceId, string? preferredRenderDeviceName, string? preferredRenderDeviceStableId = null)
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

                    return SetInputListenStateOnEndpoint(inputEndpoint, !current, preferredRenderDeviceId, preferredRenderDeviceName, preferredRenderDeviceStableId);
                }
                catch (Exception ex)
                {
                    _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.Listen.StateSetException, nameof(TryToggleCurrentInputListenState), ex);
                    return new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, Enabled: false, Error: AppConstants.Audio.ErrorCodes.Listen.StateSetException);
                }
                finally
                {
                    DisposeEndpoint(inputEndpoint);
                }
            }
        }

        private bool TryGetListenState(IAudioEndpointInfo inputEndpoint, out bool enabled, out string? error)
        {
            return _inputListenPropertyReader.TryGetListenEnabled(inputEndpoint, out enabled, out error);
        }

        private InputListenStateChangeResult SetInputListenStateOnEndpoint(
            IAudioEndpointInfo inputEndpoint,
            bool enabled,
            string? preferredRenderDeviceId,
            string? preferredRenderDeviceName,
            string? preferredRenderDeviceStableId = null)
        {
            IAudioEndpointInfo? playbackEndpoint = null;
            try
            {
                string inputDeviceId = inputEndpoint.Id;
                string? inputDeviceName = ReadEndpointName(inputEndpoint);
                var result = new InputListenStateChangeResult(InputListenApplyStatus.NotApplied, enabled, null, inputDeviceId, inputDeviceName);

                bool explicitOutput = false;
                if (enabled)
                {
                    explicitOutput = !string.IsNullOrWhiteSpace(preferredRenderDeviceId)
                        || !string.IsNullOrWhiteSpace(preferredRenderDeviceName) || !string.IsNullOrEmpty(preferredRenderDeviceStableId);
                    playbackEndpoint = explicitOutput
                        ? TryResolvePreferredPlaybackEndpoint(preferredRenderDeviceId, preferredRenderDeviceName, preferredRenderDeviceStableId)
                        : _inputListenDeviceResolver.GetDefaultPlaybackEndpoint();
                    if (playbackEndpoint == null)
                    {
                        return result with { Error = explicitOutput ? AppConstants.Audio.ErrorCodes.Listen.MonitorOutputUnavailable : AppConstants.Audio.ErrorCodes.Listen.NoDefaultOutputDevice };
                    }
                    result = result with { MonitorOutputDeviceId = playbackEndpoint.Id, MonitorOutputDeviceName = ReadEndpointName(playbackEndpoint) };
                }

                // Write VT_EMPTY for Default output so Windows dynamically follows default playback changes.
                string? renderDeviceIdToWrite = enabled && explicitOutput ? result.MonitorOutputDeviceId : null;
                if (!_inputListenPropertyWriter.TrySetListenProperties(inputEndpoint, renderDeviceIdToWrite, enabled, out string? setError))
                {
                    return result with { Error = setError ?? AppConstants.Audio.ErrorCodes.Listen.StateSetFailed };
                }

                InputListenPropertiesSnapshot postSnapshot = _inputListenPropertyReader.ReadListenSnapshot(inputEndpoint);
                bool verifiedEnabled = postSnapshot.EnabledRead;
                bool postEnabled = postSnapshot.Enabled;
                bool verifiedRenderTarget = postSnapshot.RenderTargetRead;
                string? postRenderDeviceId = postSnapshot.RenderTargetId;
                bool enabledMatches = postEnabled == enabled;
                bool renderTargetMatches = !string.IsNullOrWhiteSpace(renderDeviceIdToWrite)
                    ? string.Equals(postRenderDeviceId, renderDeviceIdToWrite, StringComparison.OrdinalIgnoreCase)
                    : string.IsNullOrWhiteSpace(postRenderDeviceId);

                if ((verifiedEnabled && !enabledMatches) || (verifiedRenderTarget && !renderTargetMatches))
                {
                    _logger.Warning(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.Listen.SetVerifyMismatch} | target={enabled} verified={postEnabled} stateReadVerified={verifiedEnabled} renderTargetReadVerified={verifiedRenderTarget} renderTargetExpected={LogPrivacy.Id(renderDeviceIdToWrite)} renderTargetVerified={LogPrivacy.Id(postRenderDeviceId)} device={LogPrivacy.Device(inputDeviceName)} id={LogPrivacy.Id(inputDeviceId)}");
                    return result with { Error = AppConstants.Audio.ErrorCodes.Listen.StateVerifyMismatch };
                }

                if (!verifiedEnabled || !verifiedRenderTarget)
                {
                    _logger.Warning(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.Listen.SetVerifyUnknown} | target={enabled} stateReadVerified={verifiedEnabled} renderTargetReadVerified={verifiedRenderTarget} device={LogPrivacy.Device(inputDeviceName)} id={LogPrivacy.Id(inputDeviceId)}");
                    return result with { Status = InputListenApplyStatus.AppliedUnverified, Error = AppConstants.Audio.ErrorCodes.Listen.StateVerifyUnknown };
                }

                _logger.Info(
                    "AudioDeviceService",
                    () => $"{AppConstants.Audio.LogEvents.Listen.SetSuccess} | target={enabled} verified={postEnabled} renderTargetVerified={LogPrivacy.Id(postRenderDeviceId)} device={LogPrivacy.Device(inputDeviceName)} id={LogPrivacy.Id(inputDeviceId)}");
                return result with { Status = InputListenApplyStatus.AppliedVerified };
            }
            finally
            {
                DisposeEndpoint(playbackEndpoint);
            }
        }

        private string? ReadEndpointName(IAudioEndpointInfo endpoint)
        {
            try { return endpoint.Name; }
            catch (Exception ex)
            {
                _logger.Trace("AudioDeviceService", () => $"listen-endpoint-name-unavailable | error={ex.GetType().Name}");
                return null;
            }
        }

        /// <summary>Releases each owned endpoint without replacing the operation's read or write result.</summary>
        private void DisposeEndpoint(IAudioEndpointInfo? endpoint, [CallerMemberName] string operation = "")
        {
            try { endpoint?.Dispose(); }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", "listen-endpoint-dispose-failed", operation, ex);
            }
        }

        private IAudioEndpointInfo? TryResolvePreferredPlaybackEndpoint(string? preferredRenderDeviceId, string? preferredRenderDeviceName, string? preferredRenderDeviceStableId)
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

            if (string.IsNullOrWhiteSpace(preferredRenderDeviceName) && string.IsNullOrEmpty(preferredRenderDeviceStableId))
            {
                return null;
            }

            IReadOnlyList<CycleDevice> availableDevices = _inputListenDeviceResolver.GetActivePlaybackDeviceInfos();
            CycleDevice? remappedDevice = string.IsNullOrEmpty(preferredRenderId)
                ? !string.IsNullOrEmpty(preferredRenderDeviceStableId)
                    ? PersistedAudioDeviceResolver.TryFindByStableId(preferredRenderDeviceStableId, availableDevices)
                    : PersistedAudioDeviceResolver.TryResolveUniqueBestNameMatch(preferredRenderDeviceName, availableDevices)
                : PersistedAudioDeviceResolver.TryResolveMatch(
                    new CycleDevice { Id = preferredRenderId, Name = preferredRenderDeviceName ?? string.Empty, StableId = preferredRenderDeviceStableId }, availableDevices);
            if (remappedDevice == null)
            {
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("AudioDeviceService", () => $"Configured listen monitor output remapped targetId={LogPrivacy.Id(preferredRenderId)} remappedId={LogPrivacy.Id(remappedDevice.Id)} reason={(string.Equals(remappedDevice.StableId, preferredRenderDeviceStableId, StringComparison.Ordinal) && preferredRenderDeviceStableId != null ? "stable-id-match" : "name-match")}");
            }

            return _inputListenDeviceResolver.TryGetPlaybackEndpoint(remappedDevice.Id);
        }
    }
}
