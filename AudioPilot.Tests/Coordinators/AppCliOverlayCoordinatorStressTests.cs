using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Windows.Media.Control;

namespace AudioPilot.Tests.Coordinators;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class AppCliOverlayCoordinatorStressTests
{
    [StressFact]
    public async Task MediaNextTrack_RepeatedStaleCaptureCycles_CoalesceTrailingOverlayWithoutGettingStuck_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(MediaNextTrack_RepeatedStaleCaptureCycles_CoalesceTrailingOverlayWithoutGettingStuck_WhenStressEnabled)))
        {
            return;
        }

        var presenter = new RecordingOverlayPresenter();
        int sendCount = 0;
        TaskCompletionSource? captureStarted = null;
        TaskCompletionSource? releaseCapture = null;
        int snapshotSequence = 0;

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                captureStarted?.TrySetResult();
                TaskCompletionSource? gate = Volatile.Read(ref releaseCapture);
                Assert.NotNull(gate);
                await gate!.Task.WaitAsync(token);

                int sequence = Interlocked.Increment(ref snapshotSequence);
                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    $"Track {sequence}",
                    $"Artist {sequence}",
                    null,
                    "spotify",
                    sequence);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Queued Track",
                    "Queued Artist",
                    null,
                    "spotify",
                    1),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            });

        const int cycles = 25;
        const int extraPressesPerCycle = 3;

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            coordinator.MediaNextTrack();
            await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout());

            for (int press = 0; press < extraPressesPerCycle; press++)
            {
                coordinator.MediaNextTrack();
            }

            Assert.True(coordinator.IsMediaOverlayCaptureInFlightForTests);

            releaseCapture.TrySetResult();

            await AssertEventuallyAsync(
                () => !coordinator.IsMediaOverlayCaptureInFlightForTests,
                GetMediaOverlayCaptureTimeout());

            int expectedCompletedCycles = cycle + 1;
            await AssertEventuallyAsync(
                () =>
                    presenter.MediaUpdateCount >= expectedCompletedCycles &&
                    presenter.MessageUpdateCount == 0,
                GetMediaOverlayCaptureTimeout());
        }

        Assert.Equal(cycles * (extraPressesPerCycle + 1), Volatile.Read(ref sendCount));
        Assert.True(presenter.ShowCount >= cycles);
        Assert.True(presenter.MediaUpdateCount >= cycles);
        Assert.Equal(0, presenter.MessageUpdateCount);
        Assert.Equal(0, presenter.ActionUpdateCount);
        Assert.Equal(0, presenter.DeviceUpdateCount);
        Assert.Equal(0, presenter.RoutineUpdateCount);
        Assert.Equal(0, presenter.RoutinePartialUpdateCount);
    }

    [StressFact]
    public async Task MixedMediaCommandBurst_WhenCaptureIsStale_CoalescesTrailingOverlayAcrossCommandTypes_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(MixedMediaCommandBurst_WhenCaptureIsStale_CoalescesTrailingOverlayAcrossCommandTypes_WhenStressEnabled)))
        {
            return;
        }

        var presenter = new RecordingOverlayPresenter();
        int sendCount = 0;
        TaskCompletionSource? captureStarted = null;
        TaskCompletionSource? releaseCapture = null;
        int snapshotSequence = 0;

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                captureStarted?.TrySetResult();
                TaskCompletionSource? gate = Volatile.Read(ref releaseCapture);
                Assert.NotNull(gate);
                await gate!.Task.WaitAsync(token);

                int sequence = Interlocked.Increment(ref snapshotSequence);
                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    $"Burst Track {sequence}",
                    $"Burst Artist {sequence}",
                    null,
                    "spotify",
                    sequence);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Burst Track",
                    "Burst Artist",
                    null,
                    "spotify",
                    1),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppCliOverlayCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaPlayPauseCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            },
            mediaNextTrackCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            },
            mediaPreviousTrackCommand: () =>
            {
                Interlocked.Increment(ref sendCount);
                return true;
            });

        const int cycles = 15;

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            coordinator.MediaNextTrack();
            await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout());

            coordinator.MediaPlayPause();
            coordinator.MediaPreviousTrack();
            coordinator.MediaNextTrack();
            coordinator.MediaPlayPause();

            Assert.True(coordinator.IsMediaOverlayCaptureInFlightForTests);

            releaseCapture.TrySetResult();

            await AssertEventuallyAsync(
                () => !coordinator.IsMediaOverlayCaptureInFlightForTests,
                GetMediaOverlayCaptureTimeout());

            int expectedCompletedCycles = cycle + 1;
            await AssertEventuallyAsync(
                () =>
                    presenter.MediaUpdateCount >= expectedCompletedCycles &&
                    presenter.MessageUpdateCount == 0,
                GetMediaOverlayCaptureTimeout());
        }

        Assert.Equal(cycles * 5, Volatile.Read(ref sendCount));
        Assert.True(presenter.ShowCount >= cycles);
        Assert.True(presenter.MediaUpdateCount >= cycles);
        Assert.Equal(0, presenter.MessageUpdateCount);
        Assert.Equal(0, presenter.ActionUpdateCount);
        Assert.Equal(0, presenter.DeviceUpdateCount);
        Assert.Equal(0, presenter.RoutineUpdateCount);
        Assert.Equal(0, presenter.RoutinePartialUpdateCount);
    }

    private static AppCliOverlayCoordinator CreateCoordinator(
        RecordingOverlayPresenter presenter,
        MediaOverlayCommandService mediaOverlayCommands,
        Func<bool>? mediaPlayPauseCommand = null,
        Func<bool>? mediaNextTrackCommand = null,
        Func<bool>? mediaPreviousTrackCommand = null)
    {
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        var overlay = new OverlayService(action => action(), _ => presenter);
        return new AppCliOverlayCoordinator(
            audio,
            overlay,
            mediaOverlayCommands,
            Logger.Instance,
            () => new Settings(),
            mediaPlayPauseCommand,
            mediaNextTrackCommand,
            mediaPreviousTrackCommand);
    }

    private static TimeSpan GetMediaOverlayCaptureTimeout()
    {
        return TimeSpan.FromMilliseconds(AppConstants.MediaOverlay.MaxCaptureDurationMs + 1000);
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        await TestExecutionGuards.WaitUntilAsync(
            condition,
            "Timed out waiting for the overlay coordinator stress condition.",
            timeout);
    }
}
