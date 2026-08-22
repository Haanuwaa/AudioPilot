using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayTimingCompatibilityTests
{
    [Fact]
    public async Task SessionDropRecoveryRunner_DoesNotMarkExtendedTrackLoadUsed_WhenDeadlineSkipsRecovery()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: "Track A",
            Artist: "Artist",
            AlbumTitle: "Album",
            SourceAppUserModelId: "browser",
            PositionSeconds: 90);
        MediaOverlaySessionSnapshot reappearedWithoutMetadata = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Title: null,
            Artist: null,
            AlbumTitle: null,
            SourceAppUserModelId: "browser",
            PositionSeconds: 0);
        MediaOverlaySessionDropRecoveryRunner runner = MediaOverlayTestHarness.CreateSessionDropRecoveryRunner(
            (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty));

        MediaOverlaySessionDropRecoveryResult result = await runner.ResolveAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            reappearedWithoutMetadata,
            sawSessionDrop: true,
            "browser",
            commandSequence: 1,
            DateTimeOffset.UtcNow.AddMilliseconds(-1),
            nameof(SessionDropRecoveryRunner_DoesNotMarkExtendedTrackLoadUsed_WhenDeadlineSkipsRecovery),
            CancellationToken.None);

        Assert.False(result.UsedExtendedTrackLoadRecovery);
        Assert.True(result.EndedByDeadline);
    }

    [Fact]
    public async Task SessionDropRecoveryRunner_DefaultTimingProfile_UsesFifthExtendedTrackLoadAttempt()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot reappearedWithoutMetadata = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot changedTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);

        Queue<MediaOverlaySessionSnapshot> currentSnapshots = new(
        [
            reappearedWithoutMetadata,
            reappearedWithoutMetadata,
            reappearedWithoutMetadata,
            reappearedWithoutMetadata,
            changedTrack,
        ]);
        Lock gate = new();
        MediaOverlaySessionDropRecoveryRunner runner = MediaOverlayTestHarness.CreateSessionDropRecoveryRunner(
            (_, _, _) =>
            {
                lock (gate)
                {
                    return Task.FromResult(currentSnapshots.Count > 0 ? currentSnapshots.Dequeue() : changedTrack);
                }
            },
            timingProfile: null);

        MediaOverlaySessionDropRecoveryResult result = await runner.ResolveAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            reappearedWithoutMetadata,
            sawSessionDrop: true,
            "brave",
            commandSequence: 1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            nameof(SessionDropRecoveryRunner_DefaultTimingProfile_UsesFifthExtendedTrackLoadAttempt),
            CancellationToken.None);

        Assert.True(result.UsedExtendedTrackLoadRecovery);
        Assert.Equal("Track B", result.Snapshot.Title);
        Assert.Equal("Artist B", result.Snapshot.Artist);
    }

    [Fact]
    public async Task SessionDropRecoveryRunner_ShorterLegacyLikeTimingProfile_GivesUpBeforeFifthExtendedTrackLoadAttempt()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot reappearedWithoutMetadata = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            null,
            null,
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot changedTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "brave",
            0);

        Queue<MediaOverlaySessionSnapshot> currentSnapshots = new(
        [
            reappearedWithoutMetadata,
            reappearedWithoutMetadata,
            reappearedWithoutMetadata,
            reappearedWithoutMetadata,
            changedTrack,
        ]);
        Lock gate = new();
        MediaOverlayTimingProfile timingProfile = MediaOverlayTestHarness.CreateTimingProfile(
            sessionDropTrackLoadRecoveryAttempts: 4);
        MediaOverlaySessionDropRecoveryRunner runner = MediaOverlayTestHarness.CreateSessionDropRecoveryRunner(
            (_, _, _) =>
            {
                lock (gate)
                {
                    return Task.FromResult(currentSnapshots.Count > 0 ? currentSnapshots.Dequeue() : changedTrack);
                }
            },
            timingProfile: timingProfile);

        MediaOverlaySessionDropRecoveryResult result = await runner.ResolveAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            reappearedWithoutMetadata,
            sawSessionDrop: true,
            "brave",
            commandSequence: 1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            nameof(SessionDropRecoveryRunner_ShorterLegacyLikeTimingProfile_GivesUpBeforeFifthExtendedTrackLoadAttempt),
            CancellationToken.None);

        Assert.True(result.UsedExtendedTrackLoadRecovery);
        Assert.Null(result.Snapshot.Title);
        Assert.Null(result.Snapshot.Artist);
    }

    [Fact]
    public async Task SessionDropRecoveryRunner_ContinuesPolling_WhenObservedDropReturnsSameTrackBeforeChangedTrack()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "The Only Thing I Know for Real (Maniac Mix) | Metal Gear Rising: Revengeance (Soundtrack)",
            "Jamie Christopherson",
            null,
            "brave",
            550);
        MediaOverlaySessionSnapshot sameTrackFallback = baseline with { PositionSeconds = 550 };
        MediaOverlaySessionSnapshot changedTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Recovered Track",
            "SyntheticChannelIota",
            null,
            "brave",
            557);

        Queue<MediaOverlaySessionSnapshot> currentSnapshots = new(
        [
            sameTrackFallback,
            changedTrack,
        ]);
        Lock gate = new();
        MediaOverlaySessionDropRecoveryRunner runner = MediaOverlayTestHarness.CreateSessionDropRecoveryRunner(
            (_, _, _) =>
            {
                lock (gate)
                {
                    return Task.FromResult(currentSnapshots.Count > 0 ? currentSnapshots.Dequeue() : changedTrack);
                }
            },
            timingProfile: MediaOverlayTestHarness.CreateTimingProfile(
                sessionDropRecoveryAttempts: 2,
                sessionDropRecoveryInitialDelayMs: 1,
                sessionDropRecoveryRetryDelayMs: 1,
                sessionDropTrackLoadRecoveryAttempts: 1,
                sessionDropTrackLoadRecoveryInitialDelayMs: 1,
                sessionDropTrackLoadRecoveryRetryDelayMs: 1));

        MediaOverlaySessionDropRecoveryResult result = await runner.ResolveAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            sameTrackFallback,
            sawSessionDrop: true,
            "brave",
            commandSequence: 1,
            DateTimeOffset.UtcNow.AddMilliseconds(100),
            nameof(SessionDropRecoveryRunner_ContinuesPolling_WhenObservedDropReturnsSameTrackBeforeChangedTrack),
            CancellationToken.None);

        Assert.Equal("Synthetic Recovered Track", result.Snapshot.Title);
        Assert.Equal("SyntheticChannelIota", result.Snapshot.Artist);
        Assert.True(result.PollAttempts >= 2);
    }
}
