namespace AudioPilot.Tests.Helpers;

/// <summary>Advances deadline timers explicitly while retaining real bounded waits for test failures.</summary>
internal sealed class ManualTimeProvider : TimeProvider, IDisposable
{
    private readonly Lock _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly SemaphoreSlim _created = new(0);
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_sync) return _ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _created.Release();
        return timer;
    }

    internal async Task WaitForTimerAsync()
    {
        Assert.True(await _created.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    internal void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        long end = checked(GetTimestamp() + duration.Ticks);
        while (true)
        {
            ManualTimer? timer;
            lock (_sync)
            {
                timer = _timers.Where(item => item.Due <= end).MinBy(item => item.Due);
                if (timer == null) { _ticks = end; return; }
                _ticks = timer.Due;
                if (timer.Period > 0) timer.Due = checked(timer.Due + timer.Period);
                else _timers.Remove(timer);
            }
            timer.Fire();
        }
    }

    public void Dispose()
    {
        lock (_sync) _timers.Clear();
        _created.Dispose();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        internal long Due { get; set; }
        internal long Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (_disposed) return false;
                owner._timers.Remove(this);
                Period = period.Ticks;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = checked(owner._ticks + dueTime.Ticks);
                    owner._timers.Add(this);
                }
                return true;
            }
        }
        internal void Fire() => callback(state);
        public void Dispose() { lock (owner._sync) { _disposed = true; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
