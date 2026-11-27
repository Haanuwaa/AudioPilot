using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Tests.Services.Routines;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class RoutineRuntimeCoordinatorTests
{
    [Fact]
    public async Task PauseSnapshotReportsRemainingDeadlineAndClearsItOnResume()
    {
        using var time = new Helpers.ManualTimeProvider();
        await using var runtime = Create(new Monitor(), new SteamMonitor(), timeProvider: time);
        runtime.PauseAutomatic(TimeSpan.FromMinutes(15));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(10), runtime.CaptureWorkSnapshot().AutomaticResumeUtc);
        runtime.PauseAutomatic(null);
        Assert.Null(runtime.CaptureWorkSnapshot().AutomaticResumeUtc);
        runtime.PauseAutomatic(TimeSpan.FromMinutes(30));
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(30), runtime.CaptureWorkSnapshot().AutomaticResumeUtc);
        runtime.ResumeAutomatic();
        Assert.False(runtime.CaptureWorkSnapshot().AutomaticPaused);
        Assert.Null(runtime.CaptureWorkSnapshot().AutomaticResumeUtc);
        runtime.PauseAutomatic(TimeSpan.FromMinutes(5));
        runtime.BeginShutdown();
        Assert.Null(runtime.CaptureWorkSnapshot().AutomaticResumeUtc);
    }

    [Fact]
    public async Task TimedPauseResumesOnce_AndAnExpiredTimerCannotResumeItsReplacement()
    {
        var time = new ControlledTimeProvider();
        int resumes = 0;
        await using var runtime = Create(new Monitor(), new SteamMonitor(), onResumed: () => resumes++, timeProvider: time);
        runtime.PauseAutomatic(TimeSpan.FromMinutes(15));
        ControlledTimeProvider.ControlledTimer original = Assert.Single(time.Timers);
        runtime.PauseAutomatic(TimeSpan.FromMinutes(30));
        Assert.True(original.Disposed);
        original.FireQueuedCallback();
        Assert.True(runtime.AutomaticPaused);
        Assert.Equal(0, resumes);
        ControlledTimeProvider.ControlledTimer replacement = time.Timers[1];
        Assert.Equal(TimeSpan.FromMinutes(30), replacement.DueTime);
        replacement.FireQueuedCallback();
        Assert.False(runtime.AutomaticPaused);
        Assert.True(replacement.Disposed);
        replacement.FireQueuedCallback();
        Assert.Equal(1, resumes);
        runtime.PauseAutomatic(null);
        replacement.FireQueuedCallback();
        Assert.True(runtime.AutomaticPaused);
        runtime.ResumeAutomatic();
        Assert.Equal(2, resumes);
    }
    [Fact]
    public async Task PauseIsSessionOnly_AndDisposalCancelsItsTimer()
    {
        var time = new ControlledTimeProvider();
        int resumes = 0;
        await using var runtime = Create(new Monitor(), new SteamMonitor(), onResumed: () => resumes++, timeProvider: time);
        runtime.PauseAutomatic(TimeSpan.FromHours(1));
        Assert.True(runtime.CaptureWorkSnapshot().AutomaticPaused);
        long generation = runtime.AutomationGeneration;
        runtime.PauseAutomatic(null);
        Assert.True(runtime.AutomationGeneration > generation);
        runtime.ResumeAutomatic();
        Assert.False(runtime.AutomaticPaused);
        runtime.PauseAutomatic(TimeSpan.FromHours(1));
        ControlledTimeProvider.ControlledTimer timer = time.Timers[^1];
        runtime.BeginShutdown();
        Assert.True(timer.Disposed);
        timer.FireQueuedCallback();
        runtime.ResumeAutomatic();
        Assert.True(runtime.CaptureWorkSnapshot().Stopping);
        Assert.Equal(1, resumes);
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.PauseAutomatic(TimeSpan.Zero));
    }

    [Fact]
    public async Task ResumeNotificationFailure_CannotEscapeTheTimerCallbackOrLeaveTheRuntimePaused()
    {
        var time = new ControlledTimeProvider();
        await using var runtime = Create(new Monitor(), new SteamMonitor(), onResumed: () => throw new InvalidOperationException("Injected resume notification failure."), timeProvider: time);
        runtime.PauseAutomatic(TimeSpan.FromMinutes(15));
        Assert.Single(time.Timers).FireQueuedCallback();
        Assert.False(runtime.AutomaticPaused);
        runtime.PauseAutomatic(null);
        Assert.True(runtime.AutomaticPaused);
        runtime.ResumeAutomatic();
        Assert.False(runtime.AutomaticPaused);
    }
    [Fact]
    public async Task ApplyingSavedConfiguration_TracksAndInvalidatesOneShotTriggers()
    {
        var routine = new AudioRoutine
        {
            Id = "schedule",
            MasterVolumePercent = 30,
            Triggers = [new() { Kind = RoutineTriggerKind.Scheduled }]
        };
        await using var runtime = Create(new Monitor(), new SteamMonitor(), routine: routine);
        runtime.ApplyConfiguration();
        AudioRoutine trigger = Assert.Single(routine.ExpandAutomaticTriggers());
        Assert.True(await runtime.Lifetime.RunActivationAsync(trigger, 0, _ => Task.FromResult(true), cancellationToken: TestContext.Current.CancellationToken));
        routine.Enabled = false;
        runtime.ApplyConfiguration();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Lifetime.RunActivationAsync<bool>(trigger, 0,
            _ => throw new InvalidOperationException("Disabled trigger executed"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("disable", true)]
    [InlineData("change-target", true)]
    [InlineData("never-applied", false)]
    [InlineData("manual-output", false)]
    [InlineData("newer-route", false)]
    [InlineData("newer-route-removed", false)]
    public async Task ApplyingSavedConfiguration_RetiresStatefulRoutingWithoutDiscardingItsOwnership(string edit, bool resetOutput)
    {
        var routine = new AudioRoutine
        {
            Id = "route",
            TargetAppPath = @"C:\Apps\Player.exe",
            RouteToApplication = true,
            OutputDeviceId = "speakers",
            InputDeviceId = "mic",
            Triggers = [new() { Id = "launch", Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Launcher.exe" }],
            RestorePreviousAudioOnDeactivate = true,
        };
        await using var runtime = Create(new Monitor(), new SteamMonitor(), routine: routine);
        runtime.ApplyConfiguration();
        AudioRoutine trigger = Assert.Single(runtime.ApplicationRoutines);
        var lease = runtime.Lifetime.RegisterLease(trigger, 84, false, true, false, cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;
        runtime.Lifetime.Register(trigger, 42, null, routingLease: lease, cancellationToken: TestContext.Current.CancellationToken);
        if (edit == "change-target") routine.OutputDeviceId = "headphones";
        else routine.Enabled = false;
        var deactivation = Assert.Single(runtime.ApplyConfiguration());

        Assert.Empty(runtime.Lifetime.GetLeases());
        Assert.False(runtime.Lifetime.IsLeaseCurrent(lease));
        Assert.True(runtime.Lifetime.HasRoutingOwnership);
        // A native write that started before retirement can finish while teardown is waiting for the audio gate.
        if (edit != "never-applied") runtime.Lifetime.MarkLeaseProcessApplied(lease, 84, output: true);
        if (edit == "manual-output") runtime.Lifetime.MarkManualRouting(84, output: true, []);
        if (edit is "newer-route" or "newer-route-removed")
        {
            var newer = runtime.Lifetime.RegisterLease(new() { Id = "new", TargetAppPath = trigger.TargetAppPath, OutputDeviceId = "other", RouteToApplication = true },
                84, true, false, true, cancellationToken: TestContext.Current.CancellationToken, isManual: true)!.Value.Lease;
            if (edit == "newer-route-removed") runtime.Lifetime.RemoveLease(newer.LeaseKey);
        }

        await runtime.Lifetime.DeactivateAsync(deactivation, (session, restore) =>
        {
            Assert.True(restore);
            var (restoring, output, input) = Assert.Single(runtime.Lifetime.ReleaseSessionRouting(session, restore));
            Assert.Equal(resetOutput, output);
            Assert.True(input);
            runtime.Lifetime.CompleteRoutingRestoration(restoring);
            return Task.CompletedTask;
        });
        Assert.Equal(edit == "newer-route", runtime.Lifetime.HasRoutingOwnership);
    }

    [Fact]
    public async Task ShutdownDrainsSystemEventReadAndSkipsRemainingDirections()
    {
        using var clock = new Helpers.ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int inputReads = 0;
        int executions = 0;
        var routine = new AudioRoutine
        {
            OutputDeviceId = "speakers",
            InputDeviceId = "mic",
            Triggers = [new() { Kind = RoutineTriggerKind.SessionUnlock }],
        };
        await using var runtime = Create(new Monitor(), new SteamMonitor(), routine: routine, timeProvider: clock, getDevices: playback =>
        {
            if (playback)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            else Interlocked.Increment(ref inputReads);
            return [new() { Id = playback ? "speakers" : "mic" }];
        }, executeSystemEvent: (_, _) => { Interlocked.Increment(ref executions); return Task.CompletedTask; });
        runtime.SystemEvents.Unlock();
        Task drain;
        Task advance = Task.CompletedTask;
        try
        {
            await clock.WaitForTimerAsync();
            advance = Task.Run(() => clock.Advance(TimeSpan.FromMilliseconds(750)), TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            runtime.BeginShutdown();
            drain = runtime.DrainWorkAsync();
            Assert.False(drain.IsCompleted);
        }
        finally
        {
            release.Set();
            await advance;
        }
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, inputReads);
        Assert.Equal(0, executions);
    }
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
            bool discarded = false;
            Assert.False(runtime.QueueWork(_ => Task.CompletedTask, "late", onDiscarded: () => discarded = true));
            Assert.True(discarded);
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
        using var clock = new Helpers.ManualTimeProvider();
        await using var runtime = Create(new Monitor(), new SteamMonitor(), timeProvider: clock);
        try
        {
            RuntimeTuningConfig.MixerSessionRefreshDebounceMs = 1000;
            runtime.Lifetime.RegisterLease(new AudioRoutine { Id = "route", RouteToApplication = true, TargetAppPath = @"C:\Apps\Player.exe", OutputDeviceId = "output" }, 42, false, false, false, cancellationToken: TestContext.Current.CancellationToken);
            var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int refreshes = 0;
            Task Refresh(string _, int signals, CancellationToken token) { refreshes++; completed.TrySetResult(signals); return Task.CompletedTask; }
            runtime.QueueRoutingRefresh(Refresh);
            await clock.WaitForTimerAsync();
            runtime.QueueRoutingRefresh(Refresh);
            await clock.WaitForTimerAsync();
            Assert.False(completed.Task.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1000));
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

    private static RoutineRuntimeCoordinator Create(Monitor process, SteamMonitor steam, Action<int>? onStarted = null, AudioRoutine? routine = null,
        Action? onResumed = null, TimeProvider? timeProvider = null, Func<bool, IReadOnlyList<CycleDevice>>? getDevices = null,
        Func<AudioRoutine, CancellationToken, Task>? executeSystemEvent = null) => new([], new(
        () => routine == null ? [] : [routine], getDevices ?? (_ => []), (_, _) => { }, (_, _) => { }, (_, _, _) => Task.CompletedTask,
        (_, _) => Task.CompletedTask, _ => { }, _ => false, onStarted ?? (_ => { }), _ => { }, _ => { },
        executeSystemEvent ?? ((_, _) => Task.CompletedTask), onResumed), Logger.Instance, processMonitor: process, steamMonitor: steam, timeProvider: timeProvider, shutdownToken: TestContext.Current.CancellationToken);

    private sealed class ControlledTimeProvider : TimeProvider
    {
        internal List<ControlledTimer> Timers { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ControlledTimer(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }

        internal sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            internal TimeSpan DueTime { get; } = dueTime;
            internal bool Disposed { get; private set; }
            internal void FireQueuedCallback() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

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
