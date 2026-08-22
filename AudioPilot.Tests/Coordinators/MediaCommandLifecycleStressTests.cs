using System.Collections.Concurrent;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class MediaCommandLifecycleStressTests
{
    private sealed class SeekSession(ConcurrentQueue<string> commands) : IMediaSeekSession
    {
        public bool IsPresent { get; set; } = true;
        public Task<bool> IsPresentAsync(CancellationToken cancellationToken) => Task.FromResult(IsPresent);
        public object Identity => this;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;
        public Task<MediaSeekTimeline> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(
            new MediaSeekTimeline(true, false, 1, TimeSpan.FromSeconds(100), TimeSpan.Zero, TimeSpan.FromHours(1),
                DateTimeOffset.UtcNow, new("Track", "Artist", "Album", 1)));
        public Task<bool> SeekAsync(long positionTicks)
        {
            commands.Enqueue("seek");
            Assert.InRange(positionTicks, 0, TimeSpan.FromHours(1).Ticks);
            if (Interlocked.Increment(ref _requests) != 1) return Task.FromResult(true);
            Entered.TrySetResult();
            return Release.Task;
        }
    }

    private static readonly string[] first = ["play-pause"];

    [StressFact]
    public async Task MixedBursts_BoundSeekQueue_PreserveOrder_AndDiscardSupersededOrShutdownFeedback()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        for (int cycle = 0; cycle < 24; cycle++)
        {
            var commands = new ConcurrentQueue<string>();
            var ui = new ConcurrentQueue<Action>();
            var presenter = new RecordingOverlayPresenter();
            using var overlay = new OverlayService(ui.Enqueue, _ => presenter);
            var session = new SeekSession(commands);
            var playEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new MediaOverlayEngine(
                currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
                snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
                sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
            var coordinator = new AppCliOverlayCoordinator(null!, overlay, new MediaOverlayCommandService(engine), Logger.Instance,
                () => new Settings(),
                mediaPlayPauseCommandCancellableAsync: async cancellation =>
                {
                    commands.Enqueue("play-pause");
                    playEntered.TrySetResult();
                    await releasePlay.Task.WaitAsync(cancellation);
                    return new(true, MediaKeyHelper.MediaCommandRouteKind.Delegate);
                },
                mediaNextTrackCommand: () => { commands.Enqueue("next"); return true; },
                mediaPreviousTrackCommand: () => { commands.Enqueue("previous"); return true; },
                mediaSeekService: new MediaSeekService(_ => Task.FromResult<IMediaSeekSession?>(session)),
                audioStatusReader: () => new(new(AudioEndpointStatusKind.NoDevice), new(AudioEndpointStatusKind.NoDevice)));
            try
            {
                coordinator.MediaPlayPause();
                await playEntered.Task.WaitAsync(token);
                Task<MediaSeekResult>[] seeks = [.. Enumerable.Range(0, 16).Select(index => coordinator.MediaSeekAsync(index % 2 != 0, 5, cancellationToken: TestContext.Current.CancellationToken))];
                for (int rejected = 0; rejected < 8; rejected++)
                    Assert.Equal("media-seek-busy", (await coordinator.MediaSeekAsync(false, 5, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(token)).Code);
                coordinator.MediaNextTrack();
                coordinator.MediaPreviousTrack();
                coordinator.ShowCurrentTrack();
                coordinator.ShowAudioStatus();
                Task tail = TestPrivateAccess.GetField<Task>(coordinator, "_mediaSendOrderTail");
                releasePlay.SetResult();
                await session.Entered.Task.WaitAsync(token);
                Assert.Equal(["play-pause", "seek"], [.. commands]);
                bool shutdown = cycle % 3 == 0;
                if (shutdown) await coordinator.ShutdownAsync().WaitAsync(token);
                session.Release.SetResult(true);
                MediaSeekResult[] results = await Task.WhenAll(seeks).WaitAsync(token);
                await tail.WaitAsync(token);
                if (shutdown)
                {
                    Assert.All(results, result => Assert.False(result.Success));
                    Assert.Equal(["play-pause", "seek"], [.. commands]);
                }
                else
                {
                    Assert.All(results, result => Assert.True(result.Success, result.Code));
                    Assert.Equal(first.Concat(Enumerable.Repeat("seek", 16)).Concat(["next", "previous"]), [.. commands]);
                }
                while (ui.TryDequeue(out Action? action)) action();
                Assert.Equal(shutdown ? 0 : 1, presenter.ShowCount);
                Assert.Equal(shutdown ? 0 : 1, presenter.AudioStatusSnapshots.Count);
                Assert.Empty(presenter.Messages);
                Assert.Empty(presenter.MediaMessages);
                Assert.Equal(0, TestPrivateAccess.GetField<int>(coordinator, "_queuedSeeks"));
            }
            finally
            {
                releasePlay.TrySetResult();
                session.Release.TrySetResult(true);
                await coordinator.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            Dictionary<int, Task> operations = TestPrivateAccess.GetField<Dictionary<int, Task>>(coordinator, "_mediaOperations");
            Lock operationsLock = TestPrivateAccess.GetField<Lock>(coordinator, "_mediaOperationsLock");
            await TestExecutionGuards.WaitUntilAsync(() =>
            {
                lock (operationsLock) return operations.Count == 0;
            }, "Completed media operations were not released after shutdown.", TimeSpan.FromSeconds(5));
        }
    }
}
