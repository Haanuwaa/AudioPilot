using System.Diagnostics;

namespace AudioPilot.Services.Bluetooth;

/// <summary>Observes endpoint readiness after reconnect, with coalesced device notifications and bounded polling.</summary>
internal static class BluetoothReconnectReadiness
{
    internal static async Task<bool> WaitAsync(
        Func<bool> isReady,
        BluetoothReconnectAttemptResult result,
        Action<Action> subscribe,
        Action<Action> unsubscribe,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task>? delayAsync = null,
        Func<long>? elapsedMilliseconds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Attempted) return false;
        int timeoutMs = result.ShouldWaitForReadiness
            ? RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs
            : RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs;
        var clock = Stopwatch.StartNew();
        elapsedMilliseconds ??= () => clock.ElapsedMilliseconds;
        using var signal = new SemaphoreSlim(0, 1);
        void OnDeviceChanged()
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
        subscribe(OnDeviceChanged);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isReady()) return true;
                int remainingMs = (int)Math.Max(0, timeoutMs - elapsedMilliseconds());
                if (remainingMs <= 0) return false;
                int intervalMs = Math.Min(remainingMs, Math.Max(1, RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs));
                if (delayAsync != null) await delayAsync(intervalMs, cancellationToken).ConfigureAwait(false);
                else await signal.WaitAsync(TimeSpan.FromMilliseconds(intervalMs), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { unsubscribe(OnDeviceChanged); }
    }
}
