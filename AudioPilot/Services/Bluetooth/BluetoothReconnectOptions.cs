using AudioPilot.Models;

namespace AudioPilot.Services.Bluetooth
{
    public readonly record struct BluetoothReconnectOptions(
        bool Enabled,
        int MaxAttempts,
        int AttemptTimeoutMs,
        int CooldownMs,
        bool OnlyLikelyBluetoothEndpoints)
    {
        public static BluetoothReconnectOptions FromSettings(Settings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            return new BluetoothReconnectOptions(
                Enabled: settings.Miscellaneous.BluetoothReconnectEnabled,
                MaxAttempts: BluetoothReconnectRuntimeConfig.MaxAttempts,
                AttemptTimeoutMs: BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                CooldownMs: BluetoothReconnectRuntimeConfig.CooldownMs,
                OnlyLikelyBluetoothEndpoints: BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints);
        }
    }
}
