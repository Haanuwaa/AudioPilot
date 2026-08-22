namespace AudioPilot.Tests.Services.Bluetooth;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class BluetoothReconnectReadinessTests
{
    [Fact]
    public async Task AcceptedReconnect_WaitsForEndpointBeyondOriginalShortDelay()
    {
        long elapsed = 0;
        bool subscribed = false;
        bool ready = await BluetoothReconnectReadiness.WaitAsync(
            () => elapsed >= 600,
            new(true, false, 1, 0, RequestAccepted: true),
            _ => subscribed = true, _ => subscribed = false, TestContext.Current.CancellationToken,
            (delay, _) => { elapsed += delay; return Task.CompletedTask; }, () => elapsed);
        Assert.True(ready);
        Assert.True(elapsed >= 600);
        Assert.False(subscribed);
    }

    [Fact]
    public async Task RejectedReconnect_StopsAtShortDeadline()
    {
        long elapsed = 0;
        bool ready = await BluetoothReconnectReadiness.WaitAsync(() => false, new(true, false, 1, 0),
            _ => { }, _ => { }, TestContext.Current.CancellationToken,
            (delay, _) => { elapsed += delay; return Task.CompletedTask; }, () => elapsed);
        Assert.False(ready);
        Assert.Equal(RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs, elapsed);
    }

    [Fact]
    public async Task CancellationReleasesSubscriptionAndToleratesLateNotification()
    {
        using var cancellation = new CancellationTokenSource();
        Action? notification = null;
        bool subscribed = false;
        Task<bool> wait = BluetoothReconnectReadiness.WaitAsync(() => false, new(true, false, 1, 0, true),
            callback => { notification = callback; subscribed = true; },
            _ => subscribed = false, cancellation.Token);
        Assert.NotNull(notification);
        for (int index = 0; index < 500; index++) notification();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(subscribed);
        notification();
    }

    [Fact]
    public async Task NotificationWakesReadinessObservation()
    {
        bool ready = false;
        Action? notification = null;
        Task<bool> wait = BluetoothReconnectReadiness.WaitAsync(() => Volatile.Read(ref ready), new(true, false, 1, 0, true),
            callback => notification = callback, _ => { }, TestContext.Current.CancellationToken);
        Volatile.Write(ref ready, true);
        notification!();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnattemptedReconnect_DoesNotSubscribeOrReadDevices()
    {
        Assert.False(await BluetoothReconnectReadiness.WaitAsync(
            () => throw new InvalidOperationException("Unexpected device read"), default,
            _ => throw new InvalidOperationException("Unexpected subscription"), _ => { }, TestContext.Current.CancellationToken));
    }
}
