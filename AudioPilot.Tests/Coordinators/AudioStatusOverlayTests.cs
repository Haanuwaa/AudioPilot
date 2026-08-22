using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Windows.Media.Control;

namespace AudioPilot.Tests.Coordinators;

public sealed class AudioStatusOverlayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedAudioStatusIsDiscardedAfterSupersessionOrShutdown(bool shutdown)
    {
        var queue = new Queue<Action>();
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(queue.Enqueue, _ => presenter);
        int reads = 0;
        var coordinator = CreateCoordinator(overlay, () => new(
            new(AudioEndpointStatusKind.Available, "Speakers", ++reads, false), new(AudioEndpointStatusKind.NoDevice)));
        try
        {
            coordinator.ShowAudioStatus();
            if (shutdown) await coordinator.ShutdownAsync();
            else coordinator.ShowAudioStatus();
            while (queue.TryDequeue(out Action? action)) action();
            if (shutdown) Assert.Empty(presenter.AudioStatusSnapshots);
            else Assert.Equal(2, Assert.Single(presenter.AudioStatusSnapshots).Output.VolumePercent);
        }
        finally { await coordinator.ShutdownAsync(); }
    }

    [Fact]
    public async Task ShowAudioStatus_SupersedesAnOlderCurrentTrackCapture()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Old track", "Artist", null, "fixture", 1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var coordinator = new AppCliOverlayCoordinator(null!, overlay, new MediaOverlayCommandService(engine), Logger.Instance, () => new Settings(),
            audioStatusReader: () => new(new(AudioEndpointStatusKind.NoDevice), new(AudioEndpointStatusKind.NoDevice)));

        try
        {
            coordinator.ShowCurrentTrack();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            coordinator.ShowAudioStatus();
            release.SetResult();
            await TestExecutionGuards.WaitUntilAsync(() => !coordinator.IsMediaOverlayCaptureInFlightForTests, "Current track capture did not complete.");

            Assert.Single(presenter.AudioStatusSnapshots);
            Assert.Empty(presenter.MediaMessages);
            Assert.Equal(1, presenter.ShowCount);
        }
        finally
        {
            release.TrySetResult();
            await coordinator.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShowAudioStatus_NewerCurrentTrackRequestSupersedesItsResult(bool failRead)
    {
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(new MediaOverlaySessionSnapshot(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "New track", "Artist", null, "fixture", 1)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        AppCliOverlayCoordinator? coordinator = null;
        coordinator = new AppCliOverlayCoordinator(null!, overlay, new MediaOverlayCommandService(engine), Logger.Instance, () => new Settings(),
            audioStatusReader: () =>
            {
                coordinator!.ShowCurrentTrack();
                if (failRead) throw new InvalidOperationException("Fixture endpoint failure");
                return new(new(AudioEndpointStatusKind.NoDevice), new(AudioEndpointStatusKind.NoDevice));
            });

        try
        {
            coordinator.ShowAudioStatus();
            await TestExecutionGuards.WaitUntilAsync(() => !coordinator.IsMediaOverlayCaptureInFlightForTests, "Current track capture did not complete.");

            Assert.Empty(presenter.AudioStatusSnapshots);
            Assert.Equal("New track", Assert.Single(presenter.MediaMessages).title);
            Assert.Equal(1, presenter.ShowCount);
        }
        finally
        {
            await coordinator.ShutdownAsync();
        }
    }

    [Fact]
    public void ShowAudioStatus_ReadsFreshValuesAndCoalescesOverlappingRequests()
    {
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        AppCliOverlayCoordinator? coordinator = null;
        int reads = 0;
        coordinator = CreateCoordinator(overlay, () =>
        {
            reads++;
            coordinator!.ShowAudioStatus();
            return new(new(AudioEndpointStatusKind.Available, "Speakers", reads, false), new(AudioEndpointStatusKind.NoDevice));
        });

        coordinator.ShowAudioStatus();
        coordinator.ShowAudioStatus();

        Assert.Equal(2, reads);
        Assert.Equal(2, presenter.ShowCount);
        Assert.Equal(1f, presenter.AudioStatusSnapshots[0].Output.VolumePercent);
        Assert.Equal(2f, presenter.AudioStatusSnapshots[1].Output.VolumePercent);
    }

    [Fact]
    public void ShowAudioStatus_DisabledOrDisposed_DoesNotReadOrCreatePresenter()
    {
        int reads = 0;
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter, initiallyEnabled: false);
        var coordinator = CreateCoordinator(overlay, () =>
        {
            reads++;
            return new(new(AudioEndpointStatusKind.NoDevice), new(AudioEndpointStatusKind.NoDevice));
        });

        coordinator.ShowAudioStatus();
        overlay.Dispose();
        coordinator.ShowAudioStatus();

        Assert.Equal(0, reads);
        Assert.Equal(0, presenter.DisposeCount);
        Assert.Equal(0, presenter.ShowCount);
    }

    [Fact]
    public void ShowAudioStatus_ReadFailure_IsUnavailableAndDoesNotPreventRetry()
    {
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        int reads = 0;
        var coordinator = CreateCoordinator(overlay, () =>
        {
            ObjectDisposedException.ThrowIf(++reads == 1, typeof(AudioDeviceService));

            return new(new(AudioEndpointStatusKind.NoDevice), new(AudioEndpointStatusKind.Available, "Mic", 75, true));
        });

        coordinator.ShowAudioStatus();
        coordinator.ShowAudioStatus();

        Assert.Equal(AudioEndpointStatusKind.Unavailable, presenter.AudioStatusSnapshots[0].Output.Kind);
        Assert.Null(presenter.AudioStatusSnapshots[0].Output.VolumePercent);
        Assert.Null(presenter.AudioStatusSnapshots[0].Input.Muted);
        Assert.Equal(AudioEndpointStatusKind.Available, presenter.AudioStatusSnapshots[1].Input.Kind);
    }

    [Fact]
    public void ShowAudioStatus_DisabledBeforeDispatch_DropsQueuedOverlay()
    {
        var queue = new Queue<Action>();
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(queue.Enqueue, _ => presenter);
        var coordinator = CreateCoordinator(overlay, () => new(new(AudioEndpointStatusKind.NoDevice), new(AudioEndpointStatusKind.NoDevice)));

        coordinator.ShowAudioStatus();
        overlay.UpdateEnabled(false);
        while (queue.TryDequeue(out Action? action))
        {
            action();
        }

        Assert.Equal(0, presenter.ShowCount);
        Assert.Empty(presenter.AudioStatusSnapshots);
    }

    [Fact]
    public async Task ShowAudioStatus_ShutdownDuringRead_DropsResultAndRejectsFurtherReads()
    {
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        AppCliOverlayCoordinator? coordinator = null;
        Task? shutdown = null;
        int reads = 0;
        coordinator = CreateCoordinator(overlay, () =>
        {
            reads++;
            shutdown = coordinator!.ShutdownAsync();
            return new(new(AudioEndpointStatusKind.NoDevice), new(AudioEndpointStatusKind.NoDevice));
        });

        coordinator.ShowAudioStatus();
        await shutdown!;
        coordinator.ShowAudioStatus();

        Assert.Equal(1, reads);
        Assert.Equal(0, presenter.ShowCount);
    }

    private static AppCliOverlayCoordinator CreateCoordinator(OverlayService overlay, Func<AudioStatusSnapshot> reader)
    {
        return new AppCliOverlayCoordinator(null!, overlay, new MediaOverlayCommandService(), Logger.Instance, () => new Settings(), audioStatusReader: reader);
    }
}
