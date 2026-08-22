using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    /// <summary>Owns the current switch request and its reconnect state independently of audio devices and UI controls.</summary>
    internal sealed class AppSwitchIntentTracker(BluetoothReconnectDeviceKind kind) : IDisposable
    {
        private static readonly CancellationToken CanceledToken = new(canceled: true);
        private readonly Lock _sync = new();
        private CancellationTokenSource? _activeIntentCts;
        private CancellationToken _activeToken;
        private int _latestVersion;
        private bool _disposed;
        private string? _activeTargetId;
        private string? _activeTargetName;
        private string? _reconnectOverlayDeviceName;

        public (int Version, CancellationToken Token) Begin()
        {
            CancellationTokenSource? previous;
            CancellationToken token;
            int version;
            lock (_sync)
            {
                if (_disposed)
                {
                    return (0, CanceledToken);
                }

                var next = new CancellationTokenSource();
                token = next.Token;
                version = ++_latestVersion;
                previous = _activeIntentCts;
                _activeIntentCts = next;
                _activeToken = token;
            }

            try
            {
                previous?.Cancel();
            }
            finally
            {
                previous?.Dispose();
            }
            return (version, token);
        }

        public bool IsCurrent(int version)
        {
            lock (_sync)
            {
                return !_disposed && version > 0 && _latestVersion == version;
            }
        }

        public CancellationToken GetActiveToken()
        {
            lock (_sync)
            {
                return _activeToken;
            }
        }

        public void SetActiveTarget(BluetoothReconnectDeviceKind reconnectKind, string? targetId, string? targetName)
        {
            lock (_sync)
            {
                if (_disposed || reconnectKind != kind)
                {
                    return;
                }
                _activeTargetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId;
                _activeTargetName = string.IsNullOrWhiteSpace(targetName) ? null : targetName;
            }
        }

        public void ClearActiveTarget(BluetoothReconnectDeviceKind reconnectKind)
        {
            SetActiveTarget(reconnectKind, null, null);
        }

        public bool DoesRequestedTargetMatchActiveTarget(IReadOnlyList<CycleDevice> configuredCycle, string? currentDeviceId, bool reverse)
        {
            if (!AppSwitchCycleStateResolver.TryResolveConfiguredTarget(configuredCycle, currentDeviceId, reverse, out CycleDevice requestedTarget))
            {
                return false;
            }

            lock (_sync)
            {
                return !_disposed
                    && ((!string.IsNullOrWhiteSpace(_activeTargetId) && requestedTarget.Id.Equals(_activeTargetId, StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(_activeTargetName) && requestedTarget.Name.Equals(_activeTargetName, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public void SetReconnectOverlayDeviceName(BluetoothReconnectDeviceKind reconnectKind, string? deviceName)
        {
            lock (_sync)
            {
                if (!_disposed && reconnectKind == kind)
                {
                    _reconnectOverlayDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
                }
            }
        }

        public void ClearReconnectOverlayDeviceName(BluetoothReconnectDeviceKind reconnectKind)
        {
            SetReconnectOverlayDeviceName(reconnectKind, null);
        }

        public string? GetReconnectOverlayDeviceName()
        {
            lock (_sync)
            {
                return _reconnectOverlayDeviceName;
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? detached;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                detached = _activeIntentCts;
                _activeIntentCts = null;
                _activeToken = CanceledToken;
                _activeTargetId = null;
                _activeTargetName = null;
                _reconnectOverlayDeviceName = null;
            }

            try
            {
                detached?.Cancel();
            }
            finally
            {
                detached?.Dispose();
            }
        }
    }
}
