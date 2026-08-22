using System.Runtime.InteropServices;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceEndpointQueryHelper(
        MMDeviceEnumerator enumerator,
        ReaderWriterLockSlim enumeratorLock,
        Logger logger,
        Func<bool> isDisposed,
        Func<NRole[]> getConfiguredOutputRolesSnapshot,
        Func<NRole[]> getConfiguredInputRolesSnapshot,
        Action<string, Func<string>> logDebugWithCooldown)
    {
        private readonly MMDeviceEnumerator _enumerator = enumerator;
        private readonly ReaderWriterLockSlim _enumeratorLock = enumeratorLock;
        private readonly Logger _logger = logger;
        private readonly Func<bool> _isDisposed = isDisposed;
        private readonly Func<NRole[]> _getConfiguredOutputRolesSnapshot = getConfiguredOutputRolesSnapshot;
        private readonly Func<NRole[]> _getConfiguredInputRolesSnapshot = getConfiguredInputRolesSnapshot;
        private readonly Action<string, Func<string>> _logDebugWithCooldown = logDebugWithCooldown;

        internal MMDeviceCollection GetActivePlaybackDevices()
        {
            _enumeratorLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_isDisposed(), typeof(AudioDeviceService));

                MMDeviceCollection devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, NDeviceState.Active);
                _logDebugWithCooldown("EnumeratePlaybackDevices", () => $"Enumerated {devices.Count} active playback devices");
                return devices;
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(GetActivePlaybackDevices), ex);
                throw;
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(GetActivePlaybackDevices), ex);
                throw;
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        internal IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds)
        {
            if (deviceIds == null || deviceIds.Count == 0)
            {
                return [];
            }

            _enumeratorLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_isDisposed(), typeof(AudioDeviceService));

                List<MMDevice> devices = AudioDevicePlaybackDeviceLookupHelper.ResolveActivePlaybackDevicesById(
                    deviceIds,
                    _enumerator.GetDevice,
                    static device => device.DataFlow == DataFlow.Render,
                    static device => device.State == NDeviceState.Active,
                    static device => device.Dispose(),
                    ex => AudioDeviceHelper.LogComException(_logger, nameof(GetPlaybackDevicesById), ex),
                    ex => AudioDeviceHelper.LogException(_logger, nameof(GetPlaybackDevicesById), ex));

                _logDebugWithCooldown("GetPlaybackDevicesById", () => $"Materialized {devices.Count} playback devices by id");
                return devices;
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        internal MMDeviceCollection GetActiveCaptureDevices()
        {
            _enumeratorLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_isDisposed(), typeof(AudioDeviceService));

                MMDeviceCollection devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, NDeviceState.Active);
                _logDebugWithCooldown("EnumerateCaptureDevices", () => $"Enumerated {devices.Count} active capture devices");
                return devices;
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(GetActiveCaptureDevices), ex);
                throw;
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(GetActiveCaptureDevices), ex);
                throw;
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        internal MMDevice GetDefaultPlaybackDevice(string? _reason = null)
        {
            _ = _reason;
            _enumeratorLock.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_isDisposed(), typeof(AudioDeviceService));
                NRole detectionRole = AudioDeviceService.ResolveDetectionRole(_getConfiguredOutputRolesSnapshot(), NRole.Multimedia);
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, detectionRole);
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(GetDefaultPlaybackDevice), ex);
                throw new InvalidOperationException("Unable to retrieve default playback device", ex);
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(GetDefaultPlaybackDevice), ex);
                throw new InvalidOperationException("Unable to retrieve default playback device", ex);
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        internal MMDevice? GetDefaultRecordingDevice(string? _reason = null)
        {
            _ = _reason;
            _enumeratorLock.EnterReadLock();
            try
            {
                if (_isDisposed())
                {
                    return null;
                }

                try
                {
                    NRole detectionRole = AudioDeviceService.ResolveDetectionRole(_getConfiguredInputRolesSnapshot(), NRole.Console);
                    return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, detectionRole);
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
                {
                    _logger.Trace("AudioDeviceService", "No recording device available (expected in some scenarios)");
                    return null;
                }
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, nameof(GetDefaultRecordingDevice), ex);
                return null;
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, nameof(GetDefaultRecordingDevice), ex);
                return null;
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        internal List<CycleDevice> GetActiveCycleDevices(Func<MMDeviceCollection> getDevices, string operationName)
        {
            MMDeviceCollection? devices = null;
            try
            {
                devices = getDevices();
                return AudioDeviceCycleCollectionProjectionHelper.Project<MMDeviceCollection, MMDevice, CycleDevice>(
                    devices,
                    static (collection, onDisposeFailure) => AudioDeviceCollectionHelper.ProjectCycleDevices(
                        collection,
                        onDisposeFailure: onDisposeFailure),
                    static device => device?.FriendlyName,
                    static device => device?.ID,
                    message =>
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("AudioDeviceService", () => message);
                        }
                    });
            }
            catch (COMException ex)
            {
                AudioDeviceHelper.LogComException(_logger, operationName, ex);
                throw;
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, operationName, ex);
                throw;
            }
            finally
            {
                devices?.Dispose();
            }
        }

        internal List<MMDevice?> GetAllDefaultPlaybackDevices()
        {
            _enumeratorLock.EnterReadLock();
            try
            {
                if (_isDisposed())
                {
                    return [];
                }

                return AudioDeviceDefaultEndpointCollectionHelper.GetDistinctDefaultDevicesForRoles(
                    role => _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role),
                    static device => device.ID,
                    static device => device.Dispose(),
                    role => _logger.Trace("AudioDeviceService", () => $"No playback device available for role {role}"),
                    (role, ex, _) =>
                    {
                        if (ex is COMException comException)
                        {
                            AudioDeviceHelper.LogComException(_logger, nameof(GetAllDefaultPlaybackDevices), comException);
                            return;
                        }

                        _logger.Error("AudioDeviceService", () => $"Unexpected error getting playback device for role {role}", nameof(GetAllDefaultPlaybackDevices), ex);
                    });
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        internal List<MMDevice?> GetAllDefaultRecordingDevices()
        {
            _enumeratorLock.EnterReadLock();
            try
            {
                if (_isDisposed())
                {
                    return [];
                }

                return AudioDeviceDefaultEndpointCollectionHelper.GetDistinctDefaultDevicesForRoles(
                    role => _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role),
                    static device => device.ID,
                    static device => device.Dispose(),
                    role => _logger.Trace("AudioDeviceService", () => $"No recording device available for role {role}"),
                    (role, ex, _) =>
                    {
                        if (ex is COMException comException)
                        {
                            AudioDeviceHelper.LogComException(_logger, nameof(GetAllDefaultRecordingDevices), comException);
                            return;
                        }

                        _logger.Error("AudioDeviceService", () => $"Unexpected error getting recording device for role {role}", nameof(GetAllDefaultRecordingDevices), ex);
                    });
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }

        internal MMDevice? TryGetDeviceById(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            _enumeratorLock.EnterReadLock();
            try
            {
                if (_isDisposed())
                {
                    return null;
                }

                return _enumerator.GetDevice(deviceId);
            }
            catch (COMException ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"TryGetDeviceById failed | id={LogPrivacy.Id(deviceId)} reason=com-exception hresult=0x{ex.HResult:X8}");
                }

                return null;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"TryGetDeviceById failed | id={LogPrivacy.Id(deviceId)} reason={ex.GetType().Name}");
                }

                return null;
            }
            finally
            {
                _enumeratorLock.ExitReadLock();
            }
        }
    }
}
