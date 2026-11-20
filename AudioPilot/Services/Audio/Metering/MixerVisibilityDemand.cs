using AudioPilot.Models;

namespace AudioPilot.Services.Audio.Metering;

// Each visible presentation owns a lease. Closing one must not stop another.
// This does not own selection, routing expansion, volume, or microphone state.
internal sealed class MixerVisibilityDemand : IDisposable
{
    private readonly Lock _sync = new();
    private int _output;
    private int _input;
    private bool _disposed;
    private long _version;
    internal event Action? Changed;

    internal (AudioMeterModes Modes, long Version) Current
    {
        get { lock (_sync) return (GetModes(), _version); }
    }

    internal IDisposable Acquire(AudioMixerMode mode)
    {
        if (mode is not (AudioMixerMode.Output or AudioMixerMode.Input)) throw new ArgumentOutOfRangeException(nameof(mode));
        bool changed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AudioMeterModes before = GetModes();
            if (mode == AudioMixerMode.Output) _output++; else _input++;
            changed = before != GetModes();
            if (changed) _version++;
        }
        if (changed) Changed?.Invoke();
        return new Lease(this, mode);
    }

    private AudioMeterModes GetModes() => (_output > 0 ? AudioMeterModes.Output : AudioMeterModes.None)
        | (_input > 0 ? AudioMeterModes.Input : AudioMeterModes.None);

    private void Release(AudioMixerMode mode)
    {
        bool changed;
        lock (_sync)
        {
            if (_disposed) return;
            AudioMeterModes before = GetModes();
            if (mode == AudioMixerMode.Output) _output--; else _input--;
            changed = before != GetModes();
            if (changed) _version++;
        }
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _output = _input = 0;
            _version++;
        }
        Changed?.Invoke();
    }

    private sealed class Lease(MixerVisibilityDemand owner, AudioMixerMode mode) : IDisposable
    {
        private MixerVisibilityDemand? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(mode);
    }
}
