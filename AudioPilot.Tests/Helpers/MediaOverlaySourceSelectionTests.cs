using AudioPilot.Constants;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlaySourceSelectionTests
{
    [Fact]
    public async Task GetCurrentMediaSnapshotAsync_WithoutCurrentSession_FallsBackToBestMaterializedSession()
    {
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                new(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                    "Paused Fixture",
                    "Fixture Artist",
                    null,
                    "fixture-paused",
                    10),
                new(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Playing Fixture",
                    "Fixture Artist",
                    "Fixture Album",
                    "fixture-playing",
                    22),
            }));

        MediaOverlaySessionSnapshot snapshot = await engine.GetCurrentMediaSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Playing Fixture", snapshot.Title);
        Assert.Equal("fixture-playing", snapshot.SourceAppUserModelId);
    }

    [Fact]
    public async Task GetCurrentMediaSnapshotAsync_ReturnsPausedTrack_WhenItIsTheBestAvailableSnapshot()
    {
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                new(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                    "Paused Fixture",
                    "Fixture Artist",
                    null,
                    "fixture-paused",
                    10),
            }));

        MediaOverlaySessionSnapshot snapshot = await engine.GetCurrentMediaSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, snapshot.PlaybackStatus);
        Assert.Equal("Paused Fixture", snapshot.Title);
    }

    [Fact]
    public async Task GetCurrentMediaSnapshotAsync_CancelsProviderAtCaptureDeadline()
    {
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MediaOverlaySessionSnapshot.Empty;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            timingProfile: MediaOverlayTestHarness.CreateTimingProfile(maxCaptureDurationMs: 100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.GetCurrentMediaSnapshotAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SelectPreferredSourceForSampling_PrefersPostCommandPlayingSource_WhenPolicyAllowsRetargeting()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
            baselineSource: "spotify",
            baselineUsable: true,
            postCommandCurrentSource: "youtube",
            postCommandCurrentIsUsableAndPlaying: true,
            commandTargetSource: null,
            stickySource: "twitch",
            recoveredSource: null,
            commandPolicy: new MediaOverlayCommandPolicy(
                PreferBaselineSourceForSampling: false,
                AllowSingleCandidateMetadataChangeFallback: false,
                AcceptUnchangedTrackFallback: true,
                AllowRecoveredSourceOverride: false));

        Assert.Equal("youtube", selected);
    }

    [Fact]
    public void SelectPreferredSourceForSampling_PrefersSticky_WhenTrackNavigationHasValidatedRetargetSource()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
            baselineSource: "spotify",
            baselineUsable: true,
            postCommandCurrentSource: "youtube",
            postCommandCurrentIsUsableAndPlaying: true,
            commandTargetSource: null,
            stickySource: "twitch",
            recoveredSource: null,
            commandPolicy: MediaOverlayCommandPolicy.For(MediaOverlayCommand.NextTrack));

        Assert.Equal("twitch", selected);
    }

    [Fact]
    public void SelectPreferredSourceForSampling_FallsBackToBaselineWhenPostCommandNotUsable()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
            baselineSource: "spotify",
            baselineUsable: true,
            postCommandCurrentSource: "youtube",
            postCommandCurrentIsUsableAndPlaying: false,
            commandTargetSource: null,
            stickySource: "twitch",
            recoveredSource: null,
            commandPolicy: MediaOverlayCommandPolicy.For(MediaOverlayCommand.PlayPause));

        Assert.Equal("spotify", selected);
    }

    [Fact]
    public void SelectPreferredSourceForSampling_UsesStickyWhenBaselineUnavailable()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
            baselineSource: null,
            baselineUsable: false,
            postCommandCurrentSource: null,
            postCommandCurrentIsUsableAndPlaying: false,
            commandTargetSource: null,
            stickySource: "youtube",
            recoveredSource: null,
            commandPolicy: MediaOverlayCommandPolicy.For(MediaOverlayCommand.PlayPause));

        Assert.Equal("youtube", selected);
    }

    [Fact]
    public void ValidateStickySourceForSampling_ReturnsSticky_WhenSeenInPreCommandSessions()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            42);
        var postCommandCurrent = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "youtube",
            1);
        var preCommandSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["twitch"] = new(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                "Stream",
                "Channel",
                null,
                "twitch",
                120),
        };

        string? validated = MediaOverlaySourceSelector.ValidateStickySourceForSampling(
            "twitch",
            baseline,
            postCommandCurrent,
            preCommandSnapshots);

        Assert.Equal("twitch", validated);
    }

    [Fact]
    public void ValidateStickySourceForSampling_ReturnsNull_WhenStickySourceIsUncorroborated()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            42);
        var postCommandCurrent = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "youtube",
            1);
        var preCommandSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["spotify"] = baseline,
            ["youtube"] = postCommandCurrent,
        };

        string? validated = MediaOverlaySourceSelector.ValidateStickySourceForSampling(
            "twitch",
            baseline,
            postCommandCurrent,
            preCommandSnapshots);

        Assert.Null(validated);
    }

    [Fact]
    public void ValidateStickySourceForPlayPauseSampling_ReturnsSticky_WhenSeenInPreCommandSessions()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            42);
        var preCommandSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = new(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                "Browser Audio",
                "Browser",
                null,
                "chrome",
                12),
        };

        string? validated = MediaOverlaySourceSelector.ValidateStickySourceForPlayPauseSampling(
            "chrome",
            baseline,
            preCommandSnapshots);

        Assert.Equal("chrome", validated);
    }

    [Fact]
    public void EvaluatePreferredSourceSingleCandidateTrace_EmitsFirstObservation_ThenSuppressesWithinThrottleWindow()
    {
        MediaOverlayPreferredSourceTraceLimiter traceLimiter = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var first = traceLimiter.Evaluate(
            "spotify",
            "sig-a",
            now,
            TimeSpan.FromSeconds(1));
        var second = traceLimiter.Evaluate(
            "spotify",
            "sig-a",
            now.AddMilliseconds(250),
            TimeSpan.FromSeconds(1));

        Assert.True(first.ShouldEmit);
        Assert.Equal(0, first.SuppressedRepeats);
        Assert.False(second.ShouldEmit);
        Assert.Equal(1, second.SuppressedRepeats);
    }

    [Fact]
    public void EvaluatePreferredSourceSingleCandidateTrace_EmitsAgainAfterThrottleWindow_WithSuppressedCount()
    {
        MediaOverlayPreferredSourceTraceLimiter traceLimiter = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = traceLimiter.Evaluate("spotify", "sig-a", now, TimeSpan.FromSeconds(1));
        _ = traceLimiter.Evaluate("spotify", "sig-a", now.AddMilliseconds(250), TimeSpan.FromSeconds(1));
        _ = traceLimiter.Evaluate("spotify", "sig-a", now.AddMilliseconds(500), TimeSpan.FromSeconds(1));

        var emittedLater = traceLimiter.Evaluate(
            "spotify",
            "sig-a",
            now.AddMilliseconds(1100),
            TimeSpan.FromSeconds(1));

        Assert.True(emittedLater.ShouldEmit);
        Assert.Equal(2, emittedLater.SuppressedRepeats);
    }

    [Fact]
    public void EvaluatePreferredSourceSingleCandidateTrace_ResetsSuppression_WhenSignatureChanges()
    {
        MediaOverlayPreferredSourceTraceLimiter traceLimiter = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = traceLimiter.Evaluate("spotify", "sig-a", now, TimeSpan.FromSeconds(1));
        _ = traceLimiter.Evaluate("spotify", "sig-a", now.AddMilliseconds(250), TimeSpan.FromSeconds(1));

        var changed = traceLimiter.Evaluate(
            "spotify",
            "sig-b",
            now.AddMilliseconds(300),
            TimeSpan.FromSeconds(1));

        Assert.True(changed.ShouldEmit);
        Assert.Equal(0, changed.SuppressedRepeats);
    }

    [Fact]
    public void EvaluatePreferredSourceSingleCandidateTrace_TreatsExpiredEntriesAsFreshObservations()
    {
        MediaOverlayPreferredSourceTraceLimiter traceLimiter = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = traceLimiter.Evaluate("spotify", "sig-a", now, TimeSpan.FromSeconds(1));
        _ = traceLimiter.Evaluate(
            "chrome",
            "sig-b",
            now.AddSeconds(AppConstants.Timing.MediaOverlayPreferredSourceSingleCandidateTraceRetentionSeconds + 1),
            TimeSpan.FromSeconds(1));
        PreferredSourceSingleCandidateLogDecision refreshed = traceLimiter.Evaluate(
            "spotify",
            "sig-a",
            now.AddSeconds(AppConstants.Timing.MediaOverlayPreferredSourceSingleCandidateTraceRetentionSeconds + 2),
            TimeSpan.FromSeconds(1));

        Assert.True(refreshed.ShouldEmit);
        Assert.Equal(0, refreshed.SuppressedRepeats);
    }

    [Fact]
    public void EvaluatePreferredSourceSingleCandidateTrace_EvictsOldestEntries_WhenCapacityIsExceeded()
    {
        MediaOverlayPreferredSourceTraceLimiter traceLimiter = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int index = 0; index < AppConstants.Limits.MaxMediaOverlayPreferredSourceSingleCandidateTraceEntries + 24; index++)
        {
            _ = traceLimiter.Evaluate(
                $"source-{index}",
                $"sig-{index}",
                now.AddMilliseconds(index),
                TimeSpan.FromSeconds(1));
        }

        PreferredSourceSingleCandidateLogDecision oldestReplayed = traceLimiter.Evaluate(
            "source-0",
            "sig-0",
            now.AddSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.True(oldestReplayed.ShouldEmit);
        Assert.Equal(0, oldestReplayed.SuppressedRepeats);
    }

    [Fact]
    public void RuntimeTuningConfig_MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs_ClampsRange()
    {
        int original = RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs;

        try
        {
            RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs = -5;
            Assert.Equal(0, RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs);

            RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs = 20000;
            Assert.Equal(10000, RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs);
        }
        finally
        {
            RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs = original;
        }
    }

    [Fact]
    public void IsCorroboratedSourceForSampling_ReturnsTrue_WhenPostCommandMatches_AndRequested()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            42);
        var postCommand = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "youtube",
            1);

        bool corroborated = MediaOverlaySourceSelector.IsCorroboratedSourceForSampling(
            "youtube",
            baseline,
            postCommand,
            new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase),
            includePostCommandMatch: true);

        Assert.True(corroborated);
    }

    [Fact]
    public void SelectPreferredSourceForSampling_ReturnsBaselineAsFinalFallback()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
            baselineSource: "spotify",
            baselineUsable: false,
            postCommandCurrentSource: null,
            postCommandCurrentIsUsableAndPlaying: false,
            commandTargetSource: null,
            stickySource: null,
            recoveredSource: null,
            commandPolicy: MediaOverlayCommandPolicy.For(MediaOverlayCommand.PlayPause));

        Assert.Equal("spotify", selected);
    }

    [Fact]
    public void SelectPreferredSourceForPlayPauseSampling_PrefersBaselineSource_OverStickySource()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForPlayPauseSampling(
            baselineSource: "spotify",
            stickySource: "chrome");

        Assert.Equal("spotify", selected);
    }

    [Fact]
    public void SelectPreferredSourceForSampling_PrefersRecoveredSource_ForTrackNavigation()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
            baselineSource: "chrome",
            baselineUsable: true,
            postCommandCurrentSource: "chrome",
            postCommandCurrentIsUsableAndPlaying: true,
            commandTargetSource: null,
            stickySource: "chrome",
            recoveredSource: "spotify",
            commandPolicy: MediaOverlayCommandPolicy.For(MediaOverlayCommand.NextTrack));

        Assert.Equal("spotify", selected);
    }

    [Fact]
    public void SelectPreferredSourceForSampling_PrefersCommandTarget_ForTrackNavigation()
    {
        string? selected = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
            baselineSource: "brave",
            baselineUsable: true,
            postCommandCurrentSource: "brave",
            postCommandCurrentIsUsableAndPlaying: true,
            commandTargetSource: "spotify",
            stickySource: "brave",
            recoveredSource: null,
            commandPolicy: MediaOverlayCommandPolicy.For(MediaOverlayCommand.NextTrack));

        Assert.Equal("spotify", selected);
    }

    [Fact]
    public void ResolveEffectiveBaselineForSampling_UsesRecoveredSourcePreCommandSnapshot_ForTrackNavigation()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Browser Track",
            "Browser Artist",
            null,
            "chrome",
            120);
        MediaOverlaySessionSnapshot spotifyPreCommand = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track B",
            "Spotify Artist",
            null,
            "spotify",
            84);
        var preCommandSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = baseline,
            ["spotify"] = spotifyPreCommand,
        };

        MediaOverlaySessionSnapshot effectiveBaseline = MediaOverlaySourceSelector.ResolveEffectiveBaselineForSampling(
            baseline,
            preferredSourceForCommand: "spotify",
            preCommandSnapshots,
            MediaOverlayCommandPolicy.For(MediaOverlayCommand.NextTrack));

        Assert.Equal("spotify", effectiveBaseline.SourceAppUserModelId);
        Assert.Equal("Spotify Track B", effectiveBaseline.Title);
    }
}
