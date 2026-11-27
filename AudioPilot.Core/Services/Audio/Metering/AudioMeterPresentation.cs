namespace AudioPilot.Services.Audio.Metering;

internal static class AudioMeterPresentation
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(16);
    internal const double FloorDb = -60;

    internal static double ToDb(double linear) => linear <= 0 || !double.IsFinite(linear)
        ? FloorDb : Math.Clamp(20 * Math.Log10(linear), FloorDb, 0);
    internal static double DbToPercent(double db) => Math.Clamp((db - FloorDb) / -FloorDb * 100, 0, 100);
    internal static double ToPercent(double linear) => DbToPercent(ToDb(linear));
}

// A short peak marker remains readable without slowing the live level. Decay is
// elapsed-time based, so delayed/repeated UI ticks do not change its speed.
internal sealed class AudioPeakEnvelope(long timestampFrequency)
{
    private readonly long _holdTicks = timestampFrequency / 10;
    private readonly double _decayPerTick = 90d / timestampFrequency;
    private double _heldDb = AudioMeterPresentation.FloorDb;
    private long _holdUntil, _lastUpdate;
    private bool _initialized;

    internal double Update(double db, long now)
    {
        if (!_initialized || db >= _heldDb)
        {
            _heldDb = db;
            _holdUntil = now + _holdTicks;
            _initialized = true;
        }
        else if (now > _holdUntil)
        {
            long elapsed = Math.Max(0, now - Math.Max(_holdUntil, _lastUpdate));
            _heldDb = Math.Max(db, _heldDb - elapsed * _decayPerTick);
        }
        _lastUpdate = now;
        return _heldDb;
    }

    internal void Reset()
    {
        _initialized = false;
        _heldDb = AudioMeterPresentation.FloorDb;
    }
}
