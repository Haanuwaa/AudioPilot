using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Internal;

public sealed class HotkeyDispatchCoordinatorTests
{
    [Fact]
    public async Task ExecuteCallback_DebouncesConcurrentDuplicateDeliveries()
    {
        long now = AppConstants.Timing.HotkeyDebounceTicks + 1;
        var coordinator = new HotkeyDispatchCoordinator(Logger.Instance, () => now);
        int callbackCount = 0;

        Task[] dispatches =
        [
            .. Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => coordinator.ExecuteCallback(10000, "media-next", () => Interlocked.Increment(ref callbackCount)))),
        ];

        await Task.WhenAll(dispatches);

        await TestExecutionGuards.WaitUntilAsync(
            () => Volatile.Read(ref callbackCount) > 0,
            "The accepted hotkey callback was not dispatched.");
        Assert.Equal(1, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public void ExecuteCallback_TrimsExpiredDebounceEntries()
    {
        long now = AppConstants.Timing.HotkeyDebounceTicks + 1;
        var coordinator = new HotkeyDispatchCoordinator(Logger.Instance, () => now);

        coordinator.ExecuteCallback(10000, "first", static () => { });
        now += AppConstants.Timing.HotkeyDebounceRetentionTicks + 1;
        coordinator.ExecuteCallback(10001, "second", static () => { });

        Assert.Equal(1, coordinator.DebounceTimestampCountForTests);
    }

    [Fact]
    public void ExecuteCallback_BoundsDebounceEntryCount()
    {
        long now = AppConstants.Timing.HotkeyDebounceTicks + 1;
        var coordinator = new HotkeyDispatchCoordinator(Logger.Instance, () => now);

        for (int hotkeyId = 10000; hotkeyId < 10000 + AppConstants.Limits.MaxHotkeyDebounceEntries + 64; hotkeyId++)
        {
            coordinator.ExecuteCallback(hotkeyId, $"hotkey-{hotkeyId}", static () => { });
            now += TimeSpan.TicksPerMillisecond;
        }

        Assert.True(coordinator.DebounceTimestampCountForTests <= AppConstants.Limits.MaxHotkeyDebounceEntries);
    }

    [Fact]
    public async Task ExecuteCallback_WhenInjectedTimestampMovesBackward_DoesNotSuppressIndefinitely()
    {
        long now = AppConstants.Timing.HotkeyDebounceTicks * 2;
        var coordinator = new HotkeyDispatchCoordinator(Logger.Instance, () => now);
        int callbackCount = 0;

        coordinator.ExecuteCallback(10000, "first", () => Interlocked.Increment(ref callbackCount));
        await TestExecutionGuards.WaitUntilAsync(
            () => Volatile.Read(ref callbackCount) == 1,
            "The first hotkey callback was not dispatched.");

        now = 1;
        coordinator.ExecuteCallback(10000, "after-clock-reset", () => Interlocked.Increment(ref callbackCount));

        await TestExecutionGuards.WaitUntilAsync(
            () => Volatile.Read(ref callbackCount) == 2,
            "The callback was not dispatched after the monotonic clock reset.");
    }

    [Fact]
    public void Reset_InvalidatesCallbackThatWasQueuedBeforeShutdown()
    {
        Action? queuedCallback = null;
        var coordinator = new HotkeyDispatchCoordinator(
            Logger.Instance,
            () => AppConstants.Timing.HotkeyDebounceTicks + 1,
            callback => queuedCallback = callback);
        int callbackCount = 0;

        coordinator.ExecuteCallback(10000, "queued", () => callbackCount++);
        coordinator.Reset();
        queuedCallback?.Invoke();

        Assert.Equal(0, callbackCount);
    }
}
