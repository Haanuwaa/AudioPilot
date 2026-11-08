using AudioPilot.Constants;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed partial class MediaOverlayTrackNavigationRecoveryTests
{

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_UsesReducedInitialSettleDelay_ForSingleNonBrowserSource()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            32);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) => Task.FromResult(changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            snapshot => string.Equals(snapshot.Title, changed.Title, StringComparison.Ordinal),
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "spotify",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal(AppConstants.MediaOverlay.SingleSourceInitialSettleDelayMs, observedDelays[0]);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_UsesConfidentInitialSettleDelay_ForTrustedNonBrowserSource()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            32);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) => Task.FromResult(changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            source => string.Equals(source, "spotify", StringComparison.OrdinalIgnoreCase),
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "spotify",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal(AppConstants.MediaOverlay.ConfidentSourceInitialSettleDelayMs, observedDelays[0]);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_UsesReducedInitialSettleDelay_ForSingleBrowserSource()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            32);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) => Task.FromResult(changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            snapshot => string.Equals(snapshot.Title, changed.Title, StringComparison.Ordinal),
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal(AppConstants.MediaOverlay.BrowserSingleSourceInitialSettleDelayMs, observedDelays[0]);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_DoesNotUseConfidenceFastPath_ForBrowserSource()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            32);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) => Task.FromResult(changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => true,
            (_, _) => default,
            snapshot => string.Equals(snapshot.Title, changed.Title, StringComparison.Ordinal),
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal(AppConstants.MediaOverlay.BrowserSingleSourceInitialSettleDelayMs, observedDelays[0]);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_KeepsDefaultInitialSettleDelay_ForMultiSourceBrowserSampling()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            32);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) => Task.FromResult(changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            snapshot => string.Equals(snapshot.Title, changed.Title, StringComparison.Ordinal),
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
                ["spotify"] = new(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                    "Other Track",
                    "Other Artist",
                    null,
                    "spotify",
                    20),
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal(AppConstants.MediaOverlay.InitialSettleDelayMs, observedDelays[0]);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_UsesReducedUnchangedRecoveryCadence_ForSingleBrowserSource()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            2);
        MediaOverlaySessionSnapshot sameTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            0);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            0);
        Queue<MediaOverlaySessionSnapshot> snapshots = new(
        [
            sameTrack,
            sameTrack,
            changed,
        ]);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) => Task.FromResult(snapshots.Count > 0 ? snapshots.Dequeue() : changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            snapshot => string.Equals(snapshot.Title, changed.Title, StringComparison.Ordinal),
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                unchangedRecoveryAttempts: 2));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Track B", result.Snapshot.Title);
        Assert.Equal(
            [
                AppConstants.MediaOverlay.BrowserSingleSourceInitialSettleDelayMs,
                AppConstants.MediaOverlay.BrowserSingleSourceUnchangedRecoveryInitialDelayMs,
                AppConstants.MediaOverlay.BrowserSingleSourceUnchangedRecoveryRetryDelayMs,
            ],
            observedDelays);
    }

}

