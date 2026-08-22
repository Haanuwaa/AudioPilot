using AudioPilot.Models;

namespace AudioPilot.Services.Configuration;

internal sealed class AdvancedTuningState
{
    private readonly Lock _sync = new();
    private AdvancedTuningSettings? _previous;

    public void Apply(AdvancedTuningSettings? settings)
    {
        var current = AdvancedTuningSettings.Clone(settings);
        lock (_sync)
        {
            if (_previous?.AutoSaveDebounceMs != current.AutoSaveDebounceMs)
                RuntimeTuningConfig.AutoSaveDebounceMs = current.AutoSaveDebounceMs;
            if (_previous?.OutputSwitchDebounceMs != current.OutputSwitchDebounceMs)
                RuntimeTuningConfig.OutputSwitchDebounceMs = current.OutputSwitchDebounceMs;
            if (_previous?.InputSwitchDebounceMs != current.InputSwitchDebounceMs)
                RuntimeTuningConfig.InputSwitchDebounceMs = current.InputSwitchDebounceMs;

            var bluetooth = current.BluetoothReconnect;
            var previousBluetooth = _previous?.BluetoothReconnect;
            if (previousBluetooth?.MaxAttempts != bluetooth.MaxAttempts)
                BluetoothReconnectRuntimeConfig.MaxAttempts = bluetooth.MaxAttempts;
            if (previousBluetooth?.AttemptTimeoutMs != bluetooth.AttemptTimeoutMs)
                BluetoothReconnectRuntimeConfig.AttemptTimeoutMs = bluetooth.AttemptTimeoutMs;
            if (previousBluetooth?.CooldownMs != bluetooth.CooldownMs)
                BluetoothReconnectRuntimeConfig.CooldownMs = bluetooth.CooldownMs;
            if (previousBluetooth?.OnlyLikelyBluetoothEndpoints != bluetooth.OnlyLikelyBluetoothEndpoints)
                BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints = bluetooth.OnlyLikelyBluetoothEndpoints;
            if (previousBluetooth?.SuccessStabilizeWindowMs != bluetooth.SuccessStabilizeWindowMs)
                RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs = bluetooth.SuccessStabilizeWindowMs;
            if (previousBluetooth?.CachedEndpointVisibilityProbeAttempts != bluetooth.CachedEndpointVisibilityProbeAttempts)
                RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts = bluetooth.CachedEndpointVisibilityProbeAttempts;
            if (previousBluetooth?.CachedEndpointVisibilityProbeDelayMs != bluetooth.CachedEndpointVisibilityProbeDelayMs)
                RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs = bluetooth.CachedEndpointVisibilityProbeDelayMs;

            var steam = current.SteamBigPicture;
            var previousSteam = _previous?.SteamBigPicture;
            if (previousSteam?.MonitorDebounceMs != steam.MonitorDebounceMs)
                RuntimeTuningConfig.SteamBigPictureMonitorDebounceMs = steam.MonitorDebounceMs;
            if (previousSteam?.ConfirmationDelayMs != steam.ConfirmationDelayMs)
                RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs = steam.ConfirmationDelayMs;
            _previous = current;
        }
    }
}
