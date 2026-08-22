using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Services.Bluetooth
{
    public static class BluetoothReconnectRuntimeConfig
    {
        private static int _maxAttempts = AppConstants.Bluetooth.ReconnectMaxAttemptsDefault;
        private static int _attemptTimeoutMs = AppConstants.Timing.BluetoothReconnectAttemptTimeoutMs;
        private static int _cooldownMs = AppConstants.Timing.BluetoothReconnectCooldownMs;
        private static int _onlyLikely = AppConstants.Bluetooth.OnlyLikelyBluetoothEndpointsDefault ? 1 : 0;

        public static int MaxAttempts
        {
            get => Volatile.Read(ref _maxAttempts);
            set => Volatile.Write(ref _maxAttempts, Math.Clamp(value, AppConstants.Limits.BluetoothReconnectMinAttempts, AppConstants.Limits.BluetoothReconnectMaxAttempts));
        }

        public static int AttemptTimeoutMs
        {
            get => Volatile.Read(ref _attemptTimeoutMs);
            set => Volatile.Write(ref _attemptTimeoutMs, Math.Clamp(value, AppConstants.Limits.BluetoothReconnectMinAttemptTimeoutMs, AppConstants.Limits.BluetoothReconnectMaxAttemptTimeoutMs));
        }

        public static int CooldownMs
        {
            get => Volatile.Read(ref _cooldownMs);
            set => Volatile.Write(ref _cooldownMs, Math.Clamp(value, AppConstants.Limits.BluetoothReconnectMinCooldownMs, AppConstants.Limits.BluetoothReconnectMaxCooldownMs));
        }

        public static bool OnlyLikelyBluetoothEndpoints
        {
            get => Volatile.Read(ref _onlyLikely) != 0;
            set => Volatile.Write(ref _onlyLikely, value ? 1 : 0);
        }

        public static void Apply(BluetoothReconnectAdvancedTuningSettings? settings)
        {
            BluetoothReconnectAdvancedTuningSettings effectiveSettings = settings ?? new BluetoothReconnectAdvancedTuningSettings();
            MaxAttempts = effectiveSettings.MaxAttempts;
            AttemptTimeoutMs = effectiveSettings.AttemptTimeoutMs;
            CooldownMs = effectiveSettings.CooldownMs;
            RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts = effectiveSettings.CachedEndpointVisibilityProbeAttempts;
            RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs = effectiveSettings.CachedEndpointVisibilityProbeDelayMs;
            OnlyLikelyBluetoothEndpoints = effectiveSettings.OnlyLikelyBluetoothEndpoints;
        }
    }
}
