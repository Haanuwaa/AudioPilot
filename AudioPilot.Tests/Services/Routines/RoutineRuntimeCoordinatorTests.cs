using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Tests.Services.Routines;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class RoutineRuntimeCoordinatorTests
{
    [Fact]
    public async Task ShutdownCancelsWorkAndWaitsBeforeDisposingMonitors()
    {
        var process = new Monitor();
        var steam = new SteamMonitor();
        await using var runtime = Create(process, steam);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(runtime.QueueWork(async token => { entered.TrySetResult(token); await release.Task; }, "blocked"));
        CancellationToken token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.CaptureWorkSnapshot().ActiveTasks);
        Assert.True(runtime.QueueDebounced(RoutineRuntimeWork.SteamConfirmation, 60000, _ => Task.CompletedTask, "pending"));
        Assert.Equal(["SteamConfirmation"], runtime.CaptureWorkSnapshot().DebounceSlots);
        Task disposal = runtime.DisposeAsync().AsTask();
        try
        {
            runtime.BeginShutdown();
            Assert.True(token.IsCancellationRequested);
            Assert.True(runtime.CaptureWorkSnapshot().Stopping);
            Assert.Empty(runtime.CaptureWorkSnapshot().DebounceSlots);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, process.Disposals);
            Assert.False(runtime.QueueWork(_ => Task.CompletedTask, "late"));
            Assert.False(runtime.QueueDebounced(RoutineRuntimeWork.RoutingRefresh, 0, _ => Task.CompletedTask, "late"));
        }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runtime.DisposeAsync();
        Assert.Equal(1, process.Disposals);
        Assert.Equal(1, steam.Disposals);
        Assert.Equal(0, steam.Starts);
    }

    [Fact]
    public async Task ReplacedWorkCannotRemoveTheNewPendingOwner()
    {
        await using var runtime = Create(new Monitor(), new SteamMonitor());
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(runtime.QueueDebounced(RoutineRuntimeWork.SteamSignal, 0, async _ => { firstEntered.SetResult(); await firstRelease.Task; }, "first"));
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(runtime.QueueDebounced(RoutineRuntimeWork.SteamSignal, 0, async _ => { secondEntered.SetResult(); await secondRelease.Task; }, "second"));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task[] firstSnapshot = runtime.SnapshotPendingTasks();
            firstRelease.TrySetResult();
            await Task.WhenAny(firstSnapshot).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(runtime.HasPendingWork(RoutineRuntimeWork.SteamSignal));
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); }
        await runtime.DrainWorkAsync();
        Assert.False(runtime.HasPendingWork(RoutineRuntimeWork.SteamSignal));
    }

    [Fact]
    public async Task PendingRefreshIsCanceledOnShutdown()
    {
        await using var runtime = Create(new Monitor(), new SteamMonitor());
        bool applied = false;
        Assert.True(runtime.QueueDebounced(RoutineRuntimeWork.RoutingRefresh, 60000, _ => { applied = true; return Task.CompletedTask; }, "pending"));
        runtime.BeginShutdown();
        await runtime.DrainWorkAsync();
        Assert.False(applied);
        Assert.False(runtime.HasPendingWork(RoutineRuntimeWork.RoutingRefresh));
    }

    [Fact]
    public async Task EmptyConfigurationDoesNotStartUnusedMonitorsAndLateEventsAreIgnored()
    {
        var process = new Monitor();
        var steam = new SteamMonitor();
        int delivered = 0;
        await using var runtime = Create(process, steam, onStarted: _ => delivered++);
        runtime.EnableMonitoring();
        runtime.ApplyConfiguration();
        runtime.UpdateAutomaticTriggers();
        runtime.UpdateProcessMonitoring();
        Assert.Equal(0, process.Starts);
        Assert.Equal(0, steam.Starts);
        Action<int>? stale = process.CapturedStarted;
        runtime.BeginShutdown();
        stale?.Invoke(42);
        Assert.Equal(0, delivered);
        Assert.Throws<ObjectDisposedException>(() => runtime.SystemEvents);
    }

    [Fact]
    public async Task RoutingSignalsAreCoalescedIntoOneRefresh()
    {
        int original = RuntimeTuningConfig.MixerSessionRefreshDebounceMs;
        await using var runtime = Create(new Monitor(), new SteamMonitor());
        try
        {
            RuntimeTuningConfig.MixerSessionRefreshDebounceMs = 1000;
            runtime.Lifetime.RegisterLease(new AudioRoutine { Id = "route", RouteToApplication = true, TargetAppPath = @"C:\Apps\Player.exe", OutputDeviceId = "output" }, 42, false, false, false, cancellationToken: TestContext.Current.CancellationToken);
            var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int refreshes = 0;
            Task Refresh(string _, int signals, CancellationToken token) { refreshes++; completed.TrySetResult(signals); return Task.CompletedTask; }
            runtime.QueueRoutingRefresh(Refresh);
            runtime.QueueRoutingRefresh(Refresh);
            Assert.Equal(2, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await runtime.DrainWorkAsync();
            Assert.Equal(1, refreshes);
        }
        finally { RuntimeTuningConfig.MixerSessionRefreshDebounceMs = original; }
    }

    [Fact]
    public async Task UnexpectedMonitorStopCanBeRetriedOnTheNextStateUpdate()
    {
        var process = new Monitor();
        await using var runtime = Create(process, new SteamMonitor(), routine: new AudioRoutine
        {
            Id = "launch",
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Apps\Player.exe",
            OutputDeviceId = "output",
        });
        runtime.EnableMonitoring();
        runtime.ApplyConfiguration();
        runtime.UpdateProcessMonitoring();
        Assert.Equal(1, process.Starts);
        process.Stop();
        runtime.UpdateProcessMonitoring();
        Assert.Equal(2, process.Starts);
        Assert.True(process.IsRunning);
    }

    [Fact]
    public async Task ShutdownCanStopAnExistingSteamMonitorWithoutCreatingAnUnusedOne()
    {
        var steam = new SteamMonitor();
        await using var runtime = Create(new Monitor(), steam);
        runtime.SteamMonitor.Start();
        runtime.BeginShutdown();
        Assert.Same(steam, runtime.SteamMonitor);
        runtime.SteamMonitor.Stop();
        Assert.False(steam.IsRunning);
        await using var unused = Create(new Monitor(), new SteamMonitor());
        unused.BeginShutdown();
        Assert.Throws<ObjectDisposedException>(() => unused.SteamMonitor);
    }

    private static RoutineRuntimeCoordinator Create(Monitor process, SteamMonitor steam, Action<int>? onStarted = null, AudioRoutine? routine = null) => new([], new(
        () => routine == null ? [] : [routine], _ => [], (_, _) => { }, (_, _) => { }, (_, _) => Task.CompletedTask,
        (_, _) => Task.CompletedTask, _ => { }, _ => false, onStarted ?? (_ => { }), _ => { }, _ => { },
        (_, _) => Task.CompletedTask), Logger.Instance, processMonitor: process, steamMonitor: steam, shutdownToken: TestContext.Current.CancellationToken);

    private sealed class Monitor : IProcessLifecycleMonitor
    {
        private Action<int>? _started;
        public event Action<int>? ProcessStarted { add { _started += value; CapturedStarted = _started; } remove { _started -= value; } }
        public event Action<int>? ProcessStopped { add { } remove { } }
        public bool IsRunning { get; private set; }
        internal Action<int>? CapturedStarted { get; private set; }
        internal int Starts { get; private set; }
        internal int Disposals { get; private set; }
        public ProcessLifecycleMonitorStartResult Start() { Starts++; IsRunning = true; return new(true, "active"); }
        public void Stop() => IsRunning = false;
        public void Dispose() { Disposals++; IsRunning = false; }
    }

    private sealed class SteamMonitor : ISteamBigPictureSignalMonitor
    {
        public event Action<SteamBigPictureSignal>? Signaled { add { } remove { } }
        public bool IsRunning { get; private set; }
        internal int Starts { get; private set; }
        internal int Disposals { get; private set; }
        public SteamBigPictureSignalMonitorStartResult Start() { Starts++; IsRunning = true; return new(true, "active"); }
        public void Stop() => IsRunning = false;
        public void Dispose() { Disposals++; IsRunning = false; }
    }
}
