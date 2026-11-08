using AudioPilot.Cli;

namespace AudioPilot.Tests.Platform;

public sealed class ProcessMonitorDiagnosticServiceTests
{
    [Fact]
    public async Task DeniedSubscriptionsDoNotLaunchAProcessAndReleaseTheSource()
    {
        var source = new Source { StartAction = () => throw new UnauthorizedAccessException("private details") };
        bool launched = false;
        ProcessMonitorDiagnosticResult result = await ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken, createSource: () => source, launchProcess: () => { launched = true; throw new InvalidOperationException(); });
        Assert.False(result.Success);
        Assert.Equal("access-denied", result.Outcome);
        Assert.False(launched);
        Assert.True(source.Disposed.Task.IsCompletedSuccessfully);
        Assert.DoesNotContain("private details", CliOutputFormatter.FormatProcessMonitorDiagnostic(result, true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmsOnlyTheProbeProcessAndDisposesItsHandle()
    {
        var source = new Source();
        var handle = new Handle();
        ProcessMonitorDiagnosticResult result = await ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken, createSource: () => source, launchProcess: () =>
            {
                _ = Task.Run(() => { source.Started(7); source.Stopped(7); source.Started(42); source.Stopped(42); }, TestContext.Current.CancellationToken);
                return new(42, handle);
            });
        Assert.True(result.Success);
        Assert.True(result.SubscriptionStarted);
        Assert.True(result.StartEventReceived);
        Assert.True(result.StopEventReceived);
        Assert.True(handle.Disposed);
        Assert.True(source.Disposed.Task.IsCompletedSuccessfully);
        string json = CliOutputFormatter.FormatProcessMonitorDiagnostic(result, true);
        Assert.DoesNotContain("processId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event-delivery-confirmed", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingStopEventReportsPartialDelivery()
    {
        var source = new Source();
        var handle = new Handle();
        var clock = new DeadlineClock();
        var startDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task nativeWork = Task.CompletedTask;
        Task<ProcessMonitorDiagnosticResult> test = ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken, createSource: () => source, launchProcess: () =>
            {
                _ = Task.Run(() => { source.Started(42); startDelivered.TrySetResult(); }, TestContext.Current.CancellationToken);
                return new(42, handle);
            }, timeout: TimeSpan.FromSeconds(1), timeProvider: clock, trackWork: task => nativeWork = task);
        try
        {
            await startDelivered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { clock.Expire(); }
        ProcessMonitorDiagnosticResult result = await test.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await nativeWork.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("event-delivery-timeout", result.Outcome);
        Assert.True(result.StartEventReceived);
        Assert.False(result.StopEventReceived);
        Assert.True(handle.Disposed);
    }

    [Fact]
    public async Task TimedOutSubscriptionCannotLaunchLaterAndEventuallyDisposes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { StartAction = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); } };
        var clock = new DeadlineClock();
        bool launched = false;
        Task nativeWork = Task.CompletedTask;
        Task<ProcessMonitorDiagnosticResult> test = ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken, createSource: () => source, launchProcess: () => { launched = true; return new(42, new Handle()); },
            timeout: TimeSpan.FromSeconds(1), timeProvider: clock, trackWork: task => nativeWork = task);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            clock.Expire();
            ProcessMonitorDiagnosticResult result = await test.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("subscription-timeout", result.Outcome);
            Assert.False(source.Disposed.Task.IsCompleted);
            Assert.False(nativeWork.IsCompleted);
            bool duplicateCreated = false;
            ProcessMonitorDiagnosticResult retry = await ProcessMonitorDiagnosticService.RunAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                createSource: () => { duplicateCreated = true; throw new InvalidOperationException("Duplicate subscription created"); });
            Assert.False(duplicateCreated);
            Assert.Equal("probe-busy", retry.Outcome);
            Assert.Contains("cleaning up", CliOutputFormatter.FormatProcessMonitorDiagnostic(retry, false), StringComparison.Ordinal);
        }
        finally { clock.Expire(); release.TrySetResult(); }
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await nativeWork.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(launched);
        ProcessMonitorDiagnosticResult next = await ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            createSource: () => new Source { StartAction = () => throw new UnauthorizedAccessException() });
        Assert.Equal("access-denied", next.Outcome);
    }

    [Fact]
    public async Task AlreadyCanceledCallerDoesNotCreateSubscriptions()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool created = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: canceled.Token, createSource: () => { created = true; return new Source(); }));
        Assert.False(created);
    }

    [Fact]
    public async Task CallerCancellationDuringSubscription_PropagatesAndReleasesTheSourceWithoutLaunching()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { StartAction = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); } };
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bool launched = false;
        Task nativeWork = Task.CompletedTask;
        Task<ProcessMonitorDiagnosticResult> test = ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: canceled.Token, createSource: () => source,
            launchProcess: () => { launched = true; return new(42, new Handle()); }, timeProvider: new DeadlineClock(), trackWork: task => nativeWork = task);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally { canceled.Cancel(); release.TrySetResult(); }
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await nativeWork.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(launched);
    }

    [Fact]
    public async Task LostSubscriptionIsReportedAndCleanedUp()
    {
        var source = new Source();
        var handle = new Handle();
        ProcessMonitorDiagnosticResult result = await ProcessMonitorDiagnosticService.RunAsync(
            cancellationToken: TestContext.Current.CancellationToken, createSource: () => source, launchProcess: () =>
            {
                _ = Task.Run(() => source.Fail(), TestContext.Current.CancellationToken);
                return new(42, handle);
            });
        Assert.Equal("subscription-interrupted", result.Outcome);
        Assert.False(result.Success);
        Assert.True(handle.Disposed);
        Assert.True(source.Disposed.Task.IsCompletedSuccessfully);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private DeadlineTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new(callback, state);
        internal void Expire() => _timer?.Fire();

        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            internal void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Handle : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Source : IRawProcessLifecycleEventSource, IProcessLifecycleFailureSource
    {
        public event Action<int>? ProcessStarted;
        public event Action<int>? ProcessStopped;
        public event Action<string>? MonitoringFailed;
        public bool IsRunning { get; private set; }
        internal Action? StartAction { get; init; }
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start() { StartAction?.Invoke(); IsRunning = true; }
        public void Stop() => IsRunning = false;
        public void Dispose() { IsRunning = false; Disposed.TrySetResult(); }
        internal void Started(int id) => ProcessStarted?.Invoke(id);
        internal void Stopped(int id) => ProcessStopped?.Invoke(id);
        internal void Fail() { IsRunning = false; MonitoringFailed?.Invoke("wmi-subscription-stopped-Failed"); }
    }
}
