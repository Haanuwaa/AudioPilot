using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed class MediaPlaybackCoordinatorTests
{
    [Fact]
    public async Task ExplicitPlaybackCommandsAreOrderedAndRecordedWithoutOverlays()
    {
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<bool>();
        var session = new FakeMediaPlaybackSession();
        session.Send = async playing =>
        {
            requests.Add(playing);
            if (playing)
            {
                submitted.TrySetResult();
                await completion.Task;
            }
            session.Status = FakeMediaPlaybackSession.Desired(playing);
            return true;
        };
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var history = new List<ExecutionHistoryEntry>();
        var coordinator = new AppCliOverlayCoordinator(null!, overlay, new MediaOverlayCommandService(), Logger.Instance, () => new Settings(),
            mediaHistoryRecorder: history.Add, mediaPlaybackService: session.CreateService());

        try
        {
            Task<MediaPlaybackResult> first = coordinator.MediaSetPlayingAsync(true);
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task<MediaPlaybackResult> second = coordinator.MediaSetPlayingAsync(false);
            Assert.False(second.IsCompleted);
            Assert.Equal([true], requests);
            completion.SetResult(true);

            MediaPlaybackResult[] results = await Task.WhenAll(first, second);

            Assert.All(results, result => Assert.True(result.Confirmed));
            Assert.Equal([true, false], requests);
            Assert.Equal(["media-play", "media-pause"], history.Select(item => item.Action));
            Assert.Equal(0, presenter.ShowCount);
        }
        finally
        {
            completion.TrySetResult(true);
            await coordinator.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ShutdownCancelsQueuedPlaybackWithoutSendingIt()
    {
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeMediaPlaybackSession { Send = _ => { submitted.TrySetResult(); return pending.Task; } };
        using var overlay = new OverlayService(action => action(), _ => new RecordingOverlayPresenter());
        var coordinator = new AppCliOverlayCoordinator(null!, overlay, new MediaOverlayCommandService(), Logger.Instance, () => new Settings(),
            mediaPlaybackService: session.CreateService());

        Task<MediaPlaybackResult> first = coordinator.MediaSetPlayingAsync(true);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task<MediaPlaybackResult> second = coordinator.MediaSetPlayingAsync(false);
        await coordinator.ShutdownAsync();
        pending.SetResult(true);

        Assert.Equal("media-playback-canceled", (await first).Code);
        Assert.Equal("media-playback-canceled", (await second).Code);
        Assert.Equal("media-playback-canceled", (await coordinator.MediaSetPlayingAsync(true)).Code);
        Assert.Equal(1, session.RequestCount);
    }
}
