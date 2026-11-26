namespace AudioPilot.Services.Audio.Metering;

internal sealed class AudioMeterControlCache(Func<(float Volume, bool Muted)> readNative, TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(64);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private (float Volume, bool Muted)? _latest;
    private long _timestamp;
    private long _readRevision;
    private long _revision;
    private bool _disconnected;

    // The meter worker owns the sample. Callbacks may arrive out of order on another thread.
    internal (float Volume, bool Muted)? Read()
    {
        if (Volatile.Read(ref _disconnected)) return null;
        long revision = Volatile.Read(ref _revision);
        if (_latest == null || _readRevision != revision || _clock.GetElapsedTime(_timestamp) >= RecheckInterval)
        {
            _latest = readNative();
            _timestamp = _clock.GetTimestamp();
            _readRevision = revision;
        }
        return Volatile.Read(ref _disconnected) ? null : _latest;
    }

    internal void Invalidate() => Interlocked.Increment(ref _revision);

    internal void Disconnect() => Volatile.Write(ref _disconnected, true);
}
