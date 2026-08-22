using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services;

public sealed class MediaSeekServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset NowUtc { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => NowUtc;
    }

    private sealed class Session : IMediaSeekSession
    {
        public object Identity { get; set; } = new();
        public MediaSeekTimeline Timeline { get; set; } = new(true, false, 1, TimeSpan.FromSeconds(30),
            TimeSpan.Zero, TimeSpan.FromSeconds(100), Now, new("Track", "Artist", "Album", 1));
        public List<long> Targets { get; } = [];
        public Func<Task<bool>> Send { get; set; } = () => Task.FromResult(true);
        public Task<MediaSeekTimeline> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Timeline);
        public Task<bool> SeekAsync(long positionTicks)
        {
            Targets.Add(positionTicks);
            return Send();
        }
    }

    [Theory]
    [InlineData(10, 40)]
    [InlineData(-10, 20)]
    [InlineData(3600, 100)]
    [InlineData(-3600, 0)]
    public async Task SeeksInTicksAndClampsToRange(int offset, double expected)
    {
        var session = new Session();
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());

        MediaSeekResult result = await service.SeekAsync(offset, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(expected, result.TargetPositionSeconds);
        Assert.Equal(30, result.PreviousPositionSeconds);
        Assert.Equal(TimeSpan.FromSeconds(expected).Ticks, Assert.Single(session.Targets));
    }

    [Theory]
    [InlineData(true, 2, 50)]
    [InlineData(false, 2, 40)]
    [InlineData(true, 0.5, 42.5)]
    public async Task ProjectsPlayingPositionUsingTimestampAndRate(bool playing, double rate, double expected)
    {
        var session = new Session();
        session.Timeline = session.Timeline with { IsPlaying = playing, PlaybackRate = rate, UpdatedAt = Now.AddSeconds(-5) };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());

        Assert.Equal(expected, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
    }

    [Fact]
    public async Task SeekFeedbackKeepsMetadataFromTheCommandedTrack()
    {
        var session = new Session();
        MediaSeekTrackIdentity? track = session.Timeline.TrackIdentity;
        session.Send = () =>
        {
            session.Timeline = session.Timeline with { TrackIdentity = new("Different song", "Other artist", "Album", 2) };
            return Task.FromResult(true);
        };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());

        MediaSeekResult result = await service.SeekAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(track, result.Track);
        Assert.Equal("0:30 → 0:40", result.PositionChangeText);
    }

    [Theory]
    [InlineData(3599, 10, "59:59 → 1:00:09")]
    [InlineData(3605, -10, "1:00:05 → 59:55")]
    [InlineData(90060, 10, "25:01:00 → 25:01:10")]
    public async Task SeekFeedbackFormatsLongMediaWithoutWrappingHours(int position, int offset, string expected)
    {
        var session = new Session();
        session.Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(position), Maximum = TimeSpan.FromDays(2) };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());

        Assert.Equal(expected, (await service.SeekAsync(offset, TestContext.Current.CancellationToken)).PositionChangeText);
    }

    [Fact]
    public async Task RepeatedPressesAccumulateAndDirectionCanReverseBeforeTimelineAcknowledgement()
    {
        var session = new Session();
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());

        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        await service.SeekAsync(-5, TestContext.Current.CancellationToken);

        Assert.Equal([40d, 50d, 45d], session.Targets.Select(t => TimeSpan.FromTicks(t).TotalSeconds));
    }

    [Theory]
    [InlineData("track")]
    [InlineData("session")]
    [InlineData("external-seek")]
    [InlineData("navigation")]
    [InlineData("range")]
    [InlineData("metadata-missing")]
    public async Task ChangedContextDiscardsUnacknowledgedPosition(string change)
    {
        var session = new Session();
        var clock = new Clock();
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), clock);
        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        switch (change)
        {
            case "track": session.Timeline = session.Timeline with { TrackIdentity = new("Other", "Artist", "Album", 2) }; break;
            case "session": session.Identity = new(); break;
            case "external-seek": session.Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(5), UpdatedAt = Now.AddMilliseconds(1) }; break;
            case "navigation": service.InvalidatePosition(); break;
            case "range": session.Timeline = session.Timeline with { Minimum = TimeSpan.FromSeconds(2) }; break;
            case "metadata-missing": session.Timeline = session.Timeline with { TrackIdentity = null }; break;
        }

        Assert.Equal(change == "external-seek" ? 15d : 40d, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
    }

    [Fact]
    public async Task RepeatedPressesDoNotExtendStalePositionLifetime()
    {
        var session = new Session();
        var clock = new Clock();
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), clock);
        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        clock.NowUtc = Now.AddSeconds(1);
        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        clock.NowUtc = Now.AddSeconds(2.1);

        Assert.Equal("media-seek-position-pending", (await service.SeekAsync(10, TestContext.Current.CancellationToken)).Code);
        Assert.Equal("media-seek-position-pending", (await service.SeekAsync(10, TestContext.Current.CancellationToken)).Code);
        Assert.Equal(2, session.Targets.Count);

        session.Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(50), UpdatedAt = clock.NowUtc };
        Assert.Equal(60, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
    }

    [Fact]
    public async Task RejectedFollowupPreservesLastAcceptedPosition()
    {
        var session = new Session();
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());
        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        session.Send = () => Task.FromResult(false);
        Assert.Equal("media-seek-rejected", (await service.SeekAsync(10, TestContext.Current.CancellationToken)).Code);
        session.Send = () => Task.FromResult(true);

        Assert.Equal(50, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
    }

    [Fact]
    public async Task AcknowledgedTimelineDoesNotDoubleCountPriorSeek()
    {
        var session = new Session();
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());
        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        session.Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(40), UpdatedAt = Now.AddMilliseconds(1) };

        Assert.Equal(50, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
    }

    [Theory]
    [InlineData(false, 0, 100)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 50, 20)]
    [InlineData(true, -1, 20)]
    public async Task UnsupportedOrInvalidTimelineNeverSends(bool enabled, int min, int max)
    {
        var session = new Session();
        session.Timeline = session.Timeline with { CanSeek = enabled, Minimum = TimeSpan.FromSeconds(min), Maximum = TimeSpan.FromSeconds(max) };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());

        Assert.Equal(enabled ? "media-seek-timeline-unavailable" : "media-seek-unavailable",
            (await service.SeekAsync(10, TestContext.Current.CancellationToken)).Code);
        Assert.Empty(session.Targets);
    }

    [Fact]
    public async Task MissingTimelineAfterAcceptedSeekIsDiagnosedAndFreshTimelineRestoresSeeking()
    {
        var session = new Session();
        var clock = new Clock();
        using var logs = TestLoggerScope.CreateInMemory("seek-timeline.log", LogLevel.Info);
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), clock, logs.Logger);
        await service.SeekAsync(10, TestContext.Current.CancellationToken);
        MediaSeekTimeline valid = session.Timeline;
        session.Timeline = valid with { Position = TimeSpan.Zero, Maximum = TimeSpan.Zero };

        MediaSeekResult missing = await service.SeekAsync(10, TestContext.Current.CancellationToken);

        Assert.False(missing.Success);
        Assert.Equal("media-seek-timeline-unavailable", missing.Code);
        Assert.Equal("Player timeline unavailable", missing.Message);
        Assert.Null(missing.TargetPositionSeconds);
        Assert.Single(session.Targets);
        clock.NowUtc = Now.AddSeconds(10);
        session.Timeline = valid with { Position = TimeSpan.FromSeconds(55), UpdatedAt = clock.NowUtc };
        Assert.Equal(65, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
        Assert.Equal([40d, 65d], session.Targets.Select(t => TimeSpan.FromTicks(t).TotalSeconds));
        string logText = logs.DisposeAndReadLogText();
        Assert.Contains("media-seek-timeline-unavailable", logText);
        Assert.Contains("maximumSeconds=0.000", logText);
        Assert.DoesNotContain("Artist", logText);
    }

    [Fact]
    public async Task MovingLiveWindowClampsBackwardSeekToItsEarliestAvailablePosition()
    {
        var session = new Session();
        session.Timeline = session.Timeline with { Minimum = TimeSpan.FromSeconds(25) };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());

        Assert.Equal(25, (await service.SeekAsync(-10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
        Assert.Equal("media-seek-limit", (await service.SeekAsync(-10, TestContext.Current.CancellationToken)).Code);
        Assert.Single(session.Targets);
    }

    [Fact]
    public async Task MissingSessionHasDistinctFeedback()
    {
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(null));
        Assert.Equal("media-seek-no-session", (await service.SeekAsync(10, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public async Task RejectionDoesNotAdvanceCachedPosition()
    {
        var session = new Session { Send = () => Task.FromResult(false) };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());
        Assert.Equal("media-seek-rejected", (await service.SeekAsync(10, TestContext.Current.CancellationToken)).Code);
        session.Send = () => Task.FromResult(true);
        Assert.Equal(40, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
    }

    [Fact]
    public async Task UncertainSubmissionBlocksAdditionalSeekUntilOriginalCompletes()
    {
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { Send = () => { submitted.SetResult(); return completion.Task; } };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());
        using var cancel = new CancellationTokenSource();
        Task<MediaSeekResult> first = service.SeekAsync(10, cancel.Token);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancel.Cancel();

        Assert.Equal("media-seek-unconfirmed", (await first).Code);
        Assert.Equal("media-seek-busy", (await service.SeekAsync(10, TestContext.Current.CancellationToken)).Code);
        Assert.Single(session.Targets);
        completion.SetResult(true);
        session.Send = () => Task.FromResult(true);
        Assert.True((await service.SeekAsync(10, TestContext.Current.CancellationToken)).Success);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotSendOrDiscardFirstAcceptedPosition()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { Send = () => completion.Task };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());
        Task<MediaSeekResult> first = service.SeekAsync(10, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        Task<MediaSeekResult> second = service.SeekAsync(10, cancel.Token);
        cancel.Cancel();
        Assert.Equal("media-seek-canceled", (await second).Code);
        completion.SetResult(true);
        await first;
        session.Send = () => Task.FromResult(true);

        Assert.Equal(50, (await service.SeekAsync(10, TestContext.Current.CancellationToken)).TargetPositionSeconds);
        Assert.Equal(2, session.Targets.Count);
    }

    [Fact]
    public async Task CoordinatorUsesSavedStepAndSuppressesSupersededOverlay()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { Send = () => { sent.TrySetResult(); return completion.Task; } };
        var service = new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock());
        var presenter = new RecordingOverlayPresenter();
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var settings = new Settings();
        settings.Hotkeys.Media.SeekStepSeconds = 15;
        var history = new List<ExecutionHistoryEntry>();
        var coordinator = new AppCliOverlayCoordinator(audio, overlay, new MediaOverlayCommandService(), Logger.Instance,
            () => settings, mediaHistoryRecorder: history.Add, mediaSeekService: service);
        try
        {
            Task<MediaSeekResult> first = coordinator.MediaSeekAsync(false, source: "hotkey");
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task<MediaSeekResult> second = coordinator.MediaSeekAsync(true, 5);
            completion.SetResult(true);
            await Task.WhenAll(first, second);

            Assert.Equal([45d, 40d], session.Targets.Select(t => TimeSpan.FromTicks(t).TotalSeconds));
            Assert.Equal(("Seek backward", "Track", "Artist", "0:45 → 0:40"), Assert.Single(presenter.MediaMessages));
            Assert.Equal(["hotkey", "cli"], history.Select(h => h.Source));
            Assert.All(history, h => Assert.True(h.Success));
        }
        finally { await coordinator.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoordinatorShowsPositionsWithoutMetadataButNeverImpliesARejectedSeekMoved(bool accepted)
    {
        var session = new Session { Send = () => Task.FromResult(accepted) };
        session.Timeline = session.Timeline with { TrackIdentity = null };
        var presenter = new RecordingOverlayPresenter();
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var coordinator = new AppCliOverlayCoordinator(audio, overlay, new MediaOverlayCommandService(), Logger.Instance,
            () => new Settings(), mediaSeekService: new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock()));
        try
        {
            await coordinator.MediaSeekAsync(false, 10);

            if (accepted)
            {
                Assert.Equal(("Seek forward", "Track information unavailable", null, "0:30 → 0:40"), Assert.Single(presenter.MediaMessages));
            }
            else
            {
                Assert.Empty(presenter.MediaMessages);
                Assert.Equal("Player could not seek", Assert.Single(presenter.Messages).header);
            }
        }
        finally { await coordinator.ShutdownAsync(); }
    }

    [Fact]
    public async Task ExpiredQueuedSeekDoesNotLetLaterCommandsOvertakeActiveTransport()
    {
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session();
        var presenter = new RecordingOverlayPresenter();
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        var coordinator = new AppCliOverlayCoordinator(audio, overlay, new MediaOverlayCommandService(engine), Logger.Instance,
            () => new Settings(), mediaPlayPauseCommandCancellableAsync: async cancellationToken =>
            {
                firstSent.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new(true, MediaKeyHelper.MediaCommandRouteKind.Delegate);
            }, mediaSeekService: new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock()));
        try
        {
            coordinator.MediaPlayPause();
            await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("media-seek-timeout", (await coordinator.MediaSeekAsync(false)).Code);
            Assert.False(TestPrivateAccess.GetField<Task>(coordinator, "_mediaSendOrderTail").IsCompleted);
            Task<MediaSeekResult> later = coordinator.MediaSeekAsync(false);
            Assert.Empty(session.Targets);
            release.TrySetResult();
            Assert.True((await later).Success);
            Assert.Single(session.Targets);
        }
        finally
        {
            release.TrySetResult();
            await coordinator.ShutdownAsync();
        }
    }

    [Fact]
    public async Task CoordinatorShutdownCancelsQueuedSeeksAndSuppressesLateFeedback()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { Send = () => { sent.TrySetResult(); return completion.Task; } };
        var presenter = new RecordingOverlayPresenter();
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var coordinator = new AppCliOverlayCoordinator(audio, overlay, new MediaOverlayCommandService(), Logger.Instance,
            () => new Settings(), mediaSeekService: new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session), new Clock()));
        Task<MediaSeekResult> first = coordinator.MediaSeekAsync(false);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task<MediaSeekResult> queued = coordinator.MediaSeekAsync(false);

        await coordinator.ShutdownAsync();
        completion.SetResult(true);
        await Task.WhenAll(first, queued);
        Assert.Single(session.Targets);
        Assert.Empty(presenter.Messages);
        Assert.Equal("media-seek-canceled", (await coordinator.MediaSeekAsync(false)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task InvalidOffsetsNeverRequestSession(int offset)
    {
        var service = new MediaSeekService(_ => throw new InvalidOperationException("Should not request session"));
        Assert.Equal("media-seek-invalid-step", (await service.SeekAsync(offset, TestContext.Current.CancellationToken)).Code);
    }
}
