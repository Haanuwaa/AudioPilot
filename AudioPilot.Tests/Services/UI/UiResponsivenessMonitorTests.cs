using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using Microsoft.Win32;

namespace AudioPilot.Tests.Services.UI;

public sealed class UiResponsivenessMonitorTests
{
    [Fact]
    public async Task SustainedStallOffersRecoveryOnceThenRearmsOnlyAfterAHeartbeat()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        var probes = new List<TaskCompletionSource>();
        var waits = new List<Task>();
        var durations = new List<TimeSpan>();
        await using var monitor = new UiResponsivenessMonitor(token =>
        {
            var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            probes.Add(probe);
            Task wait = probe.Task.WaitAsync(token);
            waits.Add(wait);
            return wait;
        }, () => false, (duration, _) => durations.Add(duration), logger, clock, queueRecovery: RunInline);
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 11; i++) await SampleAsync(clock);
        Assert.Equal(UiResponsivenessMonitor.StallThreshold, Assert.Single(durations));
        var item = Assert.Single(probes);
        for (int i = 0; i < 15; i++) await SampleAsync(clock);
        Assert.Single(durations);
        Assert.Single(probes);
        item.SetResult();
        await waits[0].WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await SampleAsync(clock);
        Assert.Equal(2, probes.Count);
        for (int i = 0; i < 10; i++) await SampleAsync(clock);
        Assert.Equal(2, durations.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LifecycleSuppressionOrMachinePauseDiscardsPendingProbeAndRestartsDeadline(bool lifecycleSuppression)
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        var resumeClock = new ResumeClock(clock);
        bool suppressed = false;
        int offers = 0;
        var tokens = new List<CancellationToken>();
        await using var monitor = new UiResponsivenessMonitor(token =>
        {
            tokens.Add(token);
            return new TaskCompletionSource().Task.WaitAsync(token);
        }, () => suppressed, (_, _) => offers++, logger, resumeClock, queueRecovery: RunInline);
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 9; i++) await SampleAsync(clock);
        suppressed = lifecycleSuppression;
        if (lifecycleSuppression) await SampleAsync(clock);
        else
        {
            resumeClock.PauseTicks = TimeSpan.FromMinutes(5).Ticks;
            await SampleAsync(clock);
        }
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.Equal(0, offers);
        suppressed = false;
        for (int i = 0; i < 10; i++) await SampleAsync(clock);
        Assert.Equal(0, offers);
        await SampleAsync(clock);
        Assert.Equal(1, offers);
    }

    [Fact]
    public async Task ShutdownCancelsBlockedHeartbeatWithoutWaitingOnUiOrLaunchingRecovery()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        CancellationToken postedToken = default;
        var monitor = new UiResponsivenessMonitor(token =>
        {
            postedToken = token;
            return new TaskCompletionSource().Task; // Even an uncooperative dispatcher adapter cannot delay shutdown.
        }, () => false, (_, _) => throw new InvalidOperationException(), logger, clock);
        monitor.Start();
        await clock.WaitForTimerAsync();
        await SampleAsync(clock);
        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(postedToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromHours(1));
    }

    private static async Task SampleAsync(ManualTimeProvider clock)
    {
        clock.Advance(UiResponsivenessMonitor.SampleInterval);
        await clock.WaitForTimerAsync();
    }

    private static Task RunInline(Action action, CancellationToken token)
    {
        try { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        catch (Exception error) { return Task.FromException(error); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitPowerEventsResetShortSleepEvenBetweenSamples(bool betweenSamples)
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        var resumeClock = new ResumeClock(clock);
        int offers = 0;
        var tokens = new List<CancellationToken>();
        await using var monitor = new UiResponsivenessMonitor(token =>
        {
            tokens.Add(token);
            return new TaskCompletionSource().Task.WaitAsync(token);
        }, () => false, (_, _) => offers++, logger, resumeClock, queueRecovery: RunInline);
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 8; i++) await SampleAsync(clock);
        monitor.NotifyPowerModeChanged(PowerModes.Suspend);
        if (!betweenSamples)
        {
            for (int i = 0; i < 15; i++) await SampleAsync(clock);
            Assert.Single(tokens);
            Assert.Equal(0, offers);
        }
        resumeClock.PauseTicks = TimeSpan.FromSeconds(7).Ticks;
        monitor.NotifyPowerModeChanged(PowerModes.Resume);
        await SampleAsync(clock);
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.Equal(0, offers);
        for (int i = 0; i < 10; i++) await SampleAsync(clock);
        Assert.Equal(0, offers);
        await SampleAsync(clock);
        Assert.Equal(1, offers);
    }

    [Fact]
    public async Task RecoveryLaunchRetriesAreSpacedBoundedAndStopAfterSuccess()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        var attemptTimes = new List<long>();
        await using var monitor = new UiResponsivenessMonitor(token => new TaskCompletionSource().Task.WaitAsync(token),
            () => false, (_, _) =>
            {
                attemptTimes.Add(clock.GetTimestamp());
                throw new InvalidOperationException("Synthetic launch failure");
            }, logger, clock, queueRecovery: RunInline);
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 80; i++) await SampleAsync(clock);
        Assert.Equal(UiResponsivenessMonitor.MaximumLaunchAttempts, attemptTimes.Count);
        for (int i = 1; i < attemptTimes.Count; i++)
            Assert.True(clock.GetElapsedTime(attemptTimes[i - 1], attemptTimes[i]) >= UiResponsivenessMonitor.LaunchRetryDelay);
    }

    [Fact]
    public async Task SuccessfulRetryDoesNotLaunchAgainDuringTheSameStall()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        int attempts = 0;
        await using var monitor = new UiResponsivenessMonitor(token => new TaskCompletionSource().Task.WaitAsync(token),
            () => false, (_, _) => { if (++attempts == 1) throw new InvalidOperationException("First launch fails"); },
            logger, clock, queueRecovery: RunInline);
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 80; i++) await SampleAsync(clock);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task BlockedLaunchCannotDelaySharedStopOrActAfterCancellation()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int launched = 0;
        var monitor = new UiResponsivenessMonitor(token => new TaskCompletionSource().Task.WaitAsync(token),
            () => false, (_, token) =>
            {
                try
                {
                    entered.TrySetResult(token);
                    release.Wait(TimeSpan.FromSeconds(15), token);
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref launched);
                }
                finally { ended.TrySetResult(); }
            }, logger, clock);
        try
        {
            monitor.Start();
            await clock.WaitForTimerAsync();
            for (int i = 0; i < 11; i++) await SampleAsync(clock);
            CancellationToken launchToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task first = monitor.DisposeAsync().AsTask();
            Task second = monitor.DisposeAsync().AsTask();
            Assert.Same(first, second);
            await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(launchToken.IsCancellationRequested);
            Assert.False(release.IsSet);
            Assert.Same(first, monitor.DisposeAsync().AsTask());
        }
        finally { release.Set(); await monitor.DisposeAsync(); }
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref launched));
    }

    [Fact]
    public async Task SubscriptionAndQueuedRecoveryAreReleasedWhenStopping()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        var subscription = new PowerSubscription();
        Action? queued = null;
        CancellationToken queuedToken = default;
        int offers = 0;
        var monitor = new UiResponsivenessMonitor(token => new TaskCompletionSource().Task.WaitAsync(token),
            () => false, (_, _) => offers++, logger, clock, _ => subscription,
            (action, token) => { queued = action; queuedToken = token; return new TaskCompletionSource().Task; });
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 11; i++) await SampleAsync(clock);
        Assert.NotNull(queued);
        await monitor.DisposeAsync();
        Assert.True(queuedToken.IsCancellationRequested);
        Assert.ThrowsAny<OperationCanceledException>(queued);
        Assert.Equal(0, offers);
        Assert.Equal(1, subscription.Disposals);
    }

    private sealed class PowerSubscription : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    [Fact]
    public async Task SuppressionAtQueuedLaunchResetsDeadlineWithoutConsumingTheRecoveryOffer()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        bool suppressed = false;
        int queuedCount = 0, offers = 0;
        Action? queued = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        await using var monitor = new UiResponsivenessMonitor(token =>
        {
            tokens.Add(token);
            return new TaskCompletionSource().Task.WaitAsync(token);
        }, () => suppressed, (_, _) => offers++, logger, clock, queueRecovery: (action, token) =>
        {
            if (++queuedCount > 1) return RunInline(action, token);
            queued = action;
            return completion.Task;
        });
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 11; i++) await SampleAsync(clock);
        suppressed = true;
        Assert.ThrowsAny<OperationCanceledException>(queued!);
        completion.SetCanceled(TestContext.Current.CancellationToken);
        suppressed = false; // The entire protected operation occurred between monitor samples.
        await SampleAsync(clock);
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.Equal(0, offers);
        for (int i = 0; i < 9; i++) await SampleAsync(clock);
        Assert.Equal(0, offers);
        await SampleAsync(clock);
        Assert.Equal(1, offers);
    }

    [Fact]
    public async Task RealDispatcherStallOffersRecoveryOffThreadAndAbortsTheQueuedHeartbeatOnShutdown()
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            using var clock = new ManualTimeProvider();
            using var logger = Logger.CreateInMemoryForTests();
            using var releaseUi = new ManualResetEventSlim();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int uiThread = Environment.CurrentManagedThreadId;
            int recoveryThread = 0;
            var recoveryOffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int executed = 0;
            DispatcherOperation? operation = null;
            var monitor = new UiResponsivenessMonitor(token =>
            {
                operation = dispatcher.InvokeAsync(() => executed++, DispatcherPriority.Input, token);
                return operation.Task;
            }, () => false, (_, _) => { recoveryThread = Environment.CurrentManagedThreadId; recoveryOffered.TrySetResult(); }, logger, clock);
            Task observe = Task.Run(async () =>
            {
                try
                {
                    monitor.Start();
                    await clock.WaitForTimerAsync();
                    for (int i = 0; i < 11; i++) await SampleAsync(clock);
                    await recoveryOffered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    Assert.NotEqual(0, recoveryThread);
                    Assert.NotEqual(uiThread, recoveryThread);
                    Assert.Equal(0, Volatile.Read(ref executed));
                    await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    Assert.Equal(DispatcherOperationStatus.Aborted, operation!.Status);
                }
                finally { releaseUi.Set(); }
            }, TestContext.Current.CancellationToken);
            bool released = releaseUi.Wait(TimeSpan.FromSeconds(15));
            await observe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(released);
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(0, executed);
        });
    }

    [Fact]
    public async Task FallbackPowerNotificationsAreIgnoredWhenDirectSubscriptionIsActive()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        Action<PowerModes>? directCallback = null;
        int offers = 0;
        await using var monitor = new UiResponsivenessMonitor(
            token => new TaskCompletionSource().Task.WaitAsync(token),
            () => false,
            (_, _) => offers++,
            logger,
            clock,
            subscribePowerChanges: callback => { directCallback = callback; return new PowerSubscription(); },
            queueRecovery: RunInline);
        monitor.Start();
        await clock.WaitForTimerAsync();
        Assert.NotNull(directCallback);
        directCallback!(PowerModes.Suspend);
        directCallback(PowerModes.Resume);
        // Simulate a stale Dispatcher-queued Suspend arriving after the direct native Resume.
        monitor.NotifyFallbackPowerModeChanged(PowerModes.Suspend);
        for (int i = 0; i < 12; i++) await SampleAsync(clock);
        Assert.Equal(1, offers);
    }

    [Fact]
    public async Task TransientHeartbeatFailureDoesNotStopMonitoring()
    {
        using var clock = new ManualTimeProvider();
        using var logger = Logger.CreateInMemoryForTests();
        int probes = 0;
        int offers = 0;
        await using var monitor = new UiResponsivenessMonitor(
            token => ++probes == 1
                ? Task.FromCanceled(new CancellationToken(canceled: true))
                : new TaskCompletionSource().Task.WaitAsync(token),
            () => false,
            (_, _) => offers++,
            logger,
            clock,
            queueRecovery: RunInline);
        monitor.Start();
        await clock.WaitForTimerAsync();
        for (int i = 0; i < 13; i++) await SampleAsync(clock);
        Assert.Equal(2, probes);
        Assert.Equal(1, offers);
    }

    private sealed class ResumeClock(ManualTimeProvider timers) : TimeProvider
    {
        internal long PauseTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timers.GetTimestamp() + Volatile.Read(ref PauseTicks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timers.CreateTimer(callback, state, dueTime, period);
    }
}
