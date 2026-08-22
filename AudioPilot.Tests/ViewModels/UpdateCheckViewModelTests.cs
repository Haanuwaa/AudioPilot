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
        int notifications = 0;
        var delays = new ControlledDelay();
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => { requests++; return Task.FromResult<PublishedRelease?>(null); }, delays.WaitAsync,
            notifyUpdate: (_, _) => { notifications++; return Task.FromResult(true); });
        model.Start();
        model.Start();
        Assert.False(model.IsUpdateAvailable);
        Assert.Equal(string.Empty, model.UpdateMessage);
        Assert.Equal(0, requests);
        Assert.Equal(0, notifications);
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
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var notified = new List<Version>();
        bool notifiedOnDispatcher = false;
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => Task.FromResult<PublishedRelease?>(release), delays.WaitAsync, new Version(current), preview,
            notifyUpdate: (version, _) =>
            {
                notifiedOnDispatcher = dispatcher.CheckAccess();
                notified.Add(version);
                return Task.FromResult(true);
            });
        model.SetEnabled(true);
        Assert.False(delays.HasPending);
        model.Start();
        DelayGate initial = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), initial.Duration);
        initial.Complete();
        DelayGate periodic = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromDays(1), periodic.Duration);
        Assert.Equal(expected, model.IsUpdateAvailable);
        Assert.Equal(expected ? [release.Version] : Array.Empty<Version>(), notified);
        Assert.False(notifiedOnDispatcher);
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
    public Task DisposingDuringNotification_CancelsAndJoinsDelivery() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = new ControlledDelay();
        var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => Task.FromResult<PublishedRelease?>(Release("99.0.0")), delays.WaitAsync,
            notifyUpdate: async (_, token) =>
            {
                entered.SetResult(token);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { stopped.SetResult(); }
                return true;
            });
        try
        {
            model.Start();
            model.SetEnabled(true);
            (await delays.NextAsync()).Complete();
            CancellationToken notificationToken = await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await model.DisposeAsync();
            Assert.True(notificationToken.IsCancellationRequested);
            Assert.True(stopped.Task.IsCompletedSuccessfully);
            Assert.False(model.IsUpdateAvailable);
            Assert.False(delays.HasPending);
        }
        finally { await model.DisposeAsync(); }
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
        clock.UtcNow = clock.UtcNow.AddDays(elapsedHours == 5 ? 1000 : -1000);
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

    [Fact]
    public Task CheckNow_BypassesStartupAndDailyWaits_AndRejectsOverlappingRequests() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<PublishedRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance, token =>
        {
            requests++;
            entered.TrySetResult();
            return requests == 1 ? result.Task.WaitAsync(token) : Task.FromResult<PublishedRelease?>(Release("2.0.0"));
        }, delays.WaitAsync, new Version(1, 0, 0), false);
        Assert.False(model.CheckNowCommand.CanExecute(null));
        model.SetEnabled(true);
        model.Start();
        Assert.Equal(TimeSpan.FromSeconds(30), (await delays.NextAsync()).Duration);
        Assert.Equal("Not checked yet.", model.CheckStatus);
        model.CheckNowCommand.Execute(null);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(model.CheckNowCommand.CanExecute(null));
        Assert.Equal("Checking for updates...", model.CheckStatus);
        model.CheckNowCommand.Execute(null);
        model.CheckNowCommand.Execute(null);
        result.SetResult(Release("1.0.0"));
        Assert.Equal(TimeSpan.FromDays(1), (await delays.NextAsync()).Duration);
        Assert.Equal(1, requests);
        Assert.Contains("up to date", model.CheckStatus);
        Assert.True(model.CheckNowCommand.CanExecute(null));
        model.CheckNowCommand.Execute(null);
        Assert.Equal(TimeSpan.FromDays(1), (await delays.NextAsync()).Duration);
        Assert.Equal(2, requests);
        Assert.Contains("update available", model.CheckStatus);
        model.SetEnabled(false);
        Assert.False(model.CheckNowCommand.CanExecute(null));
        model.CheckNowCommand.Execute(null);
        await model.DisposeAsync();
        Assert.Equal(2, requests);
    });

    [Fact]
    public Task ServerCooldown_BlocksCheckNowAndSurvivesReenable_WithoutUsingWallClock() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        var clock = new UpdateCheckClock();
        int requests = 0;
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => ++requests == 1
                ? Task.FromException<PublishedRelease?>(new ReleaseCheckException(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2)))
                : Task.FromResult<PublishedRelease?>(Release("1.0.0")), delays.WaitAsync, new Version(1, 0, 0), false, clock);
        model.Start();
        model.SetEnabled(true);
        (await delays.NextAsync()).Complete();
        Assert.Equal(TimeSpan.FromHours(2), (await delays.NextAsync()).Duration);
        Assert.Contains("Couldn't check", model.CheckStatus);
        Assert.Contains("Retry after", model.CheckNowHelp);
        Assert.False(model.CheckNowCommand.CanExecute(null));
        model.CheckNowCommand.Execute(null);
        Assert.Equal(1, requests);
        model.SetEnabled(false);
        clock.Elapsed = TimeSpan.FromMinutes(30);
        clock.UtcNow = clock.UtcNow.AddYears(10);
        model.SetEnabled(true);
        DelayGate remaining = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromMinutes(90), remaining.Duration);
        Assert.False(model.CheckNowCommand.CanExecute(null));
        clock.Elapsed = TimeSpan.FromHours(2);
        remaining.Complete();
        Assert.Equal(TimeSpan.FromDays(1), (await delays.NextAsync()).Duration);
        Assert.Equal(2, requests);
        Assert.True(model.CheckNowCommand.CanExecute(null));
        Assert.Contains("up to date", model.CheckStatus);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedNotification_RetriesWithoutAnotherReleaseRead_AndRemembersSuccess(bool throws) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var directory = new TestScopedDirectory(nameof(UpdateCheckViewModelTests));
        string path = Path.Combine(directory.Root, "update-notification.json");
        int reads = 0;
        int deliveries = 0;
        var notifications = new UpdateNotificationService(Logger.Instance, (_, _) => ++deliveries == 1
            ? throws ? Task.FromException<bool>(new InvalidOperationException("Tray fixture")) : Task.FromResult(false)
            : Task.FromResult(true), path);
        var delays = new ControlledDelay();
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => { reads++; return Task.FromResult<PublishedRelease?>(Release("2.0.0")); }, delays.WaitAsync,
            new Version(1, 0, 0), false, notifyUpdate: notifications.NotifyAsync);
        model.Start();
        model.SetEnabled(true);
        (await delays.NextAsync()).Complete();
        DelayGate retry = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromMinutes(1), retry.Duration);
        Assert.Equal(1, deliveries);
        Assert.False(File.Exists(path));
        Assert.True(model.IsUpdateAvailable);
        retry.Complete();
        Assert.Equal(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1), (await delays.NextAsync()).Duration);
        Assert.Equal(2, deliveries);
        Assert.Equal(1, reads);
        Assert.Equal("\"2.0.0\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(await notifications.NotifyAsync(new Version(2, 0, 0), TestContext.Current.CancellationToken));
        Assert.Equal(2, deliveries);
    });

    private static readonly int[] NotificationRetryMinutes = [1, 5, 30];

    [Fact]
    public Task FailedNotification_RetriesAreBounded_AndPendingRetryStopsOnDisable() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        int reads = 0;
        int deliveries = 0;
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => { reads++; return Task.FromResult<PublishedRelease?>(Release("2.0.0")); }, delays.WaitAsync,
            new Version(1, 0, 0), false, notifyUpdate: (_, _) => { deliveries++; return Task.FromResult(false); });
        model.Start();
        model.SetEnabled(true);
        (await delays.NextAsync()).Complete();
        foreach (int minutes in NotificationRetryMinutes)
        {
            DelayGate retry = await delays.NextAsync();
            Assert.Equal(TimeSpan.FromMinutes(minutes), retry.Duration);
            retry.Complete();
        }
        Assert.Equal(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(36), (await delays.NextAsync()).Duration);
        Assert.Equal(4, deliveries);
        Assert.Equal(1, reads);
        model.CheckNowCommand.Execute(null);
        Assert.Equal(TimeSpan.FromMinutes(1), (await delays.NextAsync()).Duration);
        Assert.Equal(5, deliveries);
        model.SetEnabled(false);
        await model.DisposeAsync();
        Assert.False(delays.HasPending);
        Assert.False(model.IsUpdateAvailable);
        Assert.Equal(5, deliveries);
        Assert.Equal(2, reads);
    });

    [Fact]
    public Task LongServerCooldown_UsesBoundedTimersWithoutSendingEarly() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var delays = new ControlledDelay();
        var clock = new UpdateCheckClock();
        int reads = 0;
        await using var model = new UpdateCheckViewModel(Dispatcher.CurrentDispatcher, Logger.Instance,
            _ => { reads++; return Task.FromException<PublishedRelease?>(new ReleaseCheckException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromDays(40))); },
            delays.WaitAsync, timeProvider: clock);
        model.Start();
        model.SetEnabled(true);
        (await delays.NextAsync()).Complete();
        DelayGate firstDay = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromDays(1), firstDay.Duration);
        clock.Elapsed = TimeSpan.FromDays(1);
        firstDay.Complete();
        Assert.Equal(TimeSpan.FromDays(1), (await delays.NextAsync()).Duration);
        Assert.Equal(1, reads);
        Assert.False(model.CheckNowCommand.CanExecute(null));
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
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
