using System.Net;
using System.Threading.Channels;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Services.Updates;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class UpdateCheckViewModelTests
{
    [Fact]
    public Task DisabledByDefault_DoesNotScheduleOrRequest() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int requests = 0;
        var delays = new ControlledDelay();
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => { requests++; return Task.FromResult<PublishedRelease?>(null); }, delays.WaitAsync);
        model.Start();
        model.Start();
        Assert.False(model.IsUpdateAvailable);
        Assert.Equal(string.Empty, model.UpdateMessage);
        Assert.Equal(0, requests);
        Assert.False(delays.HasPending);
    });

    [Theory]
    [InlineData("1.0.0.0", "1.0.0", false, false)]
    [InlineData("1.10.0.0", "1.9.0", false, false)]
    [InlineData("1.9.0.0", "1.10.0", false, true)]
    [InlineData("1.0.0.0", "1.0.0", true, true)]
    public Task VersionComparison_IsNumericAndHandlesAssemblyRevisionAndPreview(string current, string latest, bool preview, bool expected) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        PublishedRelease release = Release(latest);
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => Task.FromResult<PublishedRelease?>(release), delays.WaitAsync, new Version(current), preview);
        model.SetEnabled(true);
        Assert.False(delays.HasPending);
        model.Start();
        DelayGate initial = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), initial.Duration);
        initial.Complete();
        DelayGate periodic = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromDays(1), periodic.Duration);
        Assert.Equal(expected, model.IsUpdateAvailable);
        Assert.Equal(release.Url, model.ReleaseUrl);
        if (expected) Assert.Contains($"→ {latest}", model.UpdateMessage);
        else Assert.Empty(model.UpdateMessage);
        model.SetEnabled(false);
        Assert.False(model.IsUpdateAvailable);
        Assert.Empty(model.UpdateMessage);
    });

    [Fact]
    public Task ApplyingEnable_ChecksImmediately_AndRepeatedSettingsSyncDoesNotRestartChecks() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        int requests = 0;
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => { requests++; return Task.FromResult<PublishedRelease?>(Release("2.0.0")); }, delays.WaitAsync);
        model.Start();
        model.SetEnabled(true);
        DelayGate initial = await delays.NextAsync();
        Assert.Equal(TimeSpan.Zero, initial.Duration);
        initial.Complete();
        await delays.NextAsync();
        model.SetEnabled(true);
        Assert.Equal(1, requests);
        Assert.False(delays.HasPending);
        model.SetEnabled(false);
        model.SetEnabled(true);
        DelayGate reenabled = await delays.NextAsync();
        Assert.True(reenabled.Duration > TimeSpan.FromHours(23));
        Assert.Equal(1, requests);
        Assert.True(model.IsUpdateAvailable);
    });

    [Fact]
    public Task DisablingInFlight_CancelsRequestAndDiscardsLateResult() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<PublishedRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance, token =>
        {
            entered.SetResult(token);
            return result.Task;
        }, delays.WaitAsync);
        try
        {
            model.Start();
            model.SetEnabled(true);
            (await delays.NextAsync()).Complete();
            CancellationToken requestToken = await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            model.SetEnabled(false);
            Assert.True(requestToken.IsCancellationRequested);
            result.SetResult(Release("99.0.0"));
            await model.DisposeAsync();
            Assert.False(model.IsUpdateAvailable);
            Assert.Empty(model.UpdateMessage);
            Assert.False(delays.HasPending);
        }
        finally
        {
            result.TrySetResult(null);
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task Enabling_DoesNotInitializeHttpOnTheUiThread() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var model = new UpdateCheckViewModel(dispatcher, Logger.Instance, _ =>
        {
            entered.SetResult(dispatcher.CheckAccess());
            return Task.FromResult<PublishedRelease?>(null);
        }, static (duration, token) => duration == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(Timeout.InfiniteTimeSpan, token));
        model.Start();
        model.SetEnabled(true);
        Assert.False(await entered.Task.WaitAsync(TestContext.Current.CancellationToken));
    });

    [Theory]
    [InlineData(5, 19)]
    [InlineData(25, 0)]
    public Task Reenabling_UsesElapsedTimeForCooldown_EvenWhenSystemClockChanges(int elapsedHours, int remainingHours) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var clock = new UpdateCheckClock();
        var delays = new ControlledDelay();
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => Task.FromResult<PublishedRelease?>(null), delays.WaitAsync, timeProvider: clock);
        model.Start();
        model.SetEnabled(true);
        (await delays.NextAsync()).Complete();
        Assert.Equal(TimeSpan.FromDays(1), (await delays.NextAsync()).Duration);
        model.SetEnabled(false);
        clock.Elapsed = TimeSpan.FromHours(elapsedHours);
        model.SetEnabled(true);
        Assert.Equal(TimeSpan.FromHours(remainingHours), (await delays.NextAsync()).Duration);
    });

    [Fact]
    public Task OfflineCheck_RetriesAfterAnHour_ThenResumesDailyChecks() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        int requests = 0;
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => ++requests == 1
                ? Task.FromException<PublishedRelease?>(new HttpRequestException("Rate limited", null, HttpStatusCode.Forbidden))
                : Task.FromResult<PublishedRelease?>(Release("2.0.0")), delays.WaitAsync);
        model.Start();
        model.SetEnabled(true);
        (await delays.NextAsync()).Complete();
        DelayGate retry = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromHours(1), retry.Duration);
        Assert.False(model.IsUpdateAvailable);
        retry.Complete();
        Assert.Equal(TimeSpan.FromDays(1), (await delays.NextAsync()).Duration);
        Assert.True(model.IsUpdateAvailable);
        Assert.Equal(2, requests);
    });

    private static PublishedRelease Release(string version) => new(new Version(version), new Uri($"https://github.com/Haanuwaa/AudioPilot/releases/tag/v{version}"));

    private sealed class ControlledDelay
    {
        private readonly Channel<DelayGate> _gates = Channel.CreateUnbounded<DelayGate>();
        internal bool HasPending => _gates.Reader.TryPeek(out _);

        internal Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gate = new DelayGate(duration);
            _gates.Writer.TryWrite(gate);
            return gate.Task.WaitAsync(cancellationToken);
        }

        internal async Task<DelayGate> NextAsync() => await _gates.Reader.ReadAsync(TestContext.Current.CancellationToken);
    }

    private sealed class DelayGate(TimeSpan duration)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan Duration { get; } = duration;
        internal Task Task => _completion.Task;
        internal void Complete() => _completion.SetResult();
    }

    private sealed class UpdateCheckClock : TimeProvider
    {
        internal TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Update cooldowns must not depend on the system clock.");
    }
}
