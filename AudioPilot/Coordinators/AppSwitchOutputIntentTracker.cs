using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal sealed class AppSwitchOutputIntentTracker(AudioDeviceService audio) : IDisposable
    {
        private readonly AudioDeviceService _audio = audio;
        private readonly Lock _sync = new();
        private CancellationTokenSource? _activeOutputIntentCts;
        private int _latestOutputSwitchIntentVersion;
        private string? _activeOutputTargetId;
        private string? _activeOutputTargetName;
        private string? _activeOutputReconnectOverlayDeviceName;

        public (int Version, CancellationToken Token) Begin()
        {
            int version = Interlocked.Increment(ref _latestOutputSwitchIntentVersion);
            return (version, ReplaceCancellation());
        }

        public bool IsCurrent(int intentVersion)
        {
            return intentVersion > 0
                && Interlocked.CompareExchange(ref _latestOutputSwitchIntentVersion, 0, 0) == intentVersion;
        }

        public CancellationToken GetActiveToken()
        {
            lock (_sync)
            {
                return _activeOutputIntentCts?.Token ?? CancellationToken.None;
            }
        }

        public void SetActiveTarget(BluetoothReconnectDeviceKind reconnectKind, string? targetId, string? targetName)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Output)
            {
                return;
            }

            lock (_sync)
            {
                _activeOutputTargetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId;
                _activeOutputTargetName = string.IsNullOrWhiteSpace(targetName) ? null : targetName;
            }
        }

        public void ClearActiveTarget(BluetoothReconnectDeviceKind reconnectKind)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Output)
            {
                return;
            }

            lock (_sync)
            {
                _activeOutputTargetId = null;
                _activeOutputTargetName = null;
            }
        }

        public bool DoesRequestedOutputTargetMatchActiveTarget(IReadOnlyList<CycleDevice> configuredCycle, bool reverse)
        {
            MMDevice? currentDevice = null;

            try
            {
                currentDevice = _audio.GetDefaultPlaybackDevice();
                if (currentDevice == null
                    || !AppSwitchCycleStateResolver.TryResolveConfiguredTarget(configuredCycle, currentDevice.ID, reverse, out CycleDevice requestedTarget))
                {
                    return false;
                }

                lock (_sync)
                {
                    if (!string.IsNullOrWhiteSpace(_activeOutputTargetId)
                        && requestedTarget.Id.Equals(_activeOutputTargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return !string.IsNullOrWhiteSpace(_activeOutputTargetName)
                        && requestedTarget.Name.Equals(_activeOutputTargetName, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                currentDevice?.Dispose();
            }
        }

        public void SetReconnectOverlayDeviceName(BluetoothReconnectDeviceKind reconnectKind, string? deviceName)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Output)
            {
                return;
            }

            lock (_sync)
            {
                _activeOutputReconnectOverlayDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
            }
        }

        public void ClearReconnectOverlayDeviceName(BluetoothReconnectDeviceKind reconnectKind)
        {
            if (reconnectKind != BluetoothReconnectDeviceKind.Output)
            {
                return;
            }

            lock (_sync)
            {
                _activeOutputReconnectOverlayDeviceName = null;
            }
        }

        public void ShowActiveReconnectOverlayIfAvailable(OverlayService overlay)
        {
            string? reconnectDeviceName;
            lock (_sync)
            {
                reconnectDeviceName = _activeOutputReconnectOverlayDeviceName;
            }

            if (!string.IsNullOrWhiteSpace(reconnectDeviceName))
            {
                overlay.Show(OverlayDeviceKind.Output, "Reconnecting output device", reconnectDeviceName);
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? detached;
            lock (_sync)
            {
                detached = _activeOutputIntentCts;
                _activeOutputIntentCts = null;
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
                    CancellationTokenSource? previous = _activeOutputIntentCts;
                    _activeOutputIntentCts = nextDebounce;
                    return previous;
                }
            });

            return nextIntentCts.Token;
        }
    }
}
