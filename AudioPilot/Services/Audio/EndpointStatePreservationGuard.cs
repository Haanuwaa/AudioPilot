using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio;

/// <summary>Guards delayed volume and mute preservation independently against newer endpoint changes.</summary>
internal sealed class EndpointStatePreservationGuard : IDisposable
{
    private readonly MMDevice _device;
    private readonly AudioEndpointVolume _volume;
    private readonly EndpointVolumePreservationState _state;
    private readonly EndpointMutePreservationState _muteState;
    private readonly Logger _logger;
    private readonly Lock _lifetimeLock = new();
    private bool _disposed;

    private EndpointStatePreservationGuard(MMDevice device, Logger logger)
    {
        _device = device;
        _logger = logger;
        _volume = device.AudioEndpointVolume;
        _state = new(_volume.MasterVolumeLevelScalar);
        _muteState = new(_volume.Mute);
        _volume.OnVolumeNotification += OnChanged;
    }

    internal static EndpointStatePreservationGuard? Capture(string deviceId, Logger logger)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            var guard = new EndpointStatePreservationGuard(device, logger);
            device = null;
            return guard;
        }
        catch (Exception ex)
        {
            logger.Warning("AudioDeviceService", "switch-state-guard-unavailable", nameof(Capture), ex);
            return null;
        }
        finally
        {
            try { device?.Dispose(); }
            catch (Exception ex) { logger.Warning("AudioDeviceService", "switch-state-guard-release-failed", nameof(Capture), ex); }
        }
    }

    private void OnChanged(AudioVolumeNotificationData data)
    {
        _state.Observe(data.MasterVolume);
        _muteState.Observe(data.Muted);
    }

    internal bool CanRestoreMute()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return false;
            try
            {
                bool allowed = _muteState.CanRestore(_volume.Mute);
                if (!allowed) _logger.Debug("AudioDeviceService", "switch-mute-preserve-skipped | reason=newer-mute-change");
                return allowed;
            }
            catch (Exception ex)
            {
                _logger.Debug("AudioDeviceService", $"switch-mute-preserve-skipped | reason=endpoint-unavailable error={ex.GetType().Name}");
                return false;
            }
        }
    }

    internal bool CanRestore()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return false;
            try
            {
                bool allowed = _state.CanRestore(_volume.MasterVolumeLevelScalar);
                if (!allowed) _logger.Debug("AudioDeviceService", "switch-volume-preserve-skipped | reason=newer-volume-change");
                return allowed;
            }
            catch (Exception ex)
            {
                _logger.Debug("AudioDeviceService", $"switch-volume-preserve-skipped | reason=endpoint-unavailable error={ex.GetType().Name}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _volume.OnVolumeNotification -= OnChanged; }
            catch (Exception ex) { _logger.Warning("AudioDeviceService", "switch-state-guard-detach-failed", nameof(Dispose), ex); }
            finally
            {
                try { _device.Dispose(); }
                catch (Exception ex) { _logger.Warning("AudioDeviceService", "switch-state-guard-release-failed", nameof(Dispose), ex); }
            }
        }
    }
}

internal sealed class EndpointVolumePreservationState(float initialVolume)
{
    private int _changed;

    internal void Observe(float volume)
    {
        if (!MatchesInitial(volume)) Interlocked.Exchange(ref _changed, 1);
    }

    internal bool CanRestore(float currentVolume) => Volatile.Read(ref _changed) == 0 && MatchesInitial(currentVolume);

    private bool MatchesInitial(float volume) => float.IsFinite(volume) && float.IsFinite(initialVolume)
        && Math.Abs(volume - initialVolume) <= .0001f;
}

internal sealed class EndpointMutePreservationState(bool initialMute)
{
    private int _changed;
    internal void Observe(bool muted)
    {
        if (muted != initialMute) Interlocked.Exchange(ref _changed, 1);
    }
    internal bool CanRestore(bool currentMute) => Volatile.Read(ref _changed) == 0 && currentMute == initialMute;
}
