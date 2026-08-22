using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayCommandReliabilityTests
{
    [Fact]
    public async Task EventMonitoringFailure_PreservesTimedSamplingAndCancellation()
    {
        var engine = new MediaOverlayEngine();
        var cache = new MediaOverlayCommandSnapshotCache(_ =>
            Task.FromException<GlobalSystemMediaTransportControlsSessionManager>(new InvalidOperationException("Event monitoring unavailable")));
        TestPrivateAccess.SetField(engine, "_commandSnapshotCache", cache);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<MediaEventAssistOutcome> wait = TestPrivateAccess.InvokeNonPublicTask<MediaEventAssistOutcome>(
            engine, "WaitForRelevantMediaEventAsync", "FixturePlayer", 5_000, 0L, cancellation.Token);
        try
        {
            Assert.False(wait.IsCompleted, "Failed event monitoring must not bypass the sampling delay.");
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Theory]
    [InlineData(true, false, "media-command-sent-overlay-disabled")]
    [InlineData(false, false, "media-command-send-failed")]
    [InlineData(false, true, "media-command-unconfirmed")]
    public async Task DisabledOverlay_SkipsMetadataAndRecordsDelivery(bool accepted, bool uncertain, string code)
    {
        int reads = 0;
        var history = new TaskCompletionSource<ExecutionHistoryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(MediaOverlaySessionSnapshot.Empty);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        using var overlay = new OverlayService(action => action(),
            _ => throw new InvalidOperationException("Disabled overlay must not create a presenter"), initiallyEnabled: false);
        var coordinator = new AppCliOverlayCoordinator(null!, overlay,
            new MediaOverlayCommandService(engine), Logger.Instance, () => new Settings(),
            mediaNextTrackCommandDetailedAsync: () => Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(
                accepted, MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc, SuppressFallback: uncertain,
                FailureReason: uncertain ? "gsmc-command-timeout" : null)),
            mediaHistoryRecorder: entry => history.TrySetResult(entry));
        try
        {
            coordinator.MediaNextTrack();
            ExecutionHistoryEntry entry = await history.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref reads));
            Assert.Equal(accepted, entry.Success);
            Assert.Equal(code, entry.DiagCode);
            Assert.Equal("disabled", entry.Details?["overlayCapture"]);
            if (uncertain) Assert.Contains("unknown", entry.Summary);
        }
        finally
        {
            await coordinator.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MediaOverlayCommand.PlayPause)]
    [InlineData(MediaOverlayCommand.NextTrack)]
    [InlineData(MediaOverlayCommand.PreviousTrack)]
    public async Task CommandTargetAbsentFromSnapshots_DoesNotConfirmAnotherPlayer(MediaOverlayCommand command)
    {
        bool sent = false;
        var other = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Unrelated stream", null, null, "OtherPlayer", 12);
        MediaOverlaySessionSnapshot Current() => sent
            ? other with { PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing }
            : other;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (source, _, _) => Task.FromResult(source is null || source == other.SourceAppUserModelId
                ? Current() : MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>
            {
                [other.SourceAppUserModelId!] = Current(),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(command,
            () => { sent = true; return Task.FromResult(true); }, () => "CommandRecipient", TestContext.Current.CancellationToken);

        Assert.NotEqual("Unrelated stream", result.Overlay.Title);
        Assert.False(result.Overlay.IsTrackMessage);
    }

    [Theory]
    [InlineData(MediaOverlayCommand.PlayPause)]
    [InlineData(MediaOverlayCommand.NextTrack)]
    [InlineData(MediaOverlayCommand.PreviousTrack)]
    public async Task MetadataTimeoutBeforeSend_DoesNotDiscardCommand(MediaOverlayCommand command)
    {
        int sends = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return MediaOverlaySessionSnapshot.Empty;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            timingProfile: MediaOverlayTimingProfile.Default with { MaxCaptureDurationMs = 10 });

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(command,
            () => { sends++; return Task.FromResult(true); }, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, sends);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Overlay.Kind);
        Assert.Equal("media-overlay-timeout", result.DiagCode);
    }

    [Theory]
    [InlineData(MediaOverlayCommand.PlayPause, false)]
    [InlineData(MediaOverlayCommand.NextTrack, false)]
    [InlineData(MediaOverlayCommand.PreviousTrack, false)]
    [InlineData(MediaOverlayCommand.PlayPause, true)]
    [InlineData(MediaOverlayCommand.NextTrack, true)]
    [InlineData(MediaOverlayCommand.PreviousTrack, true)]
    public async Task CallerCancellation_DoesNotSendOrRetryCommand(MediaOverlayCommand command, bool afterSend)
    {
        int sends = 0;
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                if (!afterSend || sends > 0)
                {
                    reading.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return MediaOverlaySessionSnapshot.Empty;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        Task<MediaOverlayCommandResult> operation = engine.SendWithDetailedResultAsync(command,
            () => { sends++; return Task.FromResult(true); }, null, cancellation.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        MediaOverlayCommandResult result = await operation;

        Assert.Equal(afterSend ? 1 : 0, sends);
        Assert.Equal(MediaOverlayResultKind.Hidden, result.Overlay.Kind);
        Assert.Equal("media-overlay-canceled", result.DiagCode);
    }

    [Theory]
    [InlineData(MediaOverlayCommand.PlayPause)]
    [InlineData(MediaOverlayCommand.NextTrack)]
    [InlineData(MediaOverlayCommand.PreviousTrack)]
    public async Task BaselineReadFailure_StillSendsExactlyOnce(MediaOverlayCommand command)
    {
        int sends = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => sends == 0
                ? throw new InvalidOperationException("Metadata unavailable")
                : Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        await engine.SendWithDetailedResultAsync(command,
            () => { sends++; return Task.FromResult(true); }, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, sends);
    }
}
