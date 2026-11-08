using System.Collections.Concurrent;
using System.Reflection;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

public sealed class ProcessLifecycleMonitorRecoveryTests
{
    [Fact]
    public async Task SubscriptionFailureActivatesPollingAndReconcilesHandoff()
    {
        using var logger = new TestLoggerScope(nameof(ProcessLifecycleMonitorRecoveryTests), "recovery.log");
        var source = new FaultingSource();
        var primary = new EventDrivenProcessLifecycleMonitor(logger.Logger, () => source, () => [1, 2]);
        int fallbackCount = 0;
        using var monitor = new FallbackProcessLifecycleMonitor(logger.Logger, () => primary, () =>
        {
            Interlocked.Increment(ref fallbackCount);
            return new PollingProcessLifecycleMonitor(logger.Logger, TimeSpan.FromSeconds(30), () => [2, 3]);
        });
        var started = new ConcurrentQueue<int>();
        var stopped = new ConcurrentQueue<int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ProcessStarted += id => started.Enqueue(id);
        monitor.ProcessStopped += id => { stopped.Enqueue(id); completed.TrySetResult(); };
        Assert.True(monitor.Start().Success);
        Action<int> staleStart = TestPrivateAccess.GetField<Action<int>>(monitor, "_processStartedHandler");
        Action<int> staleStop = TestPrivateAccess.GetField<Action<int>>(monitor, "_processStoppedHandler");
        source.Fail();
        source.Fail();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("injected-subscription-loss", monitor.GetStatus().FallbackReason);
        Assert.Equal("polling", monitor.Mode);
        Assert.Equal(1, fallbackCount);
        staleStart(999);
        staleStop(2);
        Assert.Equal([3], [.. started]);
        Assert.Equal([1], [.. stopped]);
        Assert.True(source.Disposed);
        Assert.True(monitor.Start().Success);
        Assert.Equal(1, fallbackCount);
    }

    [Fact]
    public void ConcurrentStartReconcilesBeforeQueuedFailureRecovery()
    {
        var source = new FaultingSource();
        var primary = new EventDrivenProcessLifecycleMonitor(Logger.Instance, () => source, () => [1, 2]);
        int fallbackCount = 0;
        using var monitor = new FallbackProcessLifecycleMonitor(Logger.Instance, () => primary, () =>
        {
            fallbackCount++;
            return new PollingProcessLifecycleMonitor(Logger.Instance, TimeSpan.FromSeconds(30), () => [2, 3]);
        });
        List<int> started = [];
        List<int> stopped = [];
        monitor.ProcessStarted += started.Add;
        monitor.ProcessStopped += stopped.Add;
        Assert.True(monitor.Start().Success);
        using (TestPrivateAccess.GetField<Lock>(monitor, "_sync").EnterScope())
        {
            source.Fail();
            Assert.True(monitor.Start().Success);
        }
        Assert.Equal("polling", monitor.Mode);
        Assert.Equal(1, fallbackCount);
        Assert.Equal([3], started);
        Assert.Equal([1], stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntentionalStopOrDisposeDoesNotActivateFallback(bool dispose)
    {
        var source = new FaultingSource();
        int fallbackCount = 0;
        using var monitor = new FallbackProcessLifecycleMonitor(Logger.Instance,
            () => new EventDrivenProcessLifecycleMonitor(Logger.Instance, () => source, () => []),
            () => { fallbackCount++; return new PollingProcessLifecycleMonitor(Logger.Instance); });
        Assert.True(monitor.Start().Success);
        using (TestPrivateAccess.GetField<Lock>(monitor, "_sync").EnterScope())
        {
            long generation = TestPrivateAccess.GetField<long>(monitor, "_generation");
            source.Fail();
            if (dispose) monitor.Dispose(); else monitor.Stop();
            typeof(FallbackProcessLifecycleMonitor).GetMethod("RecoverFailedMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(monitor, [TestPrivateAccess.GetField<IProcessLifecycleMonitor?>(monitor, "_activeMonitor")!, "late-notification", generation]);
        }
        source.Fail();
        Assert.False(monitor.IsRunning);
        Assert.Equal(0, fallbackCount);
    }

    [Fact]
    public async Task FailedPollingRecoveryReportsInactiveAndCanRetry()
    {
        var source = new FaultingSource();
        var fallbackAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fail = true;
        using var monitor = new FallbackProcessLifecycleMonitor(Logger.Instance,
            () => new EventDrivenProcessLifecycleMonitor(Logger.Instance, () => source, () => []),
            () => new PollingProcessLifecycleMonitor(Logger.Instance, TimeSpan.FromSeconds(30), () =>
            {
                fallbackAttempted.TrySetResult();
                if (fail) throw new InvalidOperationException("Injected enumeration failure");
                return [];
            }));
        Assert.True(monitor.Start().Success);
        source.Fail();
        await fallbackAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(monitor.IsRunning);
        Assert.Equal("inactive", monitor.Mode);
        fail = false;
        Assert.True(monitor.Start().Success);
        Assert.Equal("polling", monitor.Mode);
    }

    private sealed class FaultingSource : IRawProcessLifecycleEventSource, IProcessLifecycleFailureSource
    {
        public event Action<int>? ProcessStarted { add { } remove { } }
        public event Action<int>? ProcessStopped { add { } remove { } }
        public event Action<string>? MonitoringFailed;
        public bool IsRunning { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() { Disposed = true; IsRunning = false; }
        public void Fail() { IsRunning = false; MonitoringFailed?.Invoke("injected-subscription-loss"); }
    }
}
