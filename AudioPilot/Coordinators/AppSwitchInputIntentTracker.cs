using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal sealed class AppSwitchInputIntentTracker(AudioDeviceService audio) : IDisposable
    {
        private readonly AudioDeviceService _audio = audio;
        private readonly Lock _sync = new();
        private CancellationTokenSource? _activeInputIntentCts;
        private int _latestInputSwitchIntentVersion;
        private string? _activeInputTargetId;
        private string? _activeInputTargetName;
        private string? _activeInputReconnectOverlayDeviceName;

        public (int Version, CancellationToken Token) Begin()
        {
            int version = Interlocked.Increment(ref _latestInputSwitchIntentVersion);
            return (version, ReplaceCancellation());
        }

        public bool IsCurrent(int intentVersion)
        {
            return intentVersion > 0
                && Interlocked.CompareExchange(ref _latestInputSwitchIntentVersion, 0, 0) == intentVersion;
        }

        public CancellationToken GetActiveToken()
        {
            lock (_sync)
            {
                return _activeInputIntentCts?.Token ?? CancellationToken.None;
            }
        }

        public void SetActiveTarget(BluetoothReconnectDeviceKind reconnectKind, string? targetId, string? targetName)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Input)
            {
                return;
            }

            lock (_sync)
            {
                _activeInputTargetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId;
                _activeInputTargetName = string.IsNullOrWhiteSpace(targetName) ? null : targetName;
            }
        }

        public void ClearActiveTarget(BluetoothReconnectDeviceKind reconnectKind)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Input)
            {
                return;
            }

            lock (_sync)
            {
                _activeInputTargetId = null;
                _activeInputTargetName = null;
            }
        }

        public bool DoesRequestedInputTargetMatchActiveTarget(IReadOnlyList<CycleDevice> configuredCycle, bool reverse)
        {
            MMDevice? currentDevice = null;

            try
            {
                currentDevice = _audio.GetDefaultRecordingDevice();
                if (currentDevice == null
                    || !AppSwitchCycleStateResolver.TryResolveConfiguredTarget(configuredCycle, currentDevice.ID, reverse, out CycleDevice requestedTarget))
                {
                    return false;
                }

                lock (_sync)
                {
                    if (!string.IsNullOrWhiteSpace(_activeInputTargetId)
                        && requestedTarget.Id.Equals(_activeInputTargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return !string.IsNullOrWhiteSpace(_activeInputTargetName)
                        && requestedTarget.Name.Equals(_activeInputTargetName, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                currentDevice?.Dispose();
            }
        }

        public void SetReconnectOverlayDeviceName(BluetoothReconnectDeviceKind reconnectKind, string? deviceName)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Input)
            {
                return;
            }

            lock (_sync)
            {
                _activeInputReconnectOverlayDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
            }
        }

        public void ClearReconnectOverlayDeviceName(BluetoothReconnectDeviceKind reconnectKind)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Input)
            {
                return;
            }

            lock (_sync)
            {
                _activeInputReconnectOverlayDeviceName = null;
            }
        }

        public void ShowActiveReconnectOverlayIfAvailable(OverlayService overlay)
        {
            string? reconnectDeviceName;
            lock (_sync)
            {
                reconnectDeviceName = _activeInputReconnectOverlayDeviceName;
            }

            if (!string.IsNullOrWhiteSpace(reconnectDeviceName))
            {
                overlay.Show(OverlayDeviceKind.Input, "Reconnecting input device", reconnectDeviceName);
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? detached;
            lock (_sync)
            {
                detached = _activeInputIntentCts;
                _activeInputIntentCts = null;
            }

            detached?.Cancel();
            detached?.Dispose();
        }

        private CancellationToken ReplaceCancellation()
        {
            CancellationTokenSource nextIntentCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(nextDebounce =>
            {
                lock (_sync)
                {
                    CancellationTokenSource? previous = _activeInputIntentCts;
                    _activeInputIntentCts = nextDebounce;
                    return previous;
                }
            });

            return nextIntentCts.Token;
        }
    }
}
