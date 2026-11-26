namespace AudioPilot.Services.Audio;

/// <summary>Coalesces endpoint notifications for one waiter, with a timeout for missed notifications.</summary>
internal sealed class DeviceStateChangeSignal : IDisposable
{
    private readonly AudioDeviceService _audio;
    private readonly TimeProvider? _timeProvider;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _pending = new(0, 1);
    private bool _disposed;

    internal DeviceStateChangeSignal(AudioDeviceService audio, TimeProvider? timeProvider = null)
    {
        _audio = audio;
        _timeProvider = timeProvider;
        _audio.DeviceStateChanged += OnDeviceStateChanged;
    }

    internal Task<bool> WaitAsync(TimeSpan fallback, CancellationToken token)
        => _timeProvider is { } clock && clock != TimeProvider.System
            ? WaitWithClockAsync(fallback, clock, token)
            : _pending.WaitAsync(fallback, token);

    private async Task<bool> WaitWithClockAsync(TimeSpan fallback, TimeProvider clock, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(fallback, clock);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await _pending.WaitAsync(wait.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private void OnDeviceStateChanged()
    {
        lock (_gate)
        {
            if (!_disposed && _pending.CurrentCount == 0) _pending.Release();
        }
    }

    public void Dispose()
    {
        _audio.DeviceStateChanged -= OnDeviceStateChanged;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Dispose();
        }
    }
}
